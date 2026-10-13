using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Extensions.Tools;

/// <summary>
/// Client MCP comune ai trasporti (specifica 2025-11-25): <c>initialize</c> e negoziazione della versione,
/// <c>notifications/initialized</c>, <c>tools/list</c> con paginazione e <c>tools/call</c>. Il router non offre
/// capacita' al server — niente sampling, elicitation o roots — e ignora le richieste che il server gli rivolge.
/// </summary>
public abstract class McpSource : IToolSource
{
    /// <summary>Versione chiesta dal router.</summary>
    public const string LatestProtocolVersion = "2025-11-25";

    /// <summary>Strumenti letti al massimo da una sorgente.</summary>
    public const int MaxTools = 256;

    private const int MaxPages = 16;

    private readonly SemaphoreSlim initialization = new(1, 1);
    private int nextId;
    private volatile bool initialized;

    protected McpSource(string id, string key, Placement placement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Id = id;
        Key = key;
        Placement = placement;
    }

    /// <summary>Versioni che il router sa parlare.</summary>
    public static IReadOnlyList<string> SupportedProtocolVersions { get; } = ["2025-11-25", "2025-06-18", "2025-03-26"];

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string Key { get; }

    /// <inheritdoc />
    public Placement Placement { get; }

    /// <summary>Versione negoziata, null prima dell'inizializzazione.</summary>
    protected string? NegotiatedVersion { get; private set; }

    /// <summary>
    /// Scambia un messaggio JSON-RPC. Per una richiesta restituisce la risposta con lo stesso identificatore; per
    /// una notifica null. Un guasto e' un'eccezione tipizzata.
    /// </summary>
    protected abstract Task<JsonNode?> ExchangeAsync(JsonObject message, bool expectsResponse, CancellationToken cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredTool>> ListAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        List<DiscoveredTool> tools = [];
        string? cursor = null;

        for (int page = 0; page < MaxPages; page++)
        {
            JsonObject parameters = [];

            if (cursor is not null)
            {
                parameters["cursor"] = cursor;
            }

            JsonNode response = await RequestAsync("tools/list", parameters, cancellationToken).ConfigureAwait(false);

            if (response["result"] is not JsonObject result || result["tools"] is not JsonArray list)
            {
                throw SourceHttp.Unavailable(Id, "tools_list_invalid");
            }

            foreach (JsonNode? item in list)
            {
                if (item is not JsonObject tool
                    || Text(tool["name"]) is not string name
                    || !ToolNames.IsWellFormedMcpName(name)
                    || tool["inputSchema"] is not JsonObject schema)
                {
                    throw SourceHttp.Unavailable(Id, "tool_malformed");
                }

                string description = Text(tool["description"]) ?? string.Empty;

                tools.Add(new DiscoveredTool(
                    name,
                    description,
                    schema.ToJsonString(),
                    ToolFingerprint.Compute(("kind", "mcp"), ("name", name), ("description", description), ("inputSchema", schema))));

                if (tools.Count > MaxTools)
                {
                    throw SourceHttp.Unavailable(Id, "too_many_tools");
                }
            }

            cursor = Text(result["nextCursor"]);

            if (cursor is null)
            {
                return tools;
            }
        }

        throw SourceHttp.Unavailable(Id, "too_many_pages");
    }

    /// <inheritdoc />
    public async Task<ToolInvocation> CallAsync(string tool, string argumentsJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(argumentsJson);

        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

            JsonNode response = await RequestAsync(
                "tools/call",
                new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(argumentsJson) },
                cancellationToken).ConfigureAwait(false);

            if (response["error"] is not null)
            {
                // Errore di protocollo: il messaggio del server non si propaga, il modello sa solo che non e' andata.
                return new ToolInvocation("The tool server answered with a protocol error.", IsError: true);
            }

            if (response["result"] is not JsonObject result)
            {
                return new ToolInvocation(string.Empty, IsError: true, ToolInvocationFailure.SourceUnavailable);
            }

            return new ToolInvocation(ContentText(result), result["isError"]?.GetValueKind() is JsonValueKind.True);
        }
        catch (PaRouterException exception)
        {
            return new ToolInvocation(string.Empty, IsError: true, SourceHttp.FailureOf(exception));
        }
        catch (JsonException)
        {
            return new ToolInvocation(string.Empty, IsError: true, ToolInvocationFailure.SourceUnavailable);
        }
    }

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    /// <summary>Vero se il messaggio risponde alla richiesta con quell'identificatore.</summary>
    protected static bool Answers(JsonNode? node, string id) =>
        node is JsonObject message
        && message["id"] is JsonValue value
        && string.Equals(value.ToString(), id, StringComparison.Ordinal)
        && (message["result"] is not null || message["error"] is not null);

    /// <summary>Rilascia le risorse comuni.</summary>
    protected void DisposeInitialization() => initialization.Dispose();

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized)
        {
            return;
        }

        await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (initialized)
            {
                return;
            }

            JsonNode response = await RequestAsync(
                "initialize",
                new JsonObject
                {
                    ["protocolVersion"] = LatestProtocolVersion,
                    // Nessuna capacita' offerta al server: il router non campiona, non chiede input all'utente e
                    // non espone cartelle.
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "pa-router", ["version"] = "1.0" },
                },
                cancellationToken).ConfigureAwait(false);

            if (Text(response["result"]?["protocolVersion"]) is not string version)
            {
                throw SourceHttp.Unavailable(Id, "initialize_failed");
            }

            if (!SupportedProtocolVersions.Contains(version, StringComparer.Ordinal))
            {
                // La specifica chiede al client di disconnettersi da una versione che non sa parlare.
                throw SourceHttp.Unavailable(Id, "protocol_version");
            }

            NegotiatedVersion = version;

            await ExchangeAsync(
                new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" },
                expectsResponse: false,
                cancellationToken).ConfigureAwait(false);

            initialized = true;
        }
        finally
        {
            initialization.Release();
        }
    }

    private async Task<JsonNode> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        string id = Interlocked.Increment(ref nextId).ToString(CultureInfo.InvariantCulture);

        JsonNode? response = await ExchangeAsync(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters },
            expectsResponse: true,
            cancellationToken).ConfigureAwait(false);

        return response ?? throw SourceHttp.Unavailable(Id, "no_response");
    }

    /// <summary>
    /// Testo del risultato: i blocchi di testo nell'ordine, il contenuto strutturato quando non c'e' testo, e un
    /// segnaposto per i blocchi che non sono testo, che il router non passa al modello.
    /// </summary>
    private static string ContentText(JsonObject result)
    {
        StringBuilder builder = new();

        if (result["content"] is JsonArray content)
        {
            foreach (JsonNode? item in content)
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                if (item is JsonObject block && Text(block["type"]) is "text" && Text(block["text"]) is string text)
                {
                    builder.Append(text);
                }
                else
                {
                    builder.Append("[non-text content omitted]");
                }
            }
        }

        if (builder.Length == 0 && result["structuredContent"] is JsonObject structured)
        {
            builder.Append(structured.ToJsonString());
        }

        return builder.ToString();
    }

    private static string? Text(JsonNode? node) =>
        node?.GetValueKind() is JsonValueKind.String ? node.GetValue<string>() : null;
}
