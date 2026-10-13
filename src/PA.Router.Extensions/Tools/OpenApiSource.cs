using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Extensions.Tools;

/// <summary>
/// API descritta da un documento OpenAPI 3 (specifica 3.1.1): un'operazione, uno strumento (ADR-020).
///
/// Lo schema dei parametri dello strumento nasce dai parametri <c>path</c>, <c>query</c> e <c>header</c>
/// dell'operazione, piu' <c>body</c> per un corpo <c>application/json</c>; i riferimenti locali si risolvono.
/// L'indirizzo del server deve avere la stessa origine del documento: un documento che manda le chiamate altrove
/// non viene usato. Un parametro obbligatorio che il router non sa passare — un cookie, un'intestazione riservata,
/// un corpo non JSON — esclude l'operazione invece di offrirla monca.
/// </summary>
public sealed class OpenApiSource : IToolSource
{
    /// <summary>Tetto del documento e di una risposta.</summary>
    public const int MaxDocumentBytes = 4 * 1024 * 1024;

    private const int MaxSchemaDepth = 16;
    private const int MaxDescriptionChars = 4096;

    private static readonly string[] Methods = ["get", "post", "put", "patch", "delete"];

    /// <summary>
    /// Intestazioni che un parametro non puo' impostare: cambiano il metodo o la destinazione vista dal server, e
    /// trasformerebbero uno strumento di lettura in uno di scrittura o in un inoltro.
    /// </summary>
    private static readonly HashSet<string> ForbiddenParameterHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "x-http-method-override", "x-http-method", "x-method-override", "forwarded", "x-forwarded-for",
        "x-forwarded-host", "x-forwarded-proto", "x-forwarded-prefix", "x-original-url", "x-rewrite-url", "x-real-ip",
    };

    private readonly HttpClient client;
    private readonly Uri documentUrl;
    private readonly ToolCredential? credential;
    private readonly Dictionary<string, Operation> operations = new(StringComparer.Ordinal);
    private Uri? baseUrl;

    public OpenApiSource(string id, Uri documentUrl, ToolCredential? credential, HttpClient client)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(documentUrl);
        ArgumentNullException.ThrowIfNull(client);

        if (ToolSourceRules.Violation(documentUrl) is string reason)
        {
            throw SourceHttp.Unavailable(id, reason);
        }

        Id = id;
        Key = documentUrl.AbsoluteUri;
        Placement = ToolSourceRules.PlacementOf(documentUrl);
        this.documentUrl = documentUrl;
        this.credential = credential;
        this.client = client;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string Key { get; }

    /// <inheritdoc />
    public Placement Placement { get; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredTool>> ListAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, documentUrl);
        request.Headers.Accept.ParseAdd("application/json");

        if (credential is not null)
        {
            request.Headers.TryAddWithoutValidation(credential.Header, credential.Value);
        }

        using HttpResponseMessage response = await SourceHttp
            .SendAsync(client, request, Id, credential is not null, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is not HttpStatusCode.OK)
        {
            throw SourceHttp.Unavailable(Id, "status_" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
        }

        string body = await SourceHttp.ReadCappedAsync(response, MaxDocumentBytes, cancellationToken).ConfigureAwait(false)
            ?? throw SourceHttp.Unavailable(Id, "document_too_large");

        JsonObject document;

        try
        {
            document = JsonNode.Parse(body) as JsonObject ?? throw SourceHttp.Unavailable(Id, "openapi_not_json");
        }
        catch (JsonException)
        {
            throw SourceHttp.Unavailable(Id, "openapi_not_json");
        }

        if (Text(document["openapi"]) is not string version || !version.StartsWith("3.", StringComparison.Ordinal))
        {
            throw SourceHttp.Unavailable(Id, "openapi_version");
        }

        baseUrl = BaseUrl(document);

        if (Uri.Compare(baseUrl, documentUrl, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
        {
            // Il documento sta su una sorgente ammessa; le chiamate devono andare li', non dove dice il documento.
            throw SourceHttp.Unavailable(Id, "server_origin_mismatch");
        }

        if (document["paths"] is not JsonObject paths)
        {
            throw SourceHttp.Unavailable(Id, "openapi_without_paths");
        }

        operations.Clear();
        List<DiscoveredTool> tools = [];

        foreach (KeyValuePair<string, JsonNode?> path in paths)
        {
            if (Resolve(path.Value, document, 0) is not JsonObject item || !path.Key.StartsWith('/'))
            {
                continue;
            }

            foreach (string method in Methods)
            {
                if (item[method] is not JsonObject operation
                    || Describe(document, path.Key, method, item, operation) is not (DiscoveredTool tool, Operation described))
                {
                    continue;
                }

                string name = tool.Name;

                for (int suffix = 2; operations.ContainsKey(name); suffix++)
                {
                    name = ToolNames.Sanitize(tool.Name[..Math.Min(tool.Name.Length, ToolNames.MaxLength - 4)] + "_" + suffix.ToString(CultureInfo.InvariantCulture));
                }

                operations[name] = described;

                tools.Add(tool with
                {
                    Name = name,
                    Fingerprint = ToolFingerprint.Compute(
                        ("kind", "openapi"),
                        ("name", name),
                        ("description", tool.Description),
                        ("inputSchema", JsonNode.Parse(tool.InputSchemaJson)),
                        ("method", method),
                        ("path", path.Key),
                        ("server", baseUrl.AbsoluteUri)),
                });

                if (tools.Count > McpSource.MaxTools)
                {
                    throw SourceHttp.Unavailable(Id, "too_many_tools");
                }
            }
        }

        return tools;
    }

    /// <inheritdoc />
    public async Task<ToolInvocation> CallAsync(string tool, string argumentsJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(argumentsJson);

        if (baseUrl is null || !operations.TryGetValue(tool, out Operation? operation))
        {
            return new ToolInvocation("This tool is not available.", IsError: true);
        }

        JsonObject arguments;

        try
        {
            arguments = JsonNode.Parse(argumentsJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return new ToolInvocation("The arguments must be a JSON object.", IsError: true);
        }

        foreach (KeyValuePair<string, JsonNode?> argument in arguments)
        {
            if (!operation.Parameters.Any(parameter => parameter.Name == argument.Key) && !(operation.HasBody && argument.Key == "body"))
            {
                return new ToolInvocation("Unknown argument: " + argument.Key, IsError: true);
            }
        }

        StringBuilder path = new(operation.Path);
        List<string> query = [];
        List<(string Name, string Value)> headers = [];

        foreach (Parameter parameter in operation.Parameters)
        {
            if (arguments[parameter.Name] is not JsonNode value)
            {
                if (parameter.Required)
                {
                    return new ToolInvocation("Missing required argument: " + parameter.Name, IsError: true);
                }

                continue;
            }

            switch (parameter.Location)
            {
                // Un segmento vuoto, "." o ".." spostato dal costruttore dell'indirizzo porterebbe la chiamata, con la
                // credenziale, su un'altra operazione dello stesso server.
                case "path" when Scalar(value) is string text && text is not ("" or "." or ".."):
                    path.Replace("{" + parameter.Name + "}", Uri.EscapeDataString(text));
                    break;

                case "query" when Scalar(value) is string text:
                    query.Add(Uri.EscapeDataString(parameter.Name) + "=" + Uri.EscapeDataString(text));
                    break;

                case "query" when value is JsonArray items && items.All(entry => Scalar(entry) is not null):
                    // Stile predefinito di OpenAPI per la query: form con explode, un valore per ripetizione.
                    query.AddRange(items.Select(entry => Uri.EscapeDataString(parameter.Name) + "=" + Uri.EscapeDataString(Scalar(entry)!)));
                    break;

                case "header" when Scalar(value) is string text
                    && !text.Contains('\r')
                    && !text.Contains('\n')
                    && !(credential is not null && string.Equals(parameter.Name, credential.Header, StringComparison.OrdinalIgnoreCase)):
                    headers.Add((parameter.Name, text));
                    break;

                default:
                    return new ToolInvocation("Unsupported value for argument: " + parameter.Name, IsError: true);
            }
        }

        if (operation.BodyRequired && arguments["body"] is null)
        {
            return new ToolInvocation("Missing required argument: body", IsError: true);
        }

        Uri target = new(baseUrl.AbsoluteUri.TrimEnd('/') + path + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty));

        if (Uri.Compare(target, documentUrl, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return new ToolInvocation(string.Empty, IsError: true, ToolInvocationFailure.SourceUnavailable);
        }

        // Secondo presidio: il percorso che parte e' esattamente quello dell'operazione con i valori al loro posto.
        // Qualunque normalizzazione che lo cambi vuol dire che un valore ha spostato la chiamata altrove.
        if (!string.Equals(target.AbsolutePath, baseUrl.AbsolutePath.TrimEnd('/') + path, StringComparison.OrdinalIgnoreCase))
        {
            return new ToolInvocation("The arguments would change the path of the operation.", IsError: true);
        }

        using HttpRequestMessage request = new(new HttpMethod(operation.Method.ToUpperInvariant()), target);
        request.Headers.Accept.ParseAdd("application/json");

        foreach ((string name, string text) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, text);
        }

        if (credential is not null)
        {
            request.Headers.TryAddWithoutValidation(credential.Header, credential.Value);
        }

        if (arguments["body"] is JsonNode payload && operation.HasBody)
        {
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        }

        try
        {
            using HttpResponseMessage response = await SourceHttp
                .SendAsync(client, request, Id, credential is not null, cancellationToken)
                .ConfigureAwait(false);

            string? body = await SourceHttp.ReadCappedAsync(response, MaxDocumentBytes, cancellationToken).ConfigureAwait(false);

            if (body is null)
            {
                return new ToolInvocation(string.Empty, IsError: true, ToolInvocationFailure.SourceUnavailable);
            }

            // Un errore dell'API e' un risultato che il modello puo' leggere: passa comunque dall'esame del router.
            return response.IsSuccessStatusCode
                ? new ToolInvocation(body, IsError: false)
                : new ToolInvocation(
                    "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + (body.Length > 0 ? "\n" + body : string.Empty),
                    IsError: true);
        }
        catch (PaRouterException exception)
        {
            return new ToolInvocation(string.Empty, IsError: true, SourceHttp.FailureOf(exception));
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Uri BaseUrl(JsonObject document)
    {
        if (document["servers"] is not JsonArray { Count: > 0 } servers || servers[0] is not JsonObject server || Text(server["url"]) is not string url)
        {
            return new Uri(documentUrl, "/");
        }

        if (server["variables"] is JsonObject variables)
        {
            foreach (KeyValuePair<string, JsonNode?> variable in variables)
            {
                if (Text(variable.Value?["default"]) is string fallback)
                {
                    url = url.Replace("{" + variable.Key + "}", fallback, StringComparison.Ordinal);
                }
            }
        }

        return Uri.TryCreate(documentUrl, url, out Uri? resolved) ? resolved : throw SourceHttp.Unavailable(Id, "server_url_invalid");
    }

    private static (DiscoveredTool Tool, Operation Operation)? Describe(
        JsonObject document,
        string path,
        string method,
        JsonObject item,
        JsonObject operation)
    {
        Dictionary<(string Name, string In), JsonObject> merged = [];

        foreach (JsonNode? source in Parameters(item["parameters"]).Concat(Parameters(operation["parameters"])))
        {
            if (Resolve(source, document, 0) is JsonObject parameter && Text(parameter["name"]) is string name && Text(parameter["in"]) is string location)
            {
                // Un parametro dell'operazione sostituisce quello del percorso con lo stesso nome e posizione.
                merged[(name, location)] = parameter;
            }
        }

        JsonObject properties = [];
        JsonArray required = [];
        List<Parameter> parameters = [];

        foreach (((string name, string location), JsonObject parameter) in merged)
        {
            bool mandatory = location == "path" || parameter["required"]?.GetValueKind() is JsonValueKind.True;
            bool supported = location is "path" or "query"
                || (location == "header"
                    && ToolSourceRules.HeaderViolation(name) is null
                    && !string.Equals(name, "authorization", StringComparison.OrdinalIgnoreCase)
                    && !ForbiddenParameterHeaders.Contains(name));

            if (!supported)
            {
                if (mandatory)
                {
                    return null;
                }

                continue;
            }

            JsonObject schema = Resolve(parameter["schema"], document, 0) as JsonObject ?? new JsonObject { ["type"] = "string" };

            if (Text(parameter["description"]) is string description && schema["description"] is null)
            {
                schema["description"] = description;
            }

            properties[name] = schema;

            if (mandatory)
            {
                required.Add(name);
            }

            parameters.Add(new Parameter(name, location, mandatory));
        }

        bool hasBody = false;
        bool bodyRequired = false;

        if (Resolve(operation["requestBody"], document, 0) is JsonObject requestBody)
        {
            bodyRequired = requestBody["required"]?.GetValueKind() is JsonValueKind.True;

            if (requestBody["content"]?["application/json"]?["schema"] is JsonNode bodySchema)
            {
                properties["body"] = Resolve(bodySchema, document, 0);
                hasBody = true;

                if (bodyRequired)
                {
                    required.Add("body");
                }
            }
            else if (bodyRequired)
            {
                return null;
            }
        }

        JsonObject inputSchema = new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };

        string summary = string.Join(
            "\n",
            new[] { Text(operation["summary"]), Text(operation["description"]) }.Where(text => !string.IsNullOrWhiteSpace(text)));

        string toolDescription = summary.Length > MaxDescriptionChars ? summary[..MaxDescriptionChars] : summary;
        string toolName = ToolNames.Sanitize(Text(operation["operationId"]) ?? method + "_" + path);

        return (
            new DiscoveredTool(toolName, toolDescription, inputSchema.ToJsonString(), string.Empty),
            new Operation(method, path, parameters, hasBody, bodyRequired));
    }

    private static IEnumerable<JsonNode?> Parameters(JsonNode? node) => node as JsonArray ?? [];

    /// <summary>
    /// Copia del nodo con i riferimenti locali risolti fino a una profondita' fissa: un riferimento circolare o
    /// esterno diventa uno schema vuoto, che accetta qualunque cosa ma non trascina il router in un ciclo.
    /// </summary>
    private static JsonNode? Resolve(JsonNode? node, JsonObject document, int depth)
    {
        if (node is null)
        {
            return null;
        }

        if (depth > MaxSchemaDepth)
        {
            return new JsonObject();
        }

        if (node is JsonObject reference && Text(reference["$ref"]) is string pointer)
        {
            return pointer.StartsWith("#/", StringComparison.Ordinal) && Pointer(document, pointer) is JsonNode target
                ? Resolve(target, document, depth + 1)
                : new JsonObject();
        }

        switch (node)
        {
            case JsonObject item:
                {
                    JsonObject copy = [];

                    foreach (KeyValuePair<string, JsonNode?> property in item)
                    {
                        copy[property.Key] = Resolve(property.Value, document, depth + 1);
                    }

                    return copy;
                }

            case JsonArray array:
                {
                    JsonArray copy = [];

                    foreach (JsonNode? entry in array)
                    {
                        copy.Add(Resolve(entry, document, depth + 1));
                    }

                    return copy;
                }

            default:
                return node.DeepClone();
        }
    }

    private static JsonNode? Pointer(JsonObject document, string pointer)
    {
        JsonNode? current = document;

        foreach (string segment in pointer[2..].Split('/'))
        {
            string key = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

            current = current is JsonObject item ? item[key] : null;

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static string? Scalar(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.Number => node.ToJsonString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static string? Text(JsonNode? node) =>
        node?.GetValueKind() is JsonValueKind.String ? node.GetValue<string>() : null;

    private sealed record Parameter(string Name, string Location, bool Required);

    private sealed record Operation(string Method, string Path, IReadOnlyList<Parameter> Parameters, bool HasBody, bool BodyRequired);
}
