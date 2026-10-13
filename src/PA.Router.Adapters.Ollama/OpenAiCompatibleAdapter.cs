using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Adapters.Ollama;

/// <summary>
/// Adapter HTTP per un servizio compatibile con l'API OpenAI: POST su
/// <c>/v1/chat/completions</c>, una risposta sola.
///
/// **Non in streaming.** Il router non trasmette testo al client mentre arriva, quindi una
/// risposta parziale non servirebbe a nessuno; in compenso complicherebbe la regola sul ripiego,
/// che dopo un output non ritenta mai. O la risposta c'e' tutta, o non c'e'.
///
/// Il payload autorizzato entra come messaggio utente; le istruzioni dell'applicazione, quando ci sono,
/// lo precedono nel ruolo <c>system</c> (ADR-020).
///
/// La regola di rete della collocazione vale prima di aprire la connessione: `private_remote` e
/// `third_party` parlano solo in TLS con certificato verificato, e nessun redirect viene seguito
/// (ADR-016).
/// </summary>
public sealed class OpenAiCompatibleAdapter : IExecutionAdapter, IDisposable
{
    /// <summary>Percorso della chat nell'API compatibile.</summary>
    public const string ChatPath = "/v1/chat/completions";

    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly Func<string, string?> secrets;

    /// <param name="secrets">
    /// Risolutore dei segreti per nome. L'adapter riceve un nome, non un valore: i nomi possono
    /// stare in un DTO, i valori no.
    /// </param>
    public OpenAiCompatibleAdapter(Func<string, string?> secrets)
        : this(PlacementRules.CreateModelClient(Placement.ThirdParty), secrets, ownsClient: true)
    {
    }

    /// <param name="client">Client gia' configurato; i test ne passano uno con handler locale.</param>
    /// <param name="secrets">Risolutore dei segreti per nome.</param>
    /// <param name="ownsClient">Vero se questo adapter deve rilasciare il client.</param>
    public OpenAiCompatibleAdapter(HttpClient client, Func<string, string?> secrets, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(secrets);

        this.client = client;
        this.secrets = secrets;
        this.ownsClient = ownsClient;
    }

    /// <inheritdoc />
    public AdapterKind AdapterId => AdapterKind.OpenAiCompatible;

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
            yield return new AdapterFailed(
                ErrorCode.ControlUnavailable,
                "The declared endpoint does not satisfy the rule of its placement.");

