using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Adapters.Ollama;

/// <summary>
/// Adapter del modello locale: POST su <c>/api/chat</c> con stream NDJSON.
///
/// Il payload autorizzato entra come messaggio utente; le istruzioni dell'applicazione, quando ci sono,
/// lo precedono nel ruolo <c>system</c> (ADR-020). Ogni riga dello stream
/// e' un evento; il terminale valido e' la riga con <c>done</c> vero. Uno stream che finisce senza
/// quel marcatore non e' un completamento, anche se la connessione si chiude in modo pulito
/// (03-specs/ADAPTERS.md).
/// </summary>
public sealed class OllamaChatAdapter : IExecutionAdapter, IDisposable
{
    /// <summary>Percorso della chat nell'API locale.</summary>
    public const string ChatPath = "/api/chat";

    private readonly HttpClient client;
    private readonly bool ownsClient;

    public OllamaChatAdapter()
        : this(PlacementRules.CreateModelClient(Placement.Lan), ownsClient: true)
    {
    }

    /// <param name="client">Client gia' configurato; i test ne passano uno con handler locale.</param>
    /// <param name="ownsClient">Vero se questo adapter deve rilasciare il client.</param>
    public OllamaChatAdapter(HttpClient client, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);

        this.client = client;
        this.ownsClient = ownsClient;
    }

    /// <inheritdoc />
    public AdapterKind AdapterId => AdapterKind.Ollama;

    /// <inheritdoc />
    public bool SupportsMemoryTools => false;

    /// <inheritdoc />
    public bool SupportsNativeSkills => false;

    /// <inheritdoc />
    public async IAsyncEnumerable<AdapterEvent> ExecuteAsync(
        AuthorizedExecution execution,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);

        if (!PlacementRules.IsAcceptable(execution.InferencePlacement, execution.Endpoint))
        {
            // Prima di qualunque connessione: un endpoint che non soddisfa la regola della
            // collocazione dichiarata non viene contattato.
            yield return new AdapterFailed(
                ErrorCode.ControlUnavailable,
                "The declared endpoint does not satisfy the rule of its placement.");

            yield break;
        }

        if (string.IsNullOrWhiteSpace(execution.Model))
        {
            yield return new AdapterFailed(
                ErrorCode.TargetUnavailable,
                "A local model target must declare the exact model to run.");

            yield break;
        }

        if (LocalModel.IsCloudAlias(execution.Model))
        {
            // Ollama con modelli ospitati resta raggiungibile in loopback: il payload uscirebbe
            // comunque. Il deny precede la connessione (AC-10).
            yield return new AdapterFailed(
                ErrorCode.ControlUnavailable,
                "The configured model is a hosted alias, so the inference would not be local.");

            yield break;
        }

        yield return new AdapterStarted();

        await foreach (AdapterEvent adapterEvent in StreamAsync(execution, cancellationToken).ConfigureAwait(false))
        {
            yield return adapterEvent;
        }
    }

    private async IAsyncEnumerable<AdapterEvent> StreamAsync(
        AuthorizedExecution execution,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(execution.Endpoint!, ChatPath))
        {
            Content = new StringContent(BuildBody(execution), Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage? response = null;
        AdapterFailed? failure = null;

        try
        {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            failure = new AdapterFailed(ErrorCode.TargetUnavailable, "The local model did not accept the connection.");
            _ = ex;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            failure = new AdapterFailed(ErrorCode.ProviderTimeout, "The local model did not answer within the deadline.");
        }

        if (failure is not null)
        {
            yield return failure;
            yield break;
        }

        using (response)
        {
            if (IsRedirect(response!.StatusCode))
            {
                // Un redirect sposterebbe l'inferenza altrove: non viene seguito e non e' un errore
                // di rete, e' un controllo mancante.
                yield return new AdapterFailed(
                    ErrorCode.ControlUnavailable,
                    "The local endpoint answered with a redirect, which is not followed.");

                yield break;
            }

            if (response.StatusCode is not HttpStatusCode.OK)
            {
                yield return new AdapterFailed(
                    ErrorCode.TargetUnavailable,
                    "The local model answered with an error status.");

                yield break;
            }

            await foreach (AdapterEvent adapterEvent in ReadNdjsonAsync(response, execution.Limits.MaxOutputBytes, cancellationToken).ConfigureAwait(false))
            {
                yield return adapterEvent;
            }
        }
    }

    private static async IAsyncEnumerable<AdapterEvent> ReadNdjsonAsync(
        HttpResponseMessage response,
        int maxOutputBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Stream? opened = null;
        try { opened = await BoundedHttpContent.OpenAsync(response.Content, BoundedHttpContent.StreamBytes(maxOutputBytes), cancellationToken).ConfigureAwait(false); }
        catch (PaRouterException) { }
        if (opened is null)
        {
            yield return new AdapterFailed(ErrorCode.ProtocolError, "The local response exceeds its transport limit.");
            yield break;
        }
        await using Stream stream = opened;
        BoundedNdjsonReader reader = new(stream, BoundedHttpContent.JsonBytes(maxOutputBytes));

        bool done = false;
        List<ToolCall> calls = [];
        long? inputTokens = null;
        long? outputTokens = null;
        long? loadNanoseconds = null;
        bool truncated = false;
        long reasoning = 0;

        while (true)
        {
            string? line;
            bool interrupted = false;

            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or PaRouterException)
            {
                line = null;
                interrupted = true;
            }

            if (interrupted)
            {
                // Uno stream troncato non e' un completamento: manca il marcatore terminale.
                yield return new AdapterFailed(ErrorCode.ProtocolError, "The local model stream was interrupted.");
                yield break;
            }

            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            NdjsonLine? parsed = ParseLine(line);

            if (parsed is null)
            {
                yield return new AdapterFailed(ErrorCode.ProtocolError, "The local model produced a malformed NDJSON line.");
                yield break;
            }

            if (parsed.Error is string providerError)
            {
                // Il messaggio del provider non viene propagato: solo il codice.
                _ = providerError;
                yield return new AdapterFailed(ErrorCode.TargetUnavailable, "The local model reported an error.");
                yield break;
            }

            calls.AddRange(parsed.ToolCalls);
            reasoning += parsed.Thinking?.Length ?? 0;

            if (parsed.Content is string content && content.Length > 0)
            {
                yield return new AdapterOutputChunk(content);
            }

            inputTokens ??= parsed.PromptEvalCount;
            outputTokens ??= parsed.EvalCount;
            loadNanoseconds ??= parsed.LoadDuration;
            truncated |= parsed.Truncated;

            if (parsed.Done)
            {
                done = true;
                break;
            }
        }

        if (!done)
        {
            yield return new AdapterFailed(
                ErrorCode.ProtocolError,
                "The local model stream ended without a terminal marker.");

            yield break;
        }

        if (calls.Count > 0)
        {
            // Le versioni recenti di Ollama danno un identificatore alle chiamate (0.34.0, osservato); quando manca lo
            // assegna l'adapter nell'ordine di arrivo. Nel giro successivo il risultato torna con il nome dello
            // strumento (tool_name), come prevede /api/chat.
            yield return new AdapterToolCalls([.. calls.Select((call, index) => call.CallId.Length > 0
                ? call
                : call with { CallId = "call-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) })]);
        }

        if (inputTokens is not null || outputTokens is not null)
        {
            // Il costo di un modello locale non e' noto: null significa non misurato.
            // Ollama dichiara in nanosecondi quanto ha speso a caricare il modello: e' cio' che separa un modello
            // lento da un modello che stava entrando in memoria.
            yield return new AdapterUsage(new UsageReport(inputTokens, outputTokens, BilledCost: null)
            {
                LoadMilliseconds = loadNanoseconds / 1_000_000,
                // Solo quanto, mai il testo: il ragionamento non e' passato dallo screening e non si mostra.
                ReasoningCharacters = reasoning > 0 ? reasoning : null,
                Truncated = truncated,
            });
        }

        yield return new AdapterCompleted(0);
    }

    /// <summary>
    /// Corpo della richiesta: le istruzioni dell'applicazione nel ruolo di sistema, quando ci sono, poi il
    /// payload gia' autorizzato come messaggio utente; stream attivo.
    /// </summary>
    private static string BuildBody(AuthorizedExecution execution)
    {
        using MemoryStream buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", execution.Model);
            writer.WriteBoolean("stream", true);

            if (execution.Thinking is bool think)
            {
                // Scelto dall'operatore nel setup: spento, il modello risponde subito invece di ragionare per minuti.
                writer.WriteBoolean("think", think);
            }

            if (execution.KeepAliveMinutes is int keep)
            {
                // Quanto resta in memoria dopo questa risposta: lo ha scelto l'operatore nel setup.
                writer.WriteString("keep_alive", OllamaKeepAlive.Format(keep));
            }

            writer.WriteStartObject("options");
            writer.WriteNumber("num_predict", GenerationLimits.OutputTokens(execution.Limits.MaxOutputBytes, execution.MaxOutputTokens));
            if (execution.ContextWindowTokens > 0)
            {
                // La finestra che il registro dichiara e con cui la policy ha ammesso il servizio. Senza, Ollama usa la
                // sua predefinita e taglia l'inizio della conversazione senza dirlo (S117).
                writer.WriteNumber("num_ctx", execution.ContextWindowTokens);
            }
            writer.WriteEndObject();

            if (execution.Payload.Tools is { Count: > 0 } tools)
            {
                // Forma di /api/chat: {"type":"function","function":{name, description, parameters}}.
                writer.WriteStartArray("tools");

                foreach (ToolDefinition tool in tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters");
                    writer.WriteRawValue(tool.InputSchemaJson);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteStartArray("messages");

            if (execution.Payload.HasInstructions)
            {
                WriteMessage(writer, "system", Encoding.UTF8.GetString(execution.Payload.Instructions.Span));
            }

            // I turni precedenti come turni, la richiesta per ultima: gli stessi byte esaminati, divisi fra i
            // messaggi invece che impilati in uno solo (ChatTranscript, S116).
            foreach (ChatMessage message in ChatTranscript.Split(execution.Payload))
            {
                WriteMessage(writer, message.Role, message.Text);
            }

            foreach (ToolRound round in execution.Rounds)
            {
                // Il messaggio del modello con le chiamate, poi un messaggio "tool" per ogni risultato.
                writer.WriteStartObject();
                writer.WriteString("role", "assistant");
                writer.WriteString("content", round.AssistantText ?? string.Empty);
                writer.WriteStartArray("tool_calls");

                foreach (ToolCall call in round.Calls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", call.Name);
                    writer.WritePropertyName("arguments");
                    writer.WriteRawValue(call.ArgumentsObjectJson);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();

                foreach (ToolCallResult result in round.Results)
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", "tool");
                    writer.WriteString("tool_name", result.Name);
                    writer.WriteString("content", result.Content);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteMessage(Utf8JsonWriter writer, string role, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    private static NdjsonLine? ParseLine(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            string? content = null;
            string? thinking = null;

            if (root.TryGetProperty("message", out JsonElement message)
                && message.ValueKind is JsonValueKind.Object
                && message.TryGetProperty("content", out JsonElement contentElement)
                && contentElement.ValueKind is JsonValueKind.String)
            {
                content = contentElement.GetString();
            }

            if (root.TryGetProperty("message", out JsonElement reasoningMessage)
                && reasoningMessage.ValueKind is JsonValueKind.Object
                && reasoningMessage.TryGetProperty("thinking", out JsonElement thinkingElement)
                && thinkingElement.ValueKind is JsonValueKind.String)
            {
                thinking = thinkingElement.GetString();
            }

            List<ToolCall> toolCalls = [];

            if (root.TryGetProperty("message", out JsonElement toolMessage)
                && toolMessage.ValueKind is JsonValueKind.Object
                && toolMessage.TryGetProperty("tool_calls", out JsonElement requested)
                && requested.ValueKind is JsonValueKind.Array)
            {
                foreach (JsonElement call in requested.EnumerateArray())
                {
                    if (call.ValueKind is not JsonValueKind.Object
                        || !call.TryGetProperty("function", out JsonElement function)
                        || function.ValueKind is not JsonValueKind.Object
                        || !function.TryGetProperty("name", out JsonElement name)
                        || name.ValueKind is not JsonValueKind.String)
                    {
                        return null;
                    }

                    // Gli argomenti arrivano come oggetto; si trasportano come testo e il coordinatore decide.
                    string arguments = function.TryGetProperty("arguments", out JsonElement value)
                        ? value.ValueKind is JsonValueKind.String ? value.GetString()! : value.GetRawText()
                        : "{}";

                    string id = call.TryGetProperty("id", out JsonElement identifier) && identifier.ValueKind is JsonValueKind.String
                        ? identifier.GetString()!
                        : string.Empty;

                    toolCalls.Add(new ToolCall(id, name.GetString()!, arguments));
                }
            }

            return new NdjsonLine(
                content,
                root.TryGetProperty("done", out JsonElement done) && done.ValueKind is JsonValueKind.True,
                root.TryGetProperty("error", out JsonElement error) && error.ValueKind is JsonValueKind.String
                    ? error.GetString()
                    : null,
                ReadCount(root, "prompt_eval_count"),
                ReadCount(root, "eval_count"),
                ReadCount(root, "load_duration"),
                toolCalls)
            {
                Thinking = thinking,
                // `length`: la finestra o il tetto di uscita hanno fermato la risposta, che non e' finita.
                Truncated = root.TryGetProperty("done_reason", out JsonElement reason)
                    && reason.ValueKind is JsonValueKind.String
                    && string.Equals(reason.GetString(), "length", StringComparison.Ordinal),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? ReadCount(JsonElement root, string name) =>
        BoundedHttpContent.NonNegativeCount(root, name);

    /// <inheritdoc />
    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
    }

    private sealed record NdjsonLine(
        string? Content,
        bool Done,
        string? Error,
        long? PromptEvalCount,
        long? EvalCount,
        long? LoadDuration,
        IReadOnlyList<ToolCall> ToolCalls)
    {
        /// <summary>Vero se Ollama ha smesso per il tetto di lunghezza.</summary>
        public bool Truncated { get; init; }

        /// <summary>Ragionamento non mostrato di questa riga: si conta e si butta.</summary>
        public string? Thinking { get; init; }
    }
}
