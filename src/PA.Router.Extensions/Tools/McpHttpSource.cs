using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Extensions.Tools;

/// <summary>
/// Server MCP in Streamable HTTP (specifica 2025-11-25, Transports). Ogni messaggio e' un POST; la risposta a una
/// richiesta arriva come JSON oppure in uno stream SSE, e il client deve saper leggere entrambi. L'identificatore
/// di sessione restituito all'inizializzazione accompagna le richieste successive, con la versione negoziata.
/// </summary>
public sealed class McpHttpSource : McpSource
{
    /// <summary>Tetto di una risposta.</summary>
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    private readonly HttpClient client;
    private readonly Uri url;
    private readonly ToolCredential? credential;
    private string? sessionId;

    public McpHttpSource(string id, Uri url, ToolCredential? credential, HttpClient client)
        : base(id, (url ?? throw new ArgumentNullException(nameof(url))).AbsoluteUri, ToolSourceRules.PlacementOf(url))
    {
        ArgumentNullException.ThrowIfNull(client);

        if (ToolSourceRules.Violation(url) is string reason)
        {
            throw SourceHttp.Unavailable(id, reason);
        }

        this.client = client;
        this.url = url;
        this.credential = credential;
    }

    /// <inheritdoc />
    protected override async Task<JsonNode?> ExchangeAsync(JsonObject message, bool expectsResponse, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        if (sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("MCP-Session-Id", sessionId);
        }

        if (NegotiatedVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", NegotiatedVersion);
        }

        if (credential is not null)
        {
            // Solo verso questa sorgente: la credenziale non entra mai negli argomenti che il modello vede.
            request.Headers.TryAddWithoutValidation(credential.Header, credential.Value);
        }

        using HttpResponseMessage response = await SourceHttp
            .SendAsync(client, request, Id, credential is not null, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw SourceHttp.Unavailable(Id, "status_" + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (sessionId is null && response.Headers.TryGetValues("MCP-Session-Id", out IEnumerable<string>? values))
        {
            sessionId = values.FirstOrDefault();
        }

        if (!expectsResponse)
        {
            return null;
        }

        string body = await SourceHttp.ReadCappedAsync(response, MaxResponseBytes, cancellationToken).ConfigureAwait(false)
            ?? throw SourceHttp.Unavailable(Id, "response_too_large");

        string id = message["id"]!.GetValue<string>();

        JsonNode? answer = string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)
            ? FromEventStream(body, id)
            : FromJson(body, id);

        return answer ?? throw SourceHttp.Unavailable(Id, "no_response");
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        DisposeInitialization();

        return ValueTask.CompletedTask;
    }

    private static JsonNode? FromJson(string body, string id)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(body);

            return Answers(node, id) ? node : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stream SSE: gli eventi sono separati da una riga vuota e il messaggio sta nelle righe <c>data:</c>. Il server
    /// puo' mandare notifiche e richieste prima della risposta: si saltano.
    /// </summary>
    private static JsonNode? FromEventStream(string body, string id)
    {
        StringBuilder data = new();

        foreach (string line in body.ReplaceLineEndings("\n").Split('\n').Append(string.Empty))
        {
            if (line.Length == 0)
            {
                if (data.Length > 0 && FromJson(data.ToString(), id) is JsonNode answer)
                {
                    return answer;
                }

                data.Clear();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.AsSpan(5).TrimStart(' '));
            }
        }

        return null;
    }
}
