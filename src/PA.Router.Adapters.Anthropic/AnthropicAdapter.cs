using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Adapters.Anthropic;

/// <summary>
/// Adapter dell'API Messages di Anthropic.
///
/// Esiste perche' quel servizio **non** e' compatibile OpenAI: percorso diverso
/// (<c>/v1/messages</c>), autenticazione diversa (<c>x-api-key</c> invece di <c>Bearer</c>),
/// versione del contratto obbligatoria in un'intestazione, tetto di uscita obbligatorio nel corpo
/// e risposta a blocchi invece che a scelte. Farlo passare per l'adapter compatibile avrebbe
/// significato dichiarare una compatibilita' che non c'e'.
///
/// La forma della richiesta e della risposta viene dalla documentazione pubblica del fornitore.
/// Da questa macchina il servizio **non e' mai stato contattato**: serve un dominio, una chiave e
/// quota dell'operatore. Le prove offline coprono la traduzione, non l'accordo con il servizio
/// vero, e finche' quello smoke non esiste il servizio resta disabilitato.
///
/// Come l'adapter compatibile: nessuno streaming, nessun redirect seguito, lo standard dei codici
/// di errore del router al posto di quelli del fornitore.
/// </summary>
public sealed class AnthropicAdapter : IExecutionAdapter, IDisposable
{
    /// <summary>Percorso della chat nell'API Messages.</summary>
    public const string MessagesPath = "/v1/messages";

    /// <summary>Intestazione che porta la chiave. Non e' <c>Authorization</c>.</summary>
    public const string KeyHeader = "x-api-key";

    /// <summary>Intestazione della versione del contratto, obbligatoria su ogni richiesta.</summary>
    public const string VersionHeader = "anthropic-version";

    /// <summary>
    /// Versione del contratto dichiarata. E' una costante del fornitore, non una preferenza: va
    /// cambiata solo insieme alla verifica della forma di richiesta e risposta.
    /// </summary>
    public const string VersionValue = "2023-06-01";

    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly Func<string, string?> secrets;

    /// <param name="secrets">
    /// Risolutore dei segreti per nome. L'adapter riceve un nome, non un valore: i nomi possono
    /// stare in un DTO, i valori no.
    /// </param>
    public AnthropicAdapter(Func<string, string?> secrets)
        : this(PlacementRules.CreateModelClient(Placement.ThirdParty), secrets, ownsClient: true)
    {
    }

    /// <param name="client">Client gia' configurato; i test ne passano uno con handler locale.</param>
    /// <param name="secrets">Risolutore dei segreti per nome.</param>
    /// <param name="ownsClient">Vero se questo adapter deve rilasciare il client.</param>
    public AnthropicAdapter(HttpClient client, Func<string, string?> secrets, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(secrets);

        this.client = client;
        this.secrets = secrets;
        this.ownsClient = ownsClient;
    }

    /// <inheritdoc />
    public AdapterKind AdapterId => AdapterKind.Anthropic;

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

        if (execution.MaxOutputTokens <= 0)
        {
            // L'API pretende il tetto nella richiesta. Dedurlo dai byte della policy vorrebbe dire
            // inventare un rapporto fra byte e token: il numero lo dichiara il registro o non si
            // parte.
            yield return new AdapterFailed(
                ErrorCode.TargetUnavailable,
                "The service declares no output ceiling in tokens, which this API requires.");

            yield break;
        }

        if (execution.SecretRef is not string reference)
        {
            // Non esiste una chiamata anonima: senza chiave non c'e' un percorso degradato.
            yield return new AdapterFailed(
                ErrorCode.AuthUnavailable,
                "The service declares no secret, and this API has no anonymous call.");

            yield break;
        }

