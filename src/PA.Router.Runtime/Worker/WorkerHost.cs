using System.Diagnostics;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Hosting.Runtime;
using PA.Router.Registry.Loading;
using PA.Router.Runtime.Ipc;

namespace PA.Router.Runtime.Worker;

/// <summary>
/// Worker locale: unico proprietario dello store e servitore del canale IPC. Non e' un servizio
/// di sistema e non apre porte di rete; si arresta da solo dopo l'inattivita' configurata.
/// </summary>
public sealed class WorkerHost : IAsyncDisposable
{
    private readonly RouterStack stack;
    private readonly WorkerOptions options;
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IpcServer server;
    private readonly Task idleMonitor;
    private readonly WorkerTurns turns;
    private readonly MemoryDrain drain;
    private readonly TurnEventBroker events = new();

    private WorkerHost(RouterStack stack, WorkerOptions options, string pipeName)
    {
        this.stack = stack;
        this.options = options;
        PipeName = pipeName;

        turns = new WorkerTurns(stack, events);
        drain = new MemoryDrain(stack, options.MemoryDrainInterval);
        server = new IpcServer(pipeName, DispatchAsync, StreamAsync);
        idleMonitor = Task.Run(MonitorIdleAsync);

        // L'analizzatore si scalda subito (ADR-031 D): a freddo il primo turno aspetterebbe la lettura del prompt di sistema.
        _ = Task.Run(() => PA.Router.Hosting.Runtime.TurnWarmUp.RunAsync(stack, shutdown.Token));
    }

