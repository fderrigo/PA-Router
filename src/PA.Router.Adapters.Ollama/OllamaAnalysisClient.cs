using System.Net;
using System.Text;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Adapters.Ollama;

/// <summary>Risposta dell'analizzatore: il testo vincolato dallo schema e, se il runtime la dichiara, la confidenza.</summary>
/// <param name="Content">Uscita del modello, gia' vincolata dallo schema passato.</param>
/// <param name="Confidence">Probabilita' minima fra i token numerici dell'uscita; null se il runtime non la dichiara.</param>
/// <param name="LoadMilliseconds">Tempo di caricamento del modello dichiarato da Ollama.</param>
public sealed record AnalysisReply(string Content, double? Confidence, long? LoadMilliseconds);

/// <summary>
/// Una chiamata all'analizzatore della richiesta (ADR-026) su un Ollama di **questa macchina**.
///
/// Non e' un adapter di esecuzione: non riceve un payload autorizzato e non risponde a nessuno. Legge il testo
/// dell'operatore prima della privacy, quindi accetta solo un endpoint di loopback — la stessa regola del
/// classificatore (S42) — e rifiuta un modello ospitato. Le condizioni di ADR-026 sono tutte qui: temperatura 0, schema
/// imposto dal runtime, ragionamento visibile spento, modello residente, finestra esplicita, probabilita' dei token per la
/// confidenza.
/// </summary>
public sealed class OllamaAnalysisClient : IDisposable
{
    private readonly HttpClient client;
    private readonly bool ownsClient;

    public OllamaAnalysisClient()
        : this(PlacementRules.CreateModelClient(Placement.Loopback), ownsClient: true)
    {
    }

    /// <param name="client">Client gia' configurato; le prove ne passano uno verso un server di prova.</param>
    /// <param name="ownsClient">Vero se questo client va rilasciato qui.</param>
    public OllamaAnalysisClient(HttpClient client, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);

        this.client = client;
        this.ownsClient = ownsClient;
    }

    /// <summary>Chiede l'analisi. Un guasto e' un'eccezione tipizzata: chi chiama la tratta come assenza di segnali.</summary>
    /// <param name="endpoint">Endpoint di Ollama, solo loopback numerico.</param>
    /// <param name="model">Modello esatto, non ospitato.</param>
    /// <param name="system">Prompt di sistema versionato, identico a ogni chiamata.</param>
    /// <param name="user">Testo dell'operatore, gia' troncato al budget.</param>
    /// <param name="format">Schema JSON dell'uscita.</param>
    /// <param name="contextTokens">Finestra da chiedere a Ollama.</param>
    /// <param name="cancellationToken">Scadenza dell'analisi.</param>
    public async Task<AnalysisReply> AnalyzeAsync(
        Uri endpoint,
        string model,
        string system,
        string user,
        string format,
        int contextTokens,
        CancellationToken cancellationToken,
        int? keepAliveMinutes = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(format);

        if (!PlacementRules.IsAcceptable(Placement.Loopback, endpoint) || LocalModel.IsCloudAlias(model))
        {
            // Prima di qualunque connessione: il testo non esce per sapere se puo' uscire.
            throw new PaRouterException(ErrorCode.ControlUnavailable, "The request analyzer must run on this machine.")
                .WithMetadata("reason", "analyzer_not_local");
        }

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(endpoint, OllamaChatAdapter.ChatPath))
        {
            Content = new StringContent(Body(model, system, user, format, contextTokens, keepAliveMinutes), Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response;
        using CancellationTokenSource timeout = BoundedHttpContent.Deadline(client, cancellationToken);

        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new PaRouterException(ErrorCode.TargetUnavailable, "The request analyzer did not accept the connection.");
        }

        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new PaRouterException(ErrorCode.ControlUnavailable, "The request analyzer answered with a redirect.");
            }

            if (response.StatusCode is not HttpStatusCode.OK)
            {
                throw new PaRouterException(ErrorCode.TargetUnavailable, "The request analyzer answered with an error status.");
            }

            string text = await BoundedHttpContent.ReadUtf8Async(response.Content, BoundedHttpContent.MetadataBytes, timeout.Token).ConfigureAwait(false);

            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                JsonElement root = document.RootElement;
                string content = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out JsonElement message)
                    && message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("content", out JsonElement value) && value.ValueKind is JsonValueKind.String
                        ? value.GetString()!
                        : throw new PaRouterException(ErrorCode.ProtocolError, "The request analyzer returned no content.");

                long? load = BoundedHttpContent.NonNegativeCount(root, "load_duration") / 1_000_000;

                return new AnalysisReply(content, Confidence(root), load);
            }
            catch (JsonException)
            {
                throw new PaRouterException(ErrorCode.ProtocolError, "The request analyzer returned malformed JSON.");
            }
        }
    }

    /// <summary>
    /// La probabilita' piu' bassa fra i token che portano cifre: sono i livelli, cioe' cio' su cui il modello decide.
    /// Una confidenza scritta dal modello non conta (ADR-026); questa la misura il runtime.
    /// </summary>
    private static double? Confidence(JsonElement root)
    {
        if (!root.TryGetProperty("logprobs", out JsonElement logprobs) || logprobs.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        double? lowest = null;

        foreach (JsonElement entry in logprobs.EnumerateArray())
        {
            if (entry.ValueKind is not JsonValueKind.Object
                || !entry.TryGetProperty("token", out JsonElement token) || token.ValueKind is not JsonValueKind.String
                || !entry.TryGetProperty("logprob", out JsonElement logprob) || logprob.ValueKind != JsonValueKind.Number
                || !logprob.TryGetDouble(out double value) || !double.IsFinite(value) || value > 0)
            {
                continue;
            }

            if (token.GetString()!.Any(char.IsDigit))
            {
                double probability = Math.Exp(value);
                lowest = lowest is double current ? Math.Min(current, probability) : probability;
            }
        }

        return lowest;
    }

    private static string Body(string model, string system, string user, string format, int contextTokens, int? keepAliveMinutes)
    {
        using MemoryStream buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteBoolean("stream", false);
            writer.WriteBoolean("think", false);
            if (keepAliveMinutes is int keep) { writer.WriteString("keep_alive", OllamaKeepAlive.Format(keep)); }
            writer.WriteBoolean("logprobs", true);
            writer.WritePropertyName("format");
            writer.WriteRawValue(format);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", system);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", user);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartObject("options");
            writer.WriteNumber("temperature", 0);
            writer.WriteNumber("seed", 42);
            writer.WriteNumber("num_ctx", contextTokens);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
    }
}