            yield break;
        }

        if (string.IsNullOrWhiteSpace(execution.Model))
        {
            yield return new AdapterFailed(ErrorCode.TargetUnavailable, "The service declares no model.");
            yield break;
        }

        string? key = execution.SecretRef is string reference ? secrets(reference) : null;

        if (execution.SecretRef is not null && key is null)
        {
            // Un segreto dichiarato e assente non e' un caso da riempire con una chiamata anonima:
            // il servizio e' inutilizzabile finche' l'operatore non lo scrive nel vault.
            yield return new AdapterFailed(
                ErrorCode.AuthUnavailable,
                "The service declares a secret that the local vault does not hold.");

            yield break;
        }

        yield return new AdapterStarted();

        await foreach (AdapterEvent adapterEvent in SendAsync(execution, key, cancellationToken).ConfigureAwait(false))
        {
            yield return adapterEvent;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
    }

    private async IAsyncEnumerable<AdapterEvent> SendAsync(
        AuthorizedExecution execution,
        string? key,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(execution.Endpoint!, ChatPath.TrimStart('/')))
        {
            Content = new StringContent(BuildBody(execution), Encoding.UTF8, "application/json"),
        };

        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        HttpResponseMessage? response = null;
        AdapterFailed? failure = null;

        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            failure = new AdapterFailed(ErrorCode.TargetUnavailable, "The remote service did not accept the connection.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            failure = new AdapterFailed(ErrorCode.ProviderTimeout, "The remote service did not answer within the deadline.");
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
                // Un redirect sposterebbe l'inferenza su un host che nessuno ha dichiarato.
                yield return new AdapterFailed(
                    ErrorCode.ControlUnavailable,
                    "The remote service answered with a redirect, which is not followed.");

                yield break;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                yield return new AdapterFailed(ErrorCode.AuthUnavailable, "The remote service refused the credentials.") { HttpStatus = (int)response.StatusCode };
                yield break;
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests)
            {
                yield return new AdapterFailed(ErrorCode.QuotaExhausted, "The remote service reported a rate limit.") { HttpStatus = (int)response.StatusCode };
                yield break;
            }

            // 402: il fornitore dice che il credito e' finito (23 settembre 2026). Quota esaurita, non un guasto.
            if (response.StatusCode is HttpStatusCode.PaymentRequired)
            {
                yield return new AdapterFailed(ErrorCode.QuotaExhausted, "The remote service reported that the credit is exhausted.") { HttpStatus = (int)response.StatusCode, QuotaLasting = true };
                yield break;
            }

            if (response.StatusCode is not HttpStatusCode.OK)
            {
                yield return new AdapterFailed(ErrorCode.TargetUnavailable, "The remote service answered with an error status.") { HttpStatus = (int)response.StatusCode };
                yield break;
            }

            string? body = null;
            try
            {
                body = await BoundedHttpContent.ReadUtf8Async(response.Content, BoundedHttpContent.JsonBytes(execution.Limits.MaxOutputBytes), cancellationToken).ConfigureAwait(false);
            }
            catch (PaRouterException)
            {
                failure = new AdapterFailed(ErrorCode.ProtocolError, "The remote response exceeded its limit or was malformed.");
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                failure = new AdapterFailed(ErrorCode.ProtocolError, "The remote response was interrupted.");
            }
            if (failure is not null) { yield return failure; yield break; }

            foreach (AdapterEvent adapterEvent in Parse(body!))
            {
                yield return adapterEvent;
            }
        }
    }

    /// <summary>
    /// Traduce la risposta. Un documento senza contenuto non e' un completamento vuoto: e' una
    /// risposta che non si e' capita, e va dichiarata tale.
    /// </summary>
    private static IEnumerable<AdapterEvent> Parse(string body)
    {
        string? text = null;
        List<ToolCall> calls = [];
        long? inputTokens = null;
        long? outputTokens = null;
        bool truncated = false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object
                || !root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind is not JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || choices[0].ValueKind is not JsonValueKind.Object
                || !choices[0].TryGetProperty("message", out JsonElement message)
                || message.ValueKind is not JsonValueKind.Object)
            {
                return [new AdapterFailed(ErrorCode.ProtocolError, "The remote answer has no readable content.")];
            }

            if (message.TryGetProperty("content", out JsonElement content) && content.ValueKind is JsonValueKind.String)
            {
                text = content.GetString();
            }

            if (message.TryGetProperty("tool_calls", out JsonElement requested) && requested.ValueKind is JsonValueKind.Array)
            {
                foreach (JsonElement call in requested.EnumerateArray())
                {
                    if (call.ValueKind is not JsonValueKind.Object
                        || !call.TryGetProperty("id", out JsonElement id)
                        || id.ValueKind is not JsonValueKind.String
                        || !call.TryGetProperty("function", out JsonElement function)
                        || function.ValueKind is not JsonValueKind.Object
                        || !function.TryGetProperty("name", out JsonElement name)
                        || name.ValueKind is not JsonValueKind.String)
                    {
                        return [new AdapterFailed(ErrorCode.ProtocolError, "The remote answer has a malformed tool call.")];
                    }

                    // Gli argomenti sono una stringa JSON scritta dal modello: si trasportano come sono, e il
                    // coordinatore decide se sono un oggetto valido.
                    string arguments = function.TryGetProperty("arguments", out JsonElement value)
                        ? value.ValueKind is JsonValueKind.String ? value.GetString()! : value.GetRawText()
                        : "{}";

                    calls.Add(new ToolCall(id.GetString()!, name.GetString()!, arguments));
                }
            }

            // `length`: il tetto di uscita ha fermato la risposta, che non e' finita.
            truncated = choices[0].TryGetProperty("finish_reason", out JsonElement finish)
                && finish.ValueKind is JsonValueKind.String
                && string.Equals(finish.GetString(), "length", StringComparison.Ordinal);

            if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind is JsonValueKind.Object)
            {
                inputTokens = ReadTokens(usage, "prompt_tokens");
                outputTokens = ReadTokens(usage, "completion_tokens");
            }
        }
        catch (JsonException)
        {
            return [new AdapterFailed(ErrorCode.ProtocolError, "The remote answer is not valid JSON.")];
        }

        if (text is null && calls.Count == 0)
        {
            return [new AdapterFailed(ErrorCode.ProtocolError, "The remote answer has no readable content.")];
        }

        List<AdapterEvent> events = [];

        if (text is not null && (text.Length > 0 || calls.Count == 0))
        {
            events.Add(new AdapterOutputChunk(text));
        }

        if (calls.Count > 0)
        {
            events.Add(new AdapterToolCalls(calls));
        }

        events.Add(new AdapterUsage(new UsageReport(inputTokens, outputTokens, BilledCost: null) { Truncated = truncated }));
        events.Add(new AdapterCompleted(0));

        return events;
    }

    private static long? ReadTokens(JsonElement usage, string name) =>
        BoundedHttpContent.NonNegativeCount(usage, name);

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    private static string BuildBody(AuthorizedExecution execution)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", execution.Model);
            writer.WriteNumber("max_completion_tokens", GenerationLimits.OutputTokens(execution.Limits.MaxOutputBytes, execution.MaxOutputTokens));

            // Nessuno streaming: una risposta parziale complicherebbe la regola sul ripiego senza
            // servire a un router che non trasmette testo al client.
            writer.WriteBoolean("stream", false);

            if (execution.Payload.Tools is { Count: > 0 } tools)
            {
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
                writer.WriteStartObject();
                writer.WriteString("role", "system");
                writer.WriteString("content", Encoding.UTF8.GetString(execution.Payload.Instructions.Span));
                writer.WriteEndObject();
            }

            // Gli stessi byte esaminati, divisi fra i messaggi: i turni precedenti come turni (ChatTranscript, S116).
            foreach (ChatMessage message in ChatTranscript.Split(execution.Payload))
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WriteString("content", message.Text);
                writer.WriteEndObject();
            }

            foreach (ToolRound round in execution.Rounds)
            {
                // Il messaggio dell'assistente riporta le chiamate con il loro id; ogni risultato e' un
                // messaggio "tool" con lo stesso id.
                writer.WriteStartObject();
                writer.WriteString("role", "assistant");

                if (string.IsNullOrEmpty(round.AssistantText))
                {
                    writer.WriteNull("content");
                }
                else
                {
                    writer.WriteString("content", round.AssistantText);
                }

                writer.WriteStartArray("tool_calls");

                foreach (ToolCall call in round.Calls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", call.CallId);
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", call.Name);
                    writer.WriteString("arguments", call.ArgumentsJson);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();

                foreach (ToolCallResult result in round.Results)
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", "tool");
                    writer.WriteString("tool_call_id", result.CallId);
                    writer.WriteString("content", result.Content);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
