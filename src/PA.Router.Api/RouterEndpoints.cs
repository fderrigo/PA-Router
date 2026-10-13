using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Sdk;

namespace PA.Router.Api;

/// <summary>
/// Endpoint HTTP di PA Router (03-specs/HEADLESS-API-SDK.md, 06-contracts/openapi.json).
///
/// Il confine e' stretto di proposito: solo loopback, un token di servizio su ogni route — health
/// compresa — e un token di sessione segreto per tutto cio' che riguarda sessioni ed esecuzioni.
/// Sessione inesistente e token sbagliato danno lo stesso 404: chi chiede non deve poter dedurre
/// quale dei due casi sia.
///
/// Non esiste un endpoint che scelga profilo, target o capability, che installi estensioni o che
/// esegua comandi. Il backend cliente autentica i propri utenti e non deve fidarsi di un
/// <c>sessionId</c> che arriva da un browser.
/// </summary>
public static class RouterEndpoints
{
    /// <summary>Byte massimi di un corpo accettato.</summary>
    public const int MaxBodyBytes = 600 * 1024;

    /// <summary>Richieste al minuto per token di servizio.</summary>
    public const int RequestsPerMinute = 60;

    /// <summary>Connessioni SSE contemporanee ammesse.</summary>
    public const int MaxEventStreams = 8;