        if (secrets(reference) is not string key)
        {
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
        string key,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(execution.Endpoint!, MessagesPath.TrimStart('/')))
        {
            Content = new StringContent(BuildBody(execution), Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(KeyHeader, key);
        request.Headers.Add(VersionHeader, VersionValue);

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
            if ((int)response!.StatusCode is >= 300 and < 400)
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

            // Credito finito (23 settembre 2026): Anthropic risponde 400 con un messaggio sul credito, altri fornitori 402.
            // E' una quota esaurita, non un servizio guasto: il codice fa comunque provare il successivo, ma il racconto
            // deve dire all'operatore che cosa ricaricare invece di «non disponibile».
            QuotaNotice? notice = response.StatusCode is HttpStatusCode.PaymentRequired
                ? new QuotaNotice(null)
                : response.StatusCode is HttpStatusCode.BadRequest
                    ? await QuotaNoticeAsync(response, cancellationToken).ConfigureAwait(false)
                    : null;

            if (notice is not null)
            {
                yield return new AdapterFailed(ErrorCode.QuotaExhausted, "The remote service reported that the credit is exhausted.")
                {
                    HttpStatus = (int)response.StatusCode,
                    QuotaLasting = true,
                    QuotaUntil = notice.Until,
                };

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

    /// <summary>Quota finita, con la data in cui torna se il fornitore la dice.</summary>
    private sealed record QuotaNotice(DateTimeOffset? Until);

    /// <summary>
    /// La quota finita letta dal corpo di un errore: credito esaurito («credit balance») o limite di spesa dell'operatore
    /// («You have reached your specified API usage limits. You will regain access on 2026-10-01 at 00:00 UTC.», osservato
    /// il 24 settembre 2026). Si legge solo il messaggio dell'errore, e non finisce da nessuna parte: serve a scegliere il
    /// codice e la data della pausa. Null se l'errore e' un altro.
    /// </summary>
    private static async Task<QuotaNotice?> QuotaNoticeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string body = await BoundedHttpContent.ReadUtf8Async(response.Content, BoundedHttpContent.ErrorBytes, cancellationToken).ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out JsonElement error)
                || error.ValueKind is not JsonValueKind.Object
                || !error.TryGetProperty("message", out JsonElement message)
                || message.ValueKind is not JsonValueKind.String)
            {
                return null;
            }

            string text = message.GetString()!;

            if (!text.Contains("credit balance", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("usage limits", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new QuotaNotice(QuotaDates.RegainAccess(text));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception ex) when (ex is PaRouterException or IOException or HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Traduce la risposta. Il contenuto e' un elenco di blocchi tipati: contano quelli di tipo
    /// <c>text</c>, concatenati nell'ordine in cui arrivano. Un documento senza alcun blocco di
    /// testo non e' una risposta vuota: e' una risposta che non si e' capita.
    /// </summary>
    private static IEnumerable<AdapterEvent> Parse(string body)
    {
        StringBuilder text = new();
        List<ToolCall> calls = [];
        long? inputTokens = null;
        long? outputTokens = null;
        bool truncated = false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object
                || !root.TryGetProperty("content", out JsonElement content)
                || content.ValueKind is not JsonValueKind.Array)
            {
                return [new AdapterFailed(ErrorCode.ProtocolError, "The remote answer has no readable content.")];
            }

            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.ValueKind is not JsonValueKind.Object)
                {
                    return [new AdapterFailed(ErrorCode.ProtocolError, "The remote answer has a malformed content block.")];
                }
                string? type = block.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind is JsonValueKind.String
                    ? typeElement.GetString()
                    : null;

                if (string.Equals(type, "text", StringComparison.Ordinal)
                    && block.TryGetProperty("text", out JsonElement value)
                    && value.ValueKind is JsonValueKind.String)
                {
                    text.Append(value.GetString());
                }
                else if (string.Equals(type, "tool_use", StringComparison.Ordinal))
                {
                    // Blocco tool_use: id, name e input, un oggetto conforme allo schema dello strumento.
                    if (!block.TryGetProperty("id", out JsonElement id)
                        || id.ValueKind is not JsonValueKind.String
                        || !block.TryGetProperty("name", out JsonElement name)
                        || name.ValueKind is not JsonValueKind.String)
                    {
                        return [new AdapterFailed(ErrorCode.ProtocolError, "The remote answer has a malformed tool call.")];
                    }

                    string input = block.TryGetProperty("input", out JsonElement arguments) ? arguments.GetRawText() : "{}";

                    calls.Add(new ToolCall(id.GetString()!, name.GetString()!, input));
                }
            }

            if (text.Length == 0 && calls.Count == 0)
            {
                return [new AdapterFailed(ErrorCode.ProtocolError, "The remote answer has no readable content.")];
            }

            // `max_tokens`: il tetto di uscita ha fermato la risposta, che non e' finita.
            truncated = root.TryGetProperty("stop_reason", out JsonElement stop)
                && stop.ValueKind is JsonValueKind.String
                && string.Equals(stop.GetString(), "max_tokens", StringComparison.Ordinal);

            if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind is JsonValueKind.Object)
            {
                // Con la cache accesa `input_tokens` conta solo cio' che non e' passato dalla cache: i token scritti
                // e quelli riletti stanno in due campi a parte. Si sommano, altrimenti il consumo mostrato sarebbe
                // piu' basso di quello vero proprio quando l'operatore ha acceso qualcosa per spendere meno.
                inputTokens = ReadTokens(usage, "input_tokens");

                if (inputTokens is long counted)
                {
                    long? creation = OptionalTokens(usage, "cache_creation_input_tokens");
                    long? read = OptionalTokens(usage, "cache_read_input_tokens");
                    inputTokens = creation is long created && read is long cached
                        && counted <= long.MaxValue - created && counted + created <= long.MaxValue - cached
                            ? counted + created + cached : null;
                }
                outputTokens = ReadTokens(usage, "output_tokens");
            }
        }
        catch (JsonException)
        {
            return [new AdapterFailed(ErrorCode.ProtocolError, "The remote answer is not valid JSON.")];
        }

