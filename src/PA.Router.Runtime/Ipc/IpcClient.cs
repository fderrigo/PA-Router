using System.IO.Pipes;
using System.Text.Json;
using PA.Router.Abstractions;

namespace PA.Router.Runtime.Ipc;

/// <summary>
/// Client IPC. CLI e host API sono client del worker: non aprono il database e non conoscono il
/// layout dello store, parlano solo con il proprietario.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream stream;
    private int requestCounter;

    private IpcClient(NamedPipeClientStream stream, string workerVersion)
    {
        this.stream = stream;
        WorkerVersion = workerVersion;
    }

    /// <summary>Versione dichiarata dal worker durante l'handshake.</summary>
    public string WorkerVersion { get; }

    /// <summary>Si collega al worker che possiede la directory dati indicata.</summary>
    /// <param name="dataDirectory">Directory dati, usata per derivare il nome della pipe.</param>
    /// <param name="timeout">Attesa massima della connessione.</param>
    /// <param name="clientVersion">
    /// Versione dichiarata dal client. Il default e' quella dell'assembly; i test la sovrascrivono
    /// per verificare il rifiuto per versione diversa.
    /// </param>
    public static async Task<IpcClient> ConnectAsync(
        string dataDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? clientVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        NamedPipeClientStream stream = new(
            ".",
            IpcPipeName.ForDataDirectory(dataDirectory),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);

            try
            {
                await stream.ConnectAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PaRouterException(
                    ErrorCode.StoreUnavailable,
                    "No local worker answered on the runtime channel within the timeout.",
                    ex);
            }

            string workerVersion = await HandshakeAsync(
                stream,
                clientVersion ?? IpcProtocol.RuntimeVersion,
                cancellationToken).ConfigureAwait(false);

            return new IpcClient(stream, workerVersion);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string> HandshakeAsync(
        Stream stream,
        string clientVersion,
        CancellationToken cancellationToken)
    {
        ServerHello hello = await IpcFraming.ReadAsync<ServerHello>(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new PaRouterException(ErrorCode.ProtocolError, "The worker closed the channel before the handshake.");

        if (hello.ProtocolVersion != IpcProtocol.Version)
        {
            throw new PaRouterException(
                ErrorCode.RuntimeVersionMismatch,
                "The worker speaks a different runtime protocol version.");
        }

        await IpcFraming.WriteAsync(
            stream,
            new ClientHello(IpcProtocol.Version, clientVersion, hello.Nonce),
            cancellationToken).ConfigureAwait(false);

        HandshakeResult result = await IpcFraming.ReadAsync<HandshakeResult>(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new PaRouterException(ErrorCode.ProtocolError, "The worker closed the channel during the handshake.");

        if (!result.Accepted)
        {
            ErrorCodes.TryParseWire(result.ErrorCode, out ErrorCode code);
            throw new PaRouterException(code, result.Message ?? "The worker refused the connection.");
        }

        return hello.WorkerVersion;
    }

    /// <summary>Verifica che il canale risponda.</summary>
    public Task<PingPayload> PingAsync(CancellationToken cancellationToken) =>
        InvokeAsync<PingPayload>(IpcProtocol.PingMethod, cancellationToken);

    /// <summary>Stato dello store posseduto dal worker.</summary>
    public Task<StoreStatusPayload> GetStoreStatusAsync(CancellationToken cancellationToken) =>
        InvokeAsync<StoreStatusPayload>(IpcProtocol.StoreStatusMethod, cancellationToken);

    /// <summary>Esegue un turno nel worker e restituisce cio' che il client deve stampare.</summary>
    public Task<RunResultPayload> RunAsync(RunRequestPayload request, CancellationToken cancellationToken) =>
        InvokeAsync<RunRequestPayload, RunResultPayload>(IpcProtocol.ExecutionRunMethod, request, cancellationToken);

    /// <summary>Stato della coda di indicizzazione del worker.</summary>
    public Task<MemoryStatusPayload> MemoryStatusAsync(CancellationToken cancellationToken) =>
        InvokeAsync<MemoryStatusPayload>(IpcProtocol.MemoryStatusMethod, cancellationToken);

    /// <summary>Ricerca nei ricordi dell'ambito di una sessione, eseguita dal worker.</summary>
    public Task<MemorySearchPayload> SearchMemoryAsync(MemoryQueryPayload request, CancellationToken cancellationToken) =>
        InvokeAsync<MemoryQueryPayload, MemorySearchPayload>(IpcProtocol.MemorySearchMethod, request, cancellationToken);

    /// <summary>Stato della memoria visibile da una sessione, letto dal worker.</summary>
    public Task<MemoryOverviewPayload> MemoryOverviewAsync(MemoryQueryPayload request, CancellationToken cancellationToken) =>
        InvokeAsync<MemoryQueryPayload, MemoryOverviewPayload>(IpcProtocol.MemoryOverviewMethod, request, cancellationToken);

    /// <summary>Pacchetti installati, letti dal worker.</summary>
    public Task<PluginListPayload> ListPluginsAsync(CancellationToken cancellationToken) =>
        InvokeAsync<PluginListPayload>(IpcProtocol.PluginListMethod, cancellationToken);

    /// <summary>Skill offerte dai pacchetti installati, lette dal worker.</summary>
    public Task<SkillListPayload> ListSkillsAsync(CancellationToken cancellationToken) =>
        InvokeAsync<SkillListPayload>(IpcProtocol.SkillListMethod, cancellationToken);

    /// <summary>Sessioni locali, elencate dal worker.</summary>
    public Task<SessionListPayload> ListSessionsAsync(SessionListRequest request, CancellationToken cancellationToken) =>
        InvokeAsync<SessionListRequest, SessionListPayload>(IpcProtocol.SessionListMethod, request, cancellationToken);

    /// <summary>Nome di una sessione, scritto dal worker che possiede lo store.</summary>
    public Task<SessionAckPayload> NameSessionAsync(SessionNameRequest request, CancellationToken cancellationToken) =>
        InvokeAsync<SessionNameRequest, SessionAckPayload>(IpcProtocol.SessionNameMethod, request, cancellationToken);

    /// <summary>Archiviazione di una sessione, scritta dal worker.</summary>
    public Task<SessionAckPayload> ArchiveSessionAsync(SessionArchiveRequest request, CancellationToken cancellationToken) =>
        InvokeAsync<SessionArchiveRequest, SessionAckPayload>(IpcProtocol.SessionArchiveMethod, request, cancellationToken);

    /// <summary>Trascritto di una sessione, letto dal worker.</summary>
    public Task<SessionTranscriptPayload> TranscriptAsync(SessionTranscriptRequest request, CancellationToken cancellationToken) =>
        InvokeAsync<SessionTranscriptRequest, SessionTranscriptPayload>(IpcProtocol.SessionTranscriptMethod, request, cancellationToken);

    /// <summary>Biforca di una sessione, eseguita dal worker.</summary>
    public Task<SessionAckPayload> ForkSessionAsync(SessionForkRequest request, CancellationToken cancellationToken) =>
        InvokeAsync<SessionForkRequest, SessionAckPayload>(IpcProtocol.SessionForkMethod, request, cancellationToken);

    /// <summary>
    /// Invoca un metodo qualunque e restituisce il payload come JSON. Serve alle diagnosi e ai
    /// test di protocollo, che devono poter chiedere anche un metodo non previsto.
    /// </summary>
    public async Task<JsonElement> InvokeRawAsync(string method, CancellationToken cancellationToken)
    {
        IpcResponse response = await SendAsync(method, null, cancellationToken).ConfigureAwait(false);
        return ReadPayload(response);
    }

    /// <summary>Invoca un metodo con contenuto e attende la risposta tipizzata.</summary>
    public async Task<TResult> InvokeAsync<TRequest, TResult>(
        string method,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        IpcResponse response = await SendAsync(
            method,
            JsonSerializer.SerializeToElement(payload, IpcProtocol.SerializerOptions),
            cancellationToken).ConfigureAwait(false);

        return ReadPayload(response).Deserialize<TResult>(IpcProtocol.SerializerOptions)
            ?? throw new PaRouterException(ErrorCode.ProtocolError, "The worker returned an empty payload.");
    }

    private async Task<T> InvokeAsync<T>(string method, CancellationToken cancellationToken)
        where T : class
    {
        IpcResponse response = await SendAsync(method, null, cancellationToken).ConfigureAwait(false);

        return ReadPayload(response).Deserialize<T>(IpcProtocol.SerializerOptions)
            ?? throw new PaRouterException(ErrorCode.ProtocolError, "The worker payload could not be read.");
    }

    /// <summary>
    /// Ascolta gli eventi dei turni di una sessione finche' il worker li manda (S65).
    ///
    /// E' l'unico metodo che riceve **piu' frame** per una richiesta sola: ogni frame porta un
    /// evento, l'ultimo lo dichiara e chiude. Fermarsi alla chiusura della connessione non basta —
    /// quella e' un guasto, non una fine — quindi il frame terminale esiste apposta.
    ///
    /// La connessione resta occupata per tutta la durata dell'ascolto: chi vuole seguire una
    /// conversazione e insieme eseguire turni ne apre due, come fa il terminale che si attacca.
    /// </summary>
    public async IAsyncEnumerable<TurnEventPayload> SubscribeAsync(
        SessionEventsRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string id = Interlocked.Increment(ref requestCounter)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        await IpcFraming.WriteAsync(
            stream,
            new IpcRequest(id, IpcProtocol.SessionEventsMethod, JsonSerializer.SerializeToElement(request, IpcProtocol.SerializerOptions)),
            cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            IpcResponse? response = await IpcFraming.ReadAsync<IpcResponse>(stream, cancellationToken).ConfigureAwait(false);

            if (response is null)
            {
                // Il worker ha chiuso senza frame terminale: e' un guasto, e va detto invece di
                // farlo sembrare la fine naturale dell'ascolto.
                throw new PaRouterException(ErrorCode.ProtocolError, "The worker closed the event stream without ending it.");
            }

            if (!string.Equals(response.Id, id, StringComparison.Ordinal))
            {
                throw new PaRouterException(ErrorCode.ProtocolError, "The worker answered a different request.");
            }

            if (!response.Ok)
            {
                ErrorCodes.TryParseWire(response.ErrorCode, out ErrorCode code);

                PaRouterException failure = new(code, response.Message ?? "The worker reported an error.");

                throw response.Reason is string reason ? failure.WithMetadata("reason", reason) : failure;
            }

            if (response.Final)
            {
                yield break;
            }

            if (response.Payload is JsonElement element
                && element.Deserialize<TurnEventPayload>(IpcProtocol.SerializerOptions) is TurnEventPayload turnEvent)
            {
                yield return turnEvent;
            }
        }
    }

    private async Task<IpcResponse> SendAsync(
        string method,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        string id = Interlocked.Increment(ref requestCounter)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        await IpcFraming.WriteAsync(stream, new IpcRequest(id, method, payload), cancellationToken).ConfigureAwait(false);

        IpcResponse response = await IpcFraming.ReadAsync<IpcResponse>(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new PaRouterException(ErrorCode.ProtocolError, "The worker closed the channel before answering.");

        if (!string.Equals(response.Id, id, StringComparison.Ordinal))
        {
            throw new PaRouterException(ErrorCode.ProtocolError, "The worker answered a different request.");
        }

        if (!response.Ok)
        {
            ErrorCodes.TryParseWire(response.ErrorCode, out ErrorCode code);

            PaRouterException failure = new(code, response.Message ?? "The worker reported an error.");

            // La ragione torna dove il chiamante la cerca: nei metadata dell'eccezione. Senza, un
            // front end che distingue due rifiuti dello stesso codice si comporterebbe diversamente
            // a seconda di chi possiede lo store, che e' esattamente cio' che INV-22 vieta.
            throw response.Reason is string reason
                ? failure.WithMetadata("reason", reason)
                : failure;
        }

        return response;
    }

    private static JsonElement ReadPayload(IpcResponse response) =>
        response.Payload is JsonElement payload
            ? payload
            : throw new PaRouterException(ErrorCode.ProtocolError, "The worker answered without a payload.");

    /// <inheritdoc />
    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