    /// <summary>
    /// Serve i metodi che trasmettono piu' frame (S65). Oggi ce n'e' uno: l'ascolto degli eventi di
    /// una sessione.
    ///
    /// L'ascolto tiene occupata la connessione, quindi non entra nel ciclo normale delle richieste:
    /// chi ascolta e chi esegue turni usano due connessioni, ed e' cio' che rende possibile
    /// attaccarsi da un altro terminale a una conversazione che sta lavorando.
    /// </summary>
    private async Task StreamAsync(
        string method,
        JsonElement? request,
        Func<object, Task> write,
        CancellationToken cancellationToken)
    {
        if (method is not IpcProtocol.SessionEventsMethod)
        {
            throw new PaRouterException(ErrorCode.ProtocolError, "Unknown runtime method.")
                .WithMetadata("method", method);
        }

        SessionEventsRequest subscription = Read<SessionEventsRequest>(request);

        using TurnEventBroker.Subscription listener = events.Subscribe(subscription.SessionId);

        await foreach (TurnEvent turnEvent in listener.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Describe(turnEvent) is TurnEventPayload payload)
            {
                await write(payload).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Un evento nella forma del canale. E' lo stesso insieme chiuso delle righe JSONL di S64: gli
    /// eventi sono l'operazione, scritta una volta, e questa e' una delle loro rese.
    /// </summary>
    private static TurnEventPayload? Describe(TurnEvent turnEvent) => turnEvent switch
    {
        TurnPhaseStarted started => new TurnEventPayload("phase_started", Phase: started.Phase),

        TurnPhaseEnded ended => new TurnEventPayload(
            "phase_ended",
            Phase: ended.Phase,
            Outcome: ended.Outcome,
            ElapsedMs: (long)ended.Elapsed.TotalMilliseconds),

        TurnToolCalled tool => new TurnEventPayload(
            "tool_called",
            Tool: tool.Tool,
            Source: tool.Source,
            Round: tool.Round),

        TurnUsageObserved usage => new TurnEventPayload(
            "usage_observed",
            InputTokens: usage.Usage.InputTokens,
            OutputTokens: usage.Usage.OutputTokens,
            BilledCost: usage.Usage.BilledCost,
            LoadMs: usage.Usage.LoadMilliseconds,
            PricedCost: usage.Usage.PricedCost,
            Currency: usage.Usage.Currency),

        TurnArtifactChanged artifact => new TurnEventPayload("artifact_declared_changed", Path: artifact.RelativePath),

        // L'unico evento con testo, e solo perche' quel testo e' gia' passato dallo screening.
        TurnBlockReleased released => new TurnEventPayload("block_released", Text: released.Text),

        _ => null,
    };

    /// <summary>Nome della pipe servita.</summary>
    public string PipeName { get; }

    /// <summary>Versione di schema applicata allo store.</summary>
    public int SchemaVersion => stack.SchemaVersion;

    /// <summary>Modo di conservazione effettivo.</summary>
    public ContentMode ContentMode => stack.ContentMode;

    /// <summary>Composizione posseduta dal worker: e' la stessa che costruisce la CLI.</summary>
    public RouterStack Stack => stack;

    /// <summary>Client attualmente collegati.</summary>
    public int ConnectedClients => server.ConnectedClients;

    /// <summary>
    /// Avvia il worker. Se un altro worker possiede gia' la directory dati l'errore e'
    /// STORE_IN_USE e nessun canale viene aperto.
    /// </summary>
    public static WorkerHost Start(WorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Il worker esegue turni, quindi ha bisogno della configurazione, non del solo store:
        // una directory dati senza configurazione valida non e' servibile.
        ConfigurationLoadResult configuration = new ConfigurationLoader(options.Clock).Load(options.DataDirectory);

        if (!configuration.IsValid)
        {
            throw new PaRouterException(ErrorCode.PolicyInvalid, "The data directory has no usable configuration.")
                .WithMetadata("reason", "configuration_invalid");
        }

        RouterStack stack = RouterStack.Open(
            configuration.Require(),
            ownsStore: true,
            options.KeyProtector,
            options.WriterEnqueueTimeout);

        WorkerHost host;

        try
        {
            host = new WorkerHost(stack, options, IpcPipeName.ForDataDirectory(options.DataDirectory));
        }
        catch
        {
            stack.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        host.server.Start();
        return host;
    }

    /// <summary>Lavori di indicizzazione completati da quando il worker e' vivo.</summary>
    public long MemoryJobsDrained => drain.Drained;

    private async Task<object> DispatchAsync(
        string method,
        JsonElement? request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (method is IpcProtocol.ExecutionRunMethod)
        {
            RunRequestPayload run = Read<RunRequestPayload>(request);
            RunResultPayload result = await turns.RunAsync(run, cancellationToken).ConfigureAwait(false);

            // Il lavoro di indicizzazione lasciato dal turno viene svuotato subito: il turno
            // successivo deve poter consultare cio' che questo ha osservato, senza che l'operatore
            // esegua nulla a mano (difetto D1).
            await drain.DrainNowAsync(64, cancellationToken).ConfigureAwait(false);

            return result with { MemoryPending = turns.Status(drain.Drained).Pending };
        }

        // Le letture dell'operatore sono le stesse che chiama chi possiede lo store: il worker non ne
        // tiene una versione propria (INV-22).
        if (method is IpcProtocol.MemorySearchMethod)
        {
            return await ReadRequests
                .SearchMemoryAsync(stack, Read<MemoryQueryPayload>(request), cancellationToken)
                .ConfigureAwait(false);
        }

        if (method is IpcProtocol.MemoryOverviewMethod)
        {
            return await ReadRequests
                .MemoryOverviewAsync(stack, Read<MemoryQueryPayload>(request), cancellationToken)
                .ConfigureAwait(false);
        }

        // Le sessioni passano dal canale come le altre letture, e a differenza di quelle portano
        // anche due scritture: con un worker vivo il client non possiede lo store, quindi dare un
        // nome o archiviare senza passare di qui sarebbe un comando che funziona solo a worker
        // spento (S62).
        if (method is IpcProtocol.SessionNameMethod)
        {
            return await SessionRequests
                .NameAsync(stack, Read<SessionNameRequest>(request), cancellationToken)
                .ConfigureAwait(false);
        }

        if (method is IpcProtocol.SessionArchiveMethod)
        {
            return await SessionRequests
                .ArchiveAsync(stack, Read<SessionArchiveRequest>(request), cancellationToken)
                .ConfigureAwait(false);
        }

        if (method is IpcProtocol.SessionTranscriptMethod)
        {
            return await SessionRequests
                .TranscriptAsync(stack, Read<SessionTranscriptRequest>(request), cancellationToken)
                .ConfigureAwait(false);
        }

        if (method is IpcProtocol.SessionForkMethod)
        {
            return await SessionRequests
                .ForkAsync(stack, Read<SessionForkRequest>(request), cancellationToken)
                .ConfigureAwait(false);
        }

        object payload = method switch
        {
            IpcProtocol.SessionListMethod => SessionRequests.List(stack, Read<SessionListRequest>(request)),
            IpcProtocol.PingMethod => new PingPayload(IpcProtocol.RuntimeVersion, Environment.ProcessId),
            IpcProtocol.StoreStatusMethod => new StoreStatusPayload(
                stack.SchemaVersion,
                stack.ContentMode.ToWire(),
                stack.KeyProtectorName,
                server.ConnectedClients),
            IpcProtocol.MemoryStatusMethod => turns.Status(drain.Drained),
            IpcProtocol.PluginListMethod => ReadRequests.ListPlugins(stack),
            IpcProtocol.SkillListMethod => ReadRequests.ListSkills(stack),
            _ => throw new PaRouterException(ErrorCode.ProtocolError, "Unknown runtime method.")
                .WithMetadata("method", method),
        };

        return payload;
    }

    /// <summary>
    /// Contenuto della richiesta. Un metodo che ne ha bisogno e non lo riceve e' un errore di
    /// protocollo, non un caso da riempire con un default.
    /// </summary>
    private static T Read<T>(JsonElement? payload) =>
        payload is JsonElement element
            ? element.Deserialize<T>(IpcProtocol.SerializerOptions)
                ?? throw new PaRouterException(ErrorCode.ProtocolError, "The request payload is empty.")
            : throw new PaRouterException(ErrorCode.ProtocolError, "The method requires a request payload.");

    /// <summary>Attende l'arresto del worker.</summary>
    public Task WaitForShutdownAsync(CancellationToken cancellationToken) =>
        stopped.Task.WaitAsync(cancellationToken);

    /// <summary>Richiede l'arresto senza attendere il drain dei client.</summary>
    public async Task StopAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);
        stopped.TrySetResult();
    }

    private async Task MonitorIdleAsync()
    {
        Stopwatch sinceIdle = Stopwatch.StartNew();

        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                await Task.Delay(options.IdlePollInterval, shutdown.Token).ConfigureAwait(false);

                // In memory_only il contenuto vive solo finche' vive il worker: l'arresto per
                // inattivita' avviene comunque, ma solo senza client collegati.
                if (server.ConnectedClients > 0)
                {
                    sinceIdle.Restart();
                    continue;
                }

                if (DateTimeOffset.UtcNow - server.LastActivityUtc >= options.IdleTimeout
                    && sinceIdle.Elapsed >= options.IdleTimeout)
                {
                    stopped.TrySetResult();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Arresto richiesto dal chiamante.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);
        stopped.TrySetResult();

        try
        {
            await idleMonitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Arresto richiesto.
        }

        await server.DisposeAsync().ConfigureAwait(false);
        await drain.DisposeAsync().ConfigureAwait(false);
        await stack.DisposeAsync().ConfigureAwait(false);
        shutdown.Dispose();
    }
}