        List<AdapterEvent> events = [];

        if (text.Length > 0)
        {
            events.Add(new AdapterOutputChunk(text.ToString()));
        }

        if (calls.Count > 0)
        {
            events.Add(new AdapterToolCalls(calls));
        }

        events.Add(new AdapterUsage(new UsageReport(inputTokens, outputTokens, BilledCost: null) { Truncated = truncated }));
        events.Add(new AdapterCompleted(0));

        return events;
    }

    private static bool HasHistory(AuthorizedExecution execution) => ChatTranscript.Split(execution.Payload).Count > 1;

    /// <summary>
    /// Un blocco di testo nella forma dell'API Messages. <c>cache_control</c> chiede al fornitore di conservare per
    /// alcuni minuti tutto cio' che precede e comprende questo blocco: si scrive solo se l'operatore lo ha chiesto.
    /// </summary>
    private static void WriteTextBlock(Utf8JsonWriter writer, string text, bool cached)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", text);

        if (cached)
        {
            writer.WriteStartObject("cache_control");
            writer.WriteString("type", "ephemeral");
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static long? ReadTokens(JsonElement usage, string name) =>
        BoundedHttpContent.NonNegativeCount(usage, name);

    private static long? OptionalTokens(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out _) ? ReadTokens(usage, name) : 0;

    private static string BuildBody(AuthorizedExecution execution)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", execution.Model);

            // Obbligatorio in questa API, e viene dal registro: nessun valore predefinito nascosto.
            writer.WriteNumber("max_tokens", GenerationLimits.OutputTokens(execution.Limits.MaxOutputBytes, execution.MaxOutputTokens));

            // Nessuno streaming: una risposta parziale complicherebbe la regola sul ripiego senza
            // servire a un router che non trasmette testo al client.
            writer.WriteBoolean("stream", false);

            if (execution.Payload.HasInstructions)
            {
                // In questa API le istruzioni di sistema non sono un messaggio: stanno nel campo
                // di primo livello `system`, e i messaggi alternano solo utente e assistente.
                string system = Encoding.UTF8.GetString(execution.Payload.Instructions.Span);

                if (execution.PromptCaching && !HasHistory(execution))
                {
                    // Senza cronologia le istruzioni sono l'unica parte che si ripete da un turno all'altro.
                    writer.WriteStartArray("system");
                    WriteTextBlock(writer, system, cached: true);
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WriteString("system", system);
                }
            }

            if (execution.Payload.Tools is { Count: > 0 } tools)
            {
                // Forma dell'API Messages: name, description, input_schema.
                writer.WriteStartArray("tools");

                foreach (ToolDefinition tool in tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("input_schema");
                    writer.WriteRawValue(tool.InputSchemaJson);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteStartArray("messages");

            // Gli stessi byte esaminati, divisi fra i messaggi: i turni precedenti come turni (ChatTranscript, S116).
            // Il primo e' sempre di chi chiede, come questo contratto pretende.
            IReadOnlyList<ChatMessage> transcript = ChatTranscript.Split(execution.Payload);

            for (int index = 0; index < transcript.Count; index++)
            {
                writer.WriteStartObject();
                writer.WriteString("role", transcript[index].Role);

                // Il punto di cache va sull'ultimo messaggio della cronologia: copre istruzioni e turni precedenti, che
                // al turno dopo saranno identici, e lascia fuori la richiesta nuova, che non lo sara' mai (ADR-022).
                if (execution.PromptCaching && transcript.Count > 1 && index == transcript.Count - 2)
                {
                    writer.WriteStartArray("content");
                    WriteTextBlock(writer, transcript[index].Text, cached: true);
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WriteString("content", transcript[index].Text);
                }

                writer.WriteEndObject();
            }

            foreach (ToolRound round in execution.Rounds)
            {
                // L'assistente con i blocchi tool_use, poi un messaggio utente che comincia con i blocchi
                // tool_result: il fornitore pretende che i risultati seguano subito le chiamate.
                writer.WriteStartObject();
                writer.WriteString("role", "assistant");
                writer.WriteStartArray("content");

                if (!string.IsNullOrEmpty(round.AssistantText))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", round.AssistantText);
                    writer.WriteEndObject();
                }

                foreach (ToolCall call in round.Calls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_use");
                    writer.WriteString("id", call.CallId);
                    writer.WriteString("name", call.Name);
                    writer.WritePropertyName("input");
                    writer.WriteRawValue(call.ArgumentsObjectJson);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();

                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteStartArray("content");

                foreach (ToolCallResult result in round.Results)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_result");
                    writer.WriteString("tool_use_id", result.CallId);
                    writer.WriteString("content", result.Content);

                    if (result.IsError)
                    {
                        writer.WriteBoolean("is_error", true);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