    /// <summary>Intervallo dell'heartbeat SSE.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    /// <summary>Monta gli endpoint della versione 1 sull'applicazione indicata.</summary>
    public static void Map(IEndpointRouteBuilder endpoints, IPaRouterRuntime runtime, string serviceToken)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceToken);

        RateLimiter limiter = new(RequestsPerMinute);
        StreamCounter streams = new(MaxEventStreams);

        endpoints.MapGet("/health", (HttpContext context) =>
        {
            if (Reject(context, serviceToken, limiter) is IResult rejected)
            {
                return rejected;
            }

            // Nessun dettaglio su percorsi o provider: solo se il router e' pronto.
            return Results.Json(new JsonObject { ["status"] = "ready" }, statusCode: StatusCodes.Status200OK);
        });

        endpoints.MapPost("/v1/sessions", async (HttpContext context) =>
        {
            if (Reject(context, serviceToken, limiter) is IResult rejected)
            {
                return rejected;
            }

            CreatedSession session = await runtime.CreateSessionAsync(context.RequestAborted).ConfigureAwait(false);

            return Results.Json(
                new JsonObject
                {
                    ["sessionId"] = session.SessionId,
                    // Restituito una volta sola: il router ne conserva l'HMAC, non il valore.
                    ["sessionToken"] = session.SessionToken,
                    ["profileId"] = session.ProfileId,
                    ["contentMode"] = session.ContentMode.ToWire(),
                },
                statusCode: StatusCodes.Status201Created);
        });

        endpoints.MapDelete("/v1/sessions/{id}", async (HttpContext context, string id) =>
        {
            if (Reject(context, serviceToken, limiter) is IResult rejected)
            {
                return rejected;
            }

            if (!Authorized(context, runtime, id))
            {
                return NotFound(context);
            }

            bool deleted = await runtime.DeleteSessionAsync(id, context.RequestAborted).ConfigureAwait(false);

            return deleted
                ? Results.NoContent()
                : Problem(context, StatusCodes.Status409Conflict, "SESSION_BUSY", "An execution of this session is still running.");
        });

        endpoints.MapPost("/v1/executions", async (HttpContext context) =>
        {
            if (Reject(context, serviceToken, limiter) is IResult rejected)
            {
                return rejected;
            }

            if (context.Request.ContentLength > MaxBodyBytes)
            {
                return Problem(context, StatusCodes.Status413PayloadTooLarge, "OUTPUT_LIMIT", "The request body is too large.");
            }

            string? key = context.Request.Headers["Idempotency-Key"].FirstOrDefault();

            if (!Guid.TryParse(key, out _))
            {
                return Problem(context, StatusCodes.Status400BadRequest, "INPUT_INVALID", "A UUID Idempotency-Key header is required.");
            }

            (JsonNode? body, bool tooLarge) = await ReadBodyAsync(context).ConfigureAwait(false);

            if (tooLarge)
            {
                return Problem(context, StatusCodes.Status413PayloadTooLarge, "OUTPUT_LIMIT", "The request body is too large.");
            }

            // Un campo che non e' testo e' una richiesta fuori dal contratto, non un guasto del router.
            if (body is not JsonObject document
                || Text(document["sessionId"]) is not string sessionId
                || Text(document["input"]) is not string input
                || input.Length == 0)
            {
                return Problem(context, StatusCodes.Status400BadRequest, "INPUT_INVALID", "The request does not match the contract.");
            }

            if (!Authorized(context, runtime, sessionId))
            {
                return NotFound(context);
            }

            string? instructions = null;

            if (document["instructions"] is JsonNode instructionsNode)
            {
                // Le istruzioni sono facoltative, ma quando ci sono devono essere testo: un oggetto o un
                // numero al loro posto e' una richiesta che non rispetta il contratto.
                if (instructionsNode.GetValueKind() is not System.Text.Json.JsonValueKind.String
                    || instructionsNode.GetValue<string>() is not { Length: > 0 } text)
                {
                    return Problem(context, StatusCodes.Status400BadRequest, "INPUT_INVALID", "The instructions must be a non-empty string.");
                }

                instructions = text;
            }

            if (!TryParseTools(document["tools"], out ToolsRequest? tools))
            {
                return Problem(context, StatusCodes.Status400BadRequest, "INPUT_INVALID", "The tools do not match the contract.");
            }

            List<ContextItem> items = [];

            if (document["contextItems"] is JsonNode contextNode && contextNode is not JsonArray)
            {
                return Problem(context, StatusCodes.Status400BadRequest, "INPUT_INVALID", "The context items must be an array.");
            }

            if (document["contextItems"] is JsonArray array)
            {
                foreach (JsonNode? item in array)
                {
                    if (item is not JsonObject entry
                        || Text(entry["id"]) is not string itemId
                        || Text(entry["text"]) is not string text
                        || Text(entry["provenance"]) is not string provenance
                        || Text(entry["classification"]) is not string classification
                        || !WireEnum.TryParse(classification, out DataClass declared))
                    {
                        return Problem(context, StatusCodes.Status400BadRequest, "INPUT_INVALID", "A context item does not match the contract.");
                    }

                    // La provenienza dichiarata dal backend resta un'etichetta: il contenuto entra
                    // comunque come dato non fidato e riattraversa privacy e policy.
                    _ = provenance;

                    items.Add(new ContextItem(itemId, text, Provenance.Workspace, declared));
                }
            }

            try
            {
                ExecutionSnapshot snapshot = await runtime
                    .ExecuteAsync(sessionId, new ApplicationRequest(input, items, instructions, tools), key!, context.RequestAborted)
                    .ConfigureAwait(false);

                JsonObject accepted = new()
                {
                    ["executionId"] = snapshot.ExecutionId,
                    ["status"] = snapshot.State.ToWire(),
                    ["eventsUrl"] = "/v1/executions/" + snapshot.ExecutionId + "/events",
                    ["memoryStatus"] = snapshot.MemoryStatus.ToWire(),
                };

                if (snapshot.Tools is ToolOffer offer)
                {
                    // Quali strumenti il modello vedra' e quali no, con il motivo: l'applicazione sa subito che cosa
                    // dichiarare.
                    accepted["tools"] = OfferJson(offer);
                }

                return Results.Json(accepted, statusCode: StatusCodes.Status202Accepted);
            }
            catch (ToolPreparationException ex)
            {
                // Rifiuto prima dell'esecuzione: che cosa, perche' e che cosa fare, senza contenuti (ADR-020).
                return ToolsProblem(context, ex);
            }
            catch (PaRouterException ex) when (ex.Metadata.TryGetValue("reason", out string? reason) && reason is "idempotency_conflict")
            {
                return Problem(context, StatusCodes.Status409Conflict, "IDEMPOTENCY_CONFLICT", "That key was used with a different request.");
            }
            catch (PaRouterException ex) when (ex.Code is ErrorCode.Busy or ErrorCode.SessionBusy or ErrorCode.WorkspaceBusy)
            {
                context.Response.Headers["Retry-After"] = "1";

                return Problem(context, StatusCodes.Status429TooManyRequests, ex.Code.ToWire(), "The runtime is busy.");
            }
            catch (PaRouterException ex)
            {
                // Un guasto del runtime — configurazione non valida, store non disponibile — non e' un errore del
                // contratto e non deve diventare un 500 senza spiegazione: torna con il suo codice stabile.
                return Problem(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    ex.Code.ToWire(),
                    "The runtime cannot accept the request now.");
            }
        });

        endpoints.MapGet("/v1/executions/{id}", (HttpContext context, string id) =>
        {
            if (Reject(context, serviceToken, limiter) is IResult rejected)
            {
                return rejected;
            }

            ExecutionSnapshot snapshot;

            try
            {
                snapshot = runtime.GetExecution(id);
            }
            catch (PaRouterException)
            {
                return NotFound(context);
            }

            if (!Authorized(context, runtime, snapshot.SessionId))
            {
                return NotFound(context);
            }

            // Un'esecuzione fallita resta 200 con lo stato e il codice: il fallimento del provider
            // non diventa un errore HTTP dell'API.
            return Results.Json(
                WithToolsUnused(
                new JsonObject
                {
                    ["executionId"] = snapshot.ExecutionId,
                    ["sessionId"] = snapshot.SessionId,
                    ["state"] = snapshot.State.ToWire(),
                    ["output"] = snapshot.State is ExecutionState.Completed ? snapshot.Output : null,
                    ["errorCode"] = snapshot.ErrorCode?.ToWire(),
                    ["contentAvailability"] = snapshot.ContentAvailability.ToWire(),
                    ["memoryStatus"] = snapshot.MemoryStatus.ToWire(),
                    // Riscontro senza contenuti su un rifiuto durante la conversazione: strumento, sorgente, classe,
                    // limite o motivo (ADR-020).
                    ["errorDetail"] = DetailJson(snapshot.ErrorDetail),
                },
                snapshot.ToolsUnused,
                snapshot.WeakPerimeter,
                snapshot.BelowDemand),
                statusCode: StatusCodes.Status200OK);
        });

        endpoints.MapPost("/v1/executions/{id}/cancel", (HttpContext context, string id) =>
        {
            if (Reject(context, serviceToken, limiter) is IResult rejected)
            {
                return rejected;
            }

            ExecutionSnapshot snapshot;

            try
            {
                snapshot = runtime.GetExecution(id);
            }
            catch (PaRouterException)
            {
                return NotFound(context);
            }

            if (!Authorized(context, runtime, snapshot.SessionId))
            {
                return NotFound(context);
            }

            runtime.Cancel(id);

            return Results.Accepted();
        });

        endpoints.MapGet("/v1/executions/{id}/events", async (HttpContext context, string id) =>
        {
            if (Reject(context, serviceToken, limiter) is IResult rejected)
            {
                return rejected;
            }

            ExecutionSnapshot snapshot;

            try
            {
                snapshot = runtime.GetExecution(id);
            }
            catch (PaRouterException)
            {
                return NotFound(context);
            }

            if (!Authorized(context, runtime, snapshot.SessionId))
            {
                return NotFound(context);
            }

            int after = 0;
            string? cursor = context.Request.Headers["Last-Event-ID"].FirstOrDefault();

            if (cursor is not null && (!int.TryParse(cursor, CultureInfo.InvariantCulture, out after) || after < 0))
            {
                return Problem(context, StatusCodes.Status400BadRequest, "INPUT_INVALID", "The cursor is not a sequence number.");
            }

            if (after > 0 && runtime.EventRange(id) is (int first, int last))
            {
                if (after > last)
                {
                    // Una sequenza mai esistita: il client ha inventato il cursore, oppure lo ha
                    // preso da un'altra esecuzione. Non e' una attesa, e' una richiesta sbagliata.
                    return Problem(
                        context,
                        StatusCodes.Status400BadRequest,
                        "INPUT_INVALID",
                        "The cursor is ahead of the events of this execution.");
                }

                if (first > after + 1)
                {
                    // Gli eventi successivi a quel cursore non esistono piu': la retention li ha
                    // rimossi. Uno stream vuoto lascerebbe il client in attesa di qualcosa che non
                    // arrivera'; dirlo lo manda a leggere lo stato, che e' l'unica cosa che resta.
                    return Problem(
                        context,
                        StatusCodes.Status410Gone,
                        "EVENTS_EXPIRED",
                        "The events after that cursor are no longer retained: read the execution state instead.");
                }
            }

            if (!streams.TryEnter())
            {
                return Problem(context, StatusCodes.Status429TooManyRequests, "BUSY", "Too many event streams.");
            }

            try
            {
                context.Response.Headers.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";

                await SseStream.WriteAsync(context.Response, runtime.ObserveAsync(id, after, context.RequestAborted), async stored =>
                {
                    // Solo stato e sequenza: nessun testo, nessun ragionamento, nessun percorso.
                    JsonObject payload = new()
                    {
                        ["executionId"] = stored.ExecutionId,
                        ["state"] = stored.State.ToWire(),
                        ["sequence"] = stored.Sequence,
                        ["timestamp"] = stored.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                    };

                    if (stored.State is ExecutionState.Denied or ExecutionState.Failed or ExecutionState.Cancelled or ExecutionState.Uncertain
                        && await TerminalSnapshotAsync(runtime, id, context.RequestAborted).ConfigureAwait(false) is { ErrorCode: ErrorCode code } final)
                    {
                        // Sull'evento terminale il codice e il riscontro, senza contenuti: l'applicazione sa che cosa e'
                        // successo senza un'altra richiesta (ADR-020).
                        payload["errorCode"] = code.ToWire();

                        if (DetailJson(final.ErrorDetail) is JsonObject detail)
                        {
                            payload["errorDetail"] = detail;
                        }
                    }

                    if (stored.State is ExecutionState.Completed
                        && await TerminalSnapshotAsync(runtime, id, context.RequestAborted).ConfigureAwait(false) is { } completed)
                    {
                        if (completed.ToolsUnused) { payload["toolsUnused"] = true; }
                        if (completed.WeakPerimeter) { payload["weakPerimeter"] = true; }
                        if (completed.BelowDemand) { payload["belowDemand"] = true; }
                    }

                    await context.Response
                        .WriteAsync(
                            "id: " + stored.Sequence.ToString(CultureInfo.InvariantCulture) + "\n"
                            + "event: state\n"
                            + "data: " + payload.ToJsonString() + "\n\n",
                            context.RequestAborted)
                        .ConfigureAwait(false);

                }, Heartbeat, context.RequestAborted).ConfigureAwait(false);

                return Results.Empty;
            }
            finally
            {
                streams.Exit();
            }
        });
    }

    /// <summary>
    /// Aggiunge <c>toolsUnused</c> solo quando vale: la richiesta offriva strumenti e la risposta non ne ha usato
    /// nessuno (S47). Negli altri casi il campo non compare.
    /// </summary>
    private static JsonObject WithToolsUnused(JsonObject item, bool toolsUnused, bool weakPerimeter = false, bool belowDemand = false)
    {
        if (toolsUnused)
        {
            item["toolsUnused"] = true;
        }

        // Una risposta dichiarata piu' debole per regola del profilo (ADR-029): come toolsUnused, compare solo quando vale.
        if (weakPerimeter)
        {
            item["weakPerimeter"] = true;
        }

        // Nessun servizio ammesso bastava per il profilo di domanda e ha risposto il piu' capace (ADR-031).
        if (belowDemand)
        {
            item["belowDemand"] = true;
        }

        return item;
    }

    /// <summary>Dettaglio del riscontro in ordine di chiave, null se assente.</summary>
    private static JsonObject? DetailJson(IReadOnlyDictionary<string, string>? detail)
    {
        if (detail is null || detail.Count == 0)
        {
            return null;
        }

        JsonObject item = [];

        foreach ((string key, string value) in detail.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            item[key] = value;
        }

        return item;
    }

    /// <summary>
    /// Stato con cui leggere l'esito dell'evento terminale. L'evento si scrive un istante prima che il turno consegni
    /// l'esito al runtime: qui si aspetta quell'istante, con un tetto, invece di mandare un terminale senza codice.
    /// </summary>
    private static async Task<ExecutionSnapshot> TerminalSnapshotAsync(IPaRouterRuntime runtime, string id, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            ExecutionSnapshot snapshot = runtime.GetExecution(id);

            if (snapshot.State is not ExecutionState.Accepted)
            {
                return snapshot;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        return runtime.GetExecution(id);
    }

    /// <summary>Riscontro su strumenti che non si possono usare: 422 con un problema per strumento o sorgente.</summary>
    private static IResult ToolsProblem(HttpContext context, ToolPreparationException exception) =>
        Results.Json(
            new JsonObject
            {
                ["type"] = "about:blank",
                ["title"] = "The tools of the request cannot be used.",
                ["status"] = StatusCodes.Status422UnprocessableEntity,
                ["code"] = exception.Code.ToWire(),
                ["traceId"] = context.TraceIdentifier,
                ["problems"] = new JsonArray([.. exception.Problems.Select(problem => (JsonNode?)ProblemJson(problem))]),
            },
            statusCode: StatusCodes.Status422UnprocessableEntity,
            contentType: "application/problem+json");

    /// <summary>Un problema senza contenuti: codice, sorgente, strumento, campo, motivo, impronta osservata.</summary>
    internal static JsonObject ProblemJson(ToolProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        JsonObject item = new() { ["code"] = problem.Code.ToWire() };

        if (problem.SourceId is not null)
        {
            item["source"] = problem.SourceId;
        }

        if (problem.Tool is not null)
        {
            item["tool"] = problem.Tool;
        }

        if (problem.Field is not null)
        {
            item["field"] = problem.Field;
        }

        if (problem.Reason is not null)
        {
            item["reason"] = problem.Reason;
        }

        if (problem.Fingerprint is not null)
        {
            item["fingerprint"] = problem.Fingerprint;
        }

        return item;
    }

    private static JsonObject OfferJson(ToolOffer offer) =>
        new()
        {
            ["offered"] = new JsonArray([.. offer.Offered.Select(name => (JsonNode?)name)]),
            ["excluded"] = new JsonArray([.. offer.Excluded.Select(problem => (JsonNode?)ProblemJson(problem))]),
        };

    /// <summary>
    /// Traduce la parte <c>tools</c> della richiesta. Un campo sconosciuto o di un tipo sbagliato rende la richiesta
    /// fuori contratto: ignorarlo cambierebbe in silenzio che cosa il router fa con i dati.
    /// </summary>
    private static bool TryParseTools(JsonNode? node, out ToolsRequest? tools)
    {
        tools = null;

        if (node is null)
        {
            return true;
        }

        if (node is not JsonObject root
            || root.Any(property => property.Key is not ("sources" or "declarations" or "credentials"))
            || !TryItems(root["sources"], 16, out List<JsonObject> sourceItems)
            || !TryItems(root["declarations"], 256, out List<JsonObject> declarationItems)
            || !TryItems(root["credentials"], 16, out List<JsonObject> credentialItems))
        {
            return false;
        }

        List<ToolSourceRequest> sources = [];
        List<ToolDeclaration> declarations = [];
        List<ToolCredential> credentials = [];

        foreach (JsonObject item in sourceItems)
        {
            if (Text(item["id"]) is not string id
                || !IsSourceId(id)
                || Text(item["kind"]) is not string kindText
                || !WireEnum.TryParse(kindText, out ToolSourceKind kind)
                || Text(item["url"]) is not string urlText
                || !Uri.TryCreate(urlText, UriKind.Absolute, out Uri? url))
            {
                return false;
            }

            sources.Add(new ToolSourceRequest(id, kind, url));
        }

        foreach (JsonObject item in declarationItems)
        {
            if (Text(item["source"]) is not string source
                || Text(item["tool"]) is not string tool
                || !TryOptionalText(item["fingerprint"], out string? fingerprint)
                || !TryOptionalClass(item["accepts"], out DataClass? accepts)
                || !TryOptionalClass(item["returns"], out DataClass? returns)
                || !TryRegions(item["regions"], out HashSet<ProcessingRegion> regions)
                || !TryOptionalText(item["expiresAt"], out string? expiresText))
            {
                return false;
            }

            DateTimeOffset? expires = null;

            if (expiresText is not null)
            {
                if (!DateTimeOffset.TryParse(expiresText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
                {
                    return false;
                }

                expires = parsed;
            }

            declarations.Add(new ToolDeclaration(source, tool, fingerprint, accepts, returns, regions, expires));
        }

        foreach (JsonObject item in credentialItems)
        {
            if (Text(item["source"]) is not string source
                || Text(item["header"]) is not string header
                || ToolSourceRules.HeaderViolation(header) is not null
                || Text(item["value"]) is not { Length: > 0 and <= 8192 } value
                || value.Contains('\r')
                || value.Contains('\n'))
            {
                return false;
            }

            credentials.Add(new ToolCredential(source, header, value));
        }

        tools = new ToolsRequest(sources, declarations, credentials);

        return true;
    }

    private static bool TryItems(JsonNode? node, int maximum, out List<JsonObject> items)
    {
        items = [];

        if (node is null)
        {
            return true;
        }

        if (node is not JsonArray array || array.Count > maximum)
        {
            return false;
        }

        foreach (JsonNode? entry in array)
        {
            if (entry is not JsonObject item)
            {
                return false;
            }

            items.Add(item);
        }

        return true;
    }

    private static bool TryOptionalText(JsonNode? node, out string? text)
    {
        text = Text(node);

        return node is null || text is not null;
    }

    private static bool TryOptionalClass(JsonNode? node, out DataClass? value)
    {
        value = null;

        if (node is null)
        {
            return true;
        }

        if (Text(node) is not string text || !WireEnum.TryParse(text, out DataClass parsed))
        {
            return false;
        }

        value = parsed;

        return true;
    }

    private static bool TryRegions(JsonNode? node, out HashSet<ProcessingRegion> regions)
    {
        regions = [];

        if (node is null)
        {
            return true;
        }

        if (node is not JsonArray array)
        {
            return false;
        }

        foreach (JsonNode? entry in array)
        {
            if (Text(entry) is not string term || !Regions.IsWellFormed(Regions.Parse(term)))
            {
                return false;
            }

            regions.Add(Regions.Parse(term));
        }

        return true;
    }

    private static string? Text(JsonNode? node) =>
        node?.GetValueKind() is System.Text.Json.JsonValueKind.String ? node.GetValue<string>() : null;

    private static bool IsSourceId(string id) =>
        id.Length is >= 1 and <= 64
        && (char.IsAsciiLetterLower(id[0]) || char.IsAsciiDigit(id[0]))
        && id.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');

    /// <summary>
    /// Corpo della richiesta come JSON. Il limite si applica **mentre** si legge: un corpo a pezzi, senza lunghezza
    /// dichiarata, non viene prima raccolto tutto in memoria e poi misurato.
    /// </summary>
    private static async Task<(JsonNode? Body, bool TooLarge)> ReadBodyAsync(HttpContext context)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16 * 1024];

        while (true)
        {
            int read;

            try
            {
                read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false);
            }
            catch (BadHttpRequestException)
            {
                // Kestrel ha fermato il corpo al limite dichiarato: e' lo stesso caso.
                return (null, true);
            }

            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxBodyBytes)
            {
                return (null, true);
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;

        try
        {
            return (JsonNode.Parse(buffer), false);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, false);
        }
    }

    /// <summary>
    /// Controlli comuni: token di servizio su ogni route e limite di frequenza. Restituisce null
    /// quando la richiesta puo' proseguire.
    /// </summary>
    private static IResult? Reject(HttpContext context, string serviceToken, RateLimiter limiter)
    {
        string? authorization = context.Request.Headers.Authorization.FirstOrDefault();

        if (authorization is null
            || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            || !FixedTimeEquals(authorization["Bearer ".Length..], serviceToken))
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "AUTH_UNAVAILABLE", "A valid service token is required.");
        }

        if (!limiter.TryTake())
        {
            context.Response.Headers["Retry-After"] = "1";

            return Problem(context, StatusCodes.Status429TooManyRequests, "BUSY", "Too many requests for this service token.");
        }

        return null;
    }

    private static bool Authorized(HttpContext context, IPaRouterRuntime runtime, string sessionId)
    {
        string? token = context.Request.Headers["X-Session-Token"].FirstOrDefault();

        return token is not null && runtime.Authorize(sessionId, token);
    }

    /// <summary>
    /// Sessione o esecuzione non autorizzata: la risposta e' la stessa di «non esiste», perche'
    /// distinguerle direbbe a chi prova quali identificatori sono reali.
    /// </summary>
    private static IResult NotFound(HttpContext context) =>
        Problem(context, StatusCodes.Status404NotFound, "MEMORY_SCOPE_DENIED", "Not found.");

    private static IResult Problem(HttpContext context, int status, string code, string title) =>
        Results.Json(
            new JsonObject
            {
                ["type"] = "about:blank",
                ["title"] = title,
                ["status"] = status,
                ["code"] = code,
                ["traceId"] = context.TraceIdentifier,
            },
            statusCode: status,
            contentType: "application/problem+json");

    private static bool FixedTimeEquals(string left, string right)
    {
        byte[] first = Encoding.UTF8.GetBytes(left);
        byte[] second = Encoding.UTF8.GetBytes(right);

        return first.Length == second.Length && CryptographicOperations.FixedTimeEquals(first, second);
    }

    /// <summary>Limite di frequenza a finestra scorrevole, per token di servizio.</summary>
    private sealed class RateLimiter(int perMinute)
    {
        private readonly Queue<DateTimeOffset> hits = new();
        private readonly Lock gate = new();

        public bool TryTake()
        {
            lock (gate)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;

                while (hits.Count > 0 && now - hits.Peek() > TimeSpan.FromMinutes(1))
                {
                    hits.Dequeue();
                }

                if (hits.Count >= perMinute)
                {
                    return false;
                }

                hits.Enqueue(now);

                return true;
            }
        }
    }

    /// <summary>Conteggio delle connessioni SSE aperte.</summary>
    private sealed class StreamCounter(int maximum)
    {
        private int open;

        public bool TryEnter()
        {
            if (Interlocked.Increment(ref open) <= maximum)
            {
                return true;
            }

            Interlocked.Decrement(ref open);

            return false;
        }

        public void Exit() => Interlocked.Decrement(ref open);
    }
}
