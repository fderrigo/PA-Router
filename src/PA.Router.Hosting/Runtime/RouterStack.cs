using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Core;
using PA.Router.Detectors.Onnx;
using PA.Router.Execution;
using PA.Router.Hosting.Adapters;
using PA.Router.Hosting.Security;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Content;
using PA.Router.Infrastructure.Crypto;
using PA.Router.Infrastructure.Observability;
using PA.Router.Infrastructure.Process;
using PA.Router.Infrastructure.Storage;
using PA.Router.Policy;
using PA.Router.Policy.Grants;
using PA.Router.Privacy;
using PA.Router.Privacy.Model;
using PA.Router.Registry.Loading;
using PA.Router.Registry.Probing;
using PA.Router.Search.Local;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Composizione della pipeline sopra una directory dati.
///
/// E' la stessa per la CLI, per il worker e per l'host API: una sola verita' su come si mettono
/// insieme store, privacy, policy, ranking e adapter. Il proprietario dello store e' uno solo, e
/// lo dichiara chi apre (<paramref name="ownsStore" />).
///
/// Due parti con due cicli di vita. Store, writer, chiavi e log appartengono alla directory dati e
/// durano quanto il processo. Configurazione, scanner e candidati appartengono a una
/// **vista** (<see cref="StackView"/>): quando i file cambiano, <see cref="EnsureCurrent"/> ne
/// costruisce una nuova per le richieste successive, e quelle gia' in corso finiscono con la vista con
/// cui sono partite. Una richiesta non vede mai due configurazioni.
///
/// Gli adapter arrivano da <see cref="AdapterRegistry"/>. I test sostituiscono l'insieme per provare
/// la pipeline end-to-end senza abilitare nulla di non certificato nel binario consegnato
/// (CLAUDE.md).
/// </summary>
public sealed class RouterStack : IAsyncDisposable
{
    private readonly StoreLock? ownership;
    private readonly StoreWriter writer;
    private readonly KeyRing? keys;
    private readonly RouterLog log;
    private readonly LocalTelemetrySink? telemetry;
    private readonly ConfigurationWatch watch;
    private readonly ExecutionScheduler scheduler;
    private readonly Lock viewGate = new();

    /// <summary>
    /// Scanner di viste sostituite. Una richiesta partita con una vista la usa fino alla fine, quindi
    /// il modello non si chiude alla sostituzione: si chiude con la composizione.
    /// </summary>
    private readonly List<Lazy<ScannerSet>> retired = [];

    private StackView view;

    private RouterStack(
        ConfigurationSnapshot snapshot,
        StorePaths paths,
        StoreLock? ownership,
        StoreWriter writer,
        KeyRing? keys,
        IMetadataDigest digest,
        SqliteExecutionStore store,
        RouterLog log,
        LocalTelemetrySink? telemetry,
        int schemaVersion)
    {
        SchemaVersion = schemaVersion;
        Paths = paths;
        this.ownership = ownership;
        this.writer = writer;
        this.keys = keys;
        this.log = log;
        this.telemetry = telemetry;
        Digest = digest;
        Store = store;

        watch = new ConfigurationWatch(snapshot, SystemClock.Instance);

        // Uno scheduler per composizione: CLI, worker e host API che condividono lo store condividono anche i limiti.
        // Uno per turno non limiterebbe nulla — due turni insieme troverebbero ciascuno tutti gli slot liberi.
        scheduler = new ExecutionScheduler(snapshot.Config.Limits.MaxConcurrency);

        // One retained budget for this store/runtime, including overlapping requests and views.
        MemoryCache = new MemoryIndexCache(snapshot.Config.Memory.CacheMiB * 1024L * 1024L, SystemClock.Instance,
            retainWhenIdle: ownership is not null);

        view = new StackView(this, snapshot, NewScanners(paths, snapshot), 0);
    }

    /// <summary>Configurazione della vista corrente.</summary>
    public ConfigurationSnapshot Snapshot => Current.Snapshot;

    /// <summary>
    /// Vista corrente, senza rileggere i file. Chi esegue una richiesta usa
    /// <see cref="EnsureCurrent"/>, che prima confronta i file.
    /// </summary>
    public StackView Current => Volatile.Read(ref view);

    public StorePaths Paths { get; }

    public IMetadataDigest Digest { get; }

    public SqliteExecutionStore Store { get; }

    internal MemoryIndexCache MemoryCache { get; }

    /// <summary>Versione di schema applicata all'apertura.</summary>
    public int SchemaVersion { get; }

    /// <summary>
    /// Modo di conservazione effettivo. Segue il modo dichiarato dall'operatore e la presenza di
    /// una chiave: avere un protector non e' un permesso a conservare, ma senza chiave la
    /// conservazione durevole non e' possibile e il modo lo dichiara.
    /// </summary>
    public ContentMode ContentMode =>
        Snapshot.Config.ContentMode is ContentMode.Encrypted && keys is not null
            ? ContentMode.Encrypted
            : ContentMode.MemoryOnly;

    /// <summary>Nome del protector in uso, null quando il modo persistente non e' attivo.</summary>
    public string? KeyProtectorName => keys?.ProtectorName;

    /// <summary>Vero se questa istanza possiede la directory dati.</summary>
    public bool OwnsStore => ownership is not null;

    /// <summary>Factory di connessioni per le letture brevi fuori dal writer.</summary>
    public SqliteConnectionFactory ConnectionFactory => new(Paths);

    /// <summary>
    /// Cifrario locale, quando esiste. L'host API lo pretende: senza protector non c'e' un posto
    /// dove conservare l'HMAC di un token di sessione.
    /// </summary>
    public KeyRing? Keys => keys;

    /// <summary>
    /// Apre la directory dati. Con <paramref name="ownsStore" /> vero prende il lock del
    /// proprietario: se un worker lo possiede gia', l'errore e' STORE_IN_USE. I comandi che
    /// leggono aprono senza lock.
    /// </summary>
    /// <param name="logLevel">
    /// Soglia del log locale. Null — il caso normale — la fa decidere alla variabile della allowlist
    /// `PA_ROUTER_LOG_LEVEL`; un valore esplicito serve alle prove, che non possono cambiare
    /// l'ambiente del processo senza disturbare quelle che girano accanto.
    /// </param>
    public static RouterStack Open(
        ConfigurationSnapshot snapshot,
        bool ownsStore = true,
        IKeyProtector? keyProtector = null,
        TimeSpan? writerEnqueueTimeout = null,
        RouterLogLevel? logLevel = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        StorePaths paths = StorePaths.For(snapshot.DataDirectory).EnsureCreated();

        // Un comando che legge non prende il lock: SQLite in modo WAL ammette piu' lettori mentre
        // un solo processo scrive, e pretendere la proprieta' per elencare una sessione
        // renderebbe la CLI inutilizzabile mentre il worker o l'host API sono vivi. Chi scrive
        // continua a doverla avere: due scrittori sullo stesso file non sono previsti.
        StoreLock? ownership = ownsStore ? StoreLock.Acquire(paths, SystemClock.Instance) : null;

        if (ownsStore)
        {
            // Un Job Object muore con il router; un cgroup no. Chi prende la proprieta' dello
            // store e' l'unico processo autorizzato ad avviare programmi su questa directory,
            // quindi e' anche l'unico che puo' raccogliere i contenitori lasciati da
            // un'esecuzione precedente senza rischiare di toccare quelli di un router vivo.
            _ = PlatformProcessGuardFactory.CleanupOrphans();
        }

        try
        {
            SqliteConnectionFactory factory = new(paths);
            MigrationRunner migrations = new(factory, SystemClock.Instance);

            // Un comando che legge convive con un worker vivo: applicare una migrazione cambierebbe lo schema sotto chi
            // lo sta usando, e creare la chiave dati sarebbe comunque una scrittura. Chi legge trova lo store come e',
            // oppure dice che non e' pronto.
            int schemaVersion;

            if (ownsStore)
            {
                schemaVersion = migrations.Migrate();
            }
            else
            {
                // Aprire una connessione creerebbe il file del database: chi legge non deve lasciare nemmeno quello.
                schemaVersion = File.Exists(paths.DatabaseFile) ? migrations.ReadCurrentVersion() : 0;

                if (schemaVersion == 0)
                {
                    throw new PaRouterException(
                        ErrorCode.StoreUnavailable,
                        "The data directory has no store yet: run a command that owns it, such as 'pa init'.")
                        .WithMetadata("reason", "store_not_initialised");
                }

                if (schemaVersion < MigrationScript.LatestVersion)
                {
                    throw new PaRouterException(
                        ErrorCode.StoreUnavailable,
                        "The store schema is older than this build: a command that owns the directory has to migrate it.")
                        .WithMetadata("reason", "migration_required")
                        .WithMetadata("schemaVersion", schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            KeyRing? keys = ownsStore || File.Exists(paths.MasterKeyFile)
                ? TryOpenKeys(paths, keyProtector ?? PlatformKeyProtector.Create())
                : null;
            IMetadataDigest digest = keys ?? (IMetadataDigest)new UnkeyedDigest();

            StoreWriter writer = new(factory, writerEnqueueTimeout ?? TimeSpan.FromSeconds(5));

            // Il livello lo sceglie l'operatore, e viene applicato **qui**: `Open` e' l'unico punto
            // che costruisce il log, quindi CLI, worker e host API lo ereditano senza che nessuno lo
            // debba passare. Aggiungere un parametro ai dodici chiamanti sarebbe bastato a
            // dimenticarne uno, e la diagnosi sarebbe valsa in un front end e non in un altro
            // (INV-22). Fino alla S54 la variabile veniva validata e poi buttata via.
            //
            // Un valore non valido non arriva fin qui: la CLI lo rifiuta con INPUT_INVALID quando
            // risolve la directory dati. Se arrivasse comunque, vale il predefinito: spegnere un
            // router per il livello del log sarebbe sproporzionato.
            RouterLog log = new(
                paths.LogsDirectory,
                SystemClock.Instance,
                logLevel ?? RouterLog.LevelFor(
                    System.Environment.GetEnvironmentVariable(EnvironmentOverrides.LogLevelVariable)));

            // Gli adapter remoti risolvono i segreti per nome dal vault di questa directory dati,
            // e i modelli locali vivono sotto la stessa radice: il registro ne dichiara il nome,
            // il percorso lo decide il layout dello store.
            AdapterRegistry.UseStore(paths);

            // L'esportazione segue la configurazione e il profilo; senza esportatore configurato
            // non si apre nessun ascoltatore, ed e' il caso normale.
            TelemetryDecision decision = TelemetryOptions.FromConfig(
                snapshot.Config, PolicyCompiler.Compile(snapshot.DefaultProfile, snapshot.Config).Zones).Resolve();
            LocalTelemetrySink? telemetry = LocalTelemetrySink.ForDecision(decision, log);

            if (decision is TelemetryDecision.RemoteRefusedDataStaysHome or TelemetryDecision.RemoteRefusedInsecure
                or TelemetryDecision.RemoteUnsupported)
            {
                // Un export chiesto e non fatto viene detto, non ignorato in silenzio.
                log.Warning("telemetry_export_refused", ("reason", decision.ToString()));
            }

            return new RouterStack(
                snapshot,
                paths,
                ownership,
                writer,
                keys,
                digest,
                new SqliteExecutionStore(writer, SystemClock.Instance),
                log,
                telemetry,
                schemaVersion);
        }
        catch
        {
            ownership?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Confronta i file della configurazione con quelli in uso e, se sono cambiati e validi, sostituisce
    /// la vista per le richieste successive. Non lancia: dice lo stato.
    /// </summary>
    public ConfigurationRefresh Refresh()
    {
        ConfigurationRefresh refresh = watch.Refresh();

        if (refresh.Reloaded)
        {
            lock (viewGate)
            {
                StackView previous = view;
                ConfigurationSnapshot next = watch.Current;

                // Il modello PII si ricarica solo se cambia cio' che lo costruisce: aprirlo costa
                // centinaia di millisecondi e la memoria dei pesi.
                Lazy<ScannerSet> scanners = SameScannerInputs(previous.Snapshot, next)
                    ? previous.Scanners
                    : NewScanners(Paths, next);

                if (!ReferenceEquals(scanners, previous.Scanners))
                {
                    retired.Add(previous.Scanners);
                }

                // Only configuration publication changes this shared limit. A turn holding an
                // older snapshot must never enlarge a budget that was just tightened.
                MemoryCache.Resize(next.Config.Memory.CacheMiB * 1024L * 1024L);
                Volatile.Write(ref view, new StackView(this, next, scanners, previous.Generation + 1));
            }
        }
        else if (!refresh.IsUsable)
        {
            log.Warning("configuration_rejected", ("reason", ReasonOf(refresh)));
        }

        return refresh;
    }

    /// <summary>
    /// Vista con cui eseguire una richiesta: quella dei file sul disco.
    ///
    /// Se i file non sono validi, o cambiano un'impostazione che vale solo ripartendo, non si esegue
    /// con la configurazione di prima: l'operatore la stava cambiando, magari per stringere una
    /// regola. L'errore e' POLICY_INVALID, con il motivo.
    /// </summary>
    public StackView EnsureCurrent()
    {
        ConfigurationRefresh refresh = Refresh();

        if (refresh.IsUsable)
        {
            return Current;
        }

        PaRouterException error = refresh.State is ConfigurationState.Invalid
            ? new PaRouterException(
                ErrorCode.PolicyInvalid,
                "The configuration on disk is not valid: nothing runs until it is fixed.")
            : new PaRouterException(
                ErrorCode.PolicyInvalid,
                "The configuration on disk changes a setting that applies only after a restart.");

        error.WithMetadata("reason", ReasonOf(refresh));

        if (refresh.RestartField is string field)
        {
            error.WithMetadata("field", field);
        }

        throw error;
    }

    private static string ReasonOf(ConfigurationRefresh refresh) =>
        refresh.State is ConfigurationState.Invalid ? "configuration_invalid" : "restart_required";

    /// <summary>Scanner privacy della vista corrente.</summary>
    public PrivacyScanner Scanner() => Current.Scanner();

    /// <summary>Ammissione delle esecuzioni di questa composizione.</summary>
    public ExecutionScheduler Scheduler => scheduler;

    /// <summary>
    /// Scheduler della composizione, allineato ai limiti della vista che sta per eseguire: i limiti si rileggono con la
    /// configurazione, l'ammissione resta una sola.
    /// </summary>
    internal ExecutionScheduler SchedulerFor(int maxConcurrency)
    {
        scheduler.Resize(maxConcurrency);

        return scheduler;
    }

    /// <summary>Coordinatore della vista corrente.</summary>
    public ExecutionCoordinator Coordinator() => Current.Coordinator();

    /// <summary>Candidati della vista corrente; vedi <see cref="StackView.Candidates"/>.</summary>
    public IReadOnlyList<PolicyCandidate> Candidates(IReadOnlyDictionary<string, ProbeResult> probes) =>
        Current.Candidates(probes);

    /// <summary>Dati di lancio della vista corrente.</summary>
    public IReadOnlyDictionary<string, TargetDispatchInfo> Dispatch() => Current.Dispatch();

    /// <summary>
    /// Store della memoria sopra lo stesso writer seriale delle esecuzioni: una sola coda di
    /// scrittura per l'intero processo, come chiede lo storage.
    /// </summary>
    public SqliteMemoryStore MemoryStore() =>
        new(
            writer,
            new SqliteConnectionFactory(Paths),
            Content(),
            SystemClock.Instance,
            GuidIdGenerator.Instance,
            MemoryCache.Clear);

    /// <summary>
    /// Cronologia e amministrazione delle sessioni. Usa lo stesso archivio contenuti del resto: in
    /// memory_only non conserva nulla, e lo dichiara invece di ricostruire.
    /// </summary>
    public SqliteSessionService Sessions() =>
        new(
            writer,
            new SqliteConnectionFactory(Paths),
            Content(),
            SystemClock.Instance,
            GuidIdGenerator.Instance);

    /// <summary>
    /// Archivio dei contenuti secondo la configurazione: <c>memory_only</c> non scrive nulla su
    /// disco. La scelta segue il modo dichiarato dall'operatore, non la disponibilita' di una
    /// chiave: avere un protector non e' un permesso a conservare. Il modo non cambia con una
    /// rilettura: vale solo ripartendo.
    /// </summary>
    private IContentStore Content() =>
        Snapshot.Config.ContentMode is ContentMode.Encrypted && keys is not null
            ? new FileContentStore(Paths, keys.Cipher)
            : new EphemeralContentStore();

    /// <summary>Archivio dei contenuti attivo, per chi deve cancellare blob scaduti.</summary>
    public IContentStore ContentStore() => Content();

    /// <summary>Manutenzione dello store: retention, sweep, checkpoint e backup.</summary>
    public MaintenanceService Maintenance() =>
        new(writer, new SqliteConnectionFactory(Paths), Paths, SystemClock.Instance, GuidIdGenerator.Instance);

    /// <summary>Catalogo dei pacchetti sopra lo stesso writer seriale.</summary>
    public SqlitePluginCatalog PluginCatalog() =>
        new(writer, new SqliteConnectionFactory(Paths), SystemClock.Instance);

    /// <summary>Attestazioni degli strumenti dichiarate dalle applicazioni, sopra lo stesso writer seriale.</summary>
    public SqliteToolAttestationStore ToolAttestations() => new(writer, new SqliteConnectionFactory(Paths));

    /// <summary>
    /// Vero se due configurazioni costruiscono lo stesso scanner: stesso servizio PII abilitato,
    /// stesso modello, stesso budget.
    /// </summary>
    private static bool SameScannerInputs(ConfigurationSnapshot previous, ConfigurationSnapshot next)
    {
        ServiceDescriptor? before = PiiServiceOf(previous);
        ServiceDescriptor? after = PiiServiceOf(next);

        return string.Equals(before?.Id, after?.Id, StringComparison.Ordinal)
            && string.Equals(before?.Model, after?.Model, StringComparison.Ordinal)
            && previous.Config.Routing.PiiLatencyBudgetMs == next.Config.Routing.PiiLatencyBudgetMs;
    }

    private static ServiceDescriptor? PiiServiceOf(ConfigurationSnapshot snapshot) =>
        snapshot.Registry.Services.FirstOrDefault(candidate =>
            candidate is { Enabled: true, Capability: ServiceCapability.PiiDetection, Adapter: AdapterKind.OnnxLocal });

    /// <summary>
    /// Scanner di una configurazione. Il modello PII, quando l'operatore lo ha abilitato, si carica
    /// una volta sola e al primo uso: ripeterlo a ogni turno renderebbe il rilevamento troppo caro
    /// per restare acceso.
    /// </summary>
    private static Lazy<ScannerSet> NewScanners(StorePaths paths, ConfigurationSnapshot snapshot) =>
        new(() => BuildScanners(paths, snapshot), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Costruisce gli scanner. Ingresso e uscita condividono lo stesso modello: una superficie
    /// esaminata con un insieme di rilevatori e l'altra con un insieme diverso darebbero due classi
    /// non confrontabili.
    ///
    /// Se il registro abilita un servizio <c>pii-detection</c> con adapter <c>onnx-local</c>, lo
    /// scanner ha anche lo stadio a modello. Se quel servizio e' abilitato ma il modello non e'
    /// installato, lo scanner non viene costruito: proseguire con la sola parte deterministica
    /// significherebbe eseguire una scansione diversa da quella che l'operatore ha configurato.
    /// </summary>
    private static ScannerSet BuildScanners(StorePaths paths, ConfigurationSnapshot snapshot)
    {
        if (PiiServiceOf(snapshot) is not ServiceDescriptor service)
        {
            return new ScannerSet(new PrivacyScanner(), new PrivacyScanner(), null);
        }

        if (LocalModelStore.Resolve(paths.ModelsDirectory, service.Model) is not string directory)
        {
            throw new PaRouterException(
                ErrorCode.TargetUnavailable,
                "The enabled local PII service declares no usable model name.")
                .WithMetadata("targetId", service.Id)
                .WithMetadata("reason", "model_name_invalid");
        }

        OnnxPiiModel model = OnnxPiiModel.Load(directory);

        // Il budget dichiarato nella configurazione limita il lavoro aggiuntivo sulle superfici
        // grandi: oltre quel tempo la scansione si dichiara incompleta invece di continuare a
        // spendere il turno dell'operatore.
        PiiModelStage stage = new(
            model,
            TimeSpan.FromMilliseconds(Math.Max(0, snapshot.Config.Routing.PiiLatencyBudgetMs)),
            TimeSpan.FromMilliseconds(Math.Max(0, snapshot.Config.Routing.PiiAttachmentBudgetMs)));

        return new ScannerSet(
            new PrivacyScanner(PrivacyScanner.DefaultDetectors(), stage),
            new PrivacyScanner(PrivacyScanner.DefaultDetectors(), stage),
            model);
    }

    private static KeyRing? TryOpenKeys(StorePaths paths, IKeyProtector protector)
    {
        try
        {
            return KeyRing.OpenOrCreate(paths, protector);
        }
        catch (PaRouterException ex) when (ex.Code == ErrorCode.CryptoUnavailable)
        {
            // Nessun protector: la CLI resta utilizzabile in memory_only, senza chiavi.
            return null;
        }
    }

    /// <inheritdoc />
    /// <summary>Log locale del processo: codici e metadati noti, mai contenuti.</summary>
    public RouterLog Log => log;

    public async ValueTask DisposeAsync()
    {
        // Refuse new borrowers now; readers still unwinding retain their cache lease.
        MemoryCache.Dispose();
        foreach (Lazy<ScannerSet> scanners in retired.Append(view.Scanners))
        {
            if (scanners.IsValueCreated)
            {
                scanners.Value.Model?.Dispose();
            }
        }

        await writer.DisposeAsync().ConfigureAwait(false);
        telemetry?.Dispose();
        log.Dispose();
        keys?.Dispose();
        ownership?.Dispose();
    }

    /// <summary>
    /// Digest di ripiego quando non esiste un protector: SHA-256 semplice, dichiarato tale.
    ///
    /// Non e' equivalente all'HMAC con chiave locale: senza chiave un digest di testo breve e'
    /// attaccabile a dizionario. Serve solo a far funzionare i comandi diagnostici su una
    /// piattaforma senza DPAPI, dove comunque non esiste conservazione durevole.
    /// </summary>
    private sealed class UnkeyedDigest : IMetadataDigest
    {
        public string Compute(ReadOnlySpan<byte> data) =>
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
    }
}

/// <summary>Scanner di ingresso e di uscita, con il modello che entrambi condividono.</summary>
internal sealed record ScannerSet(PrivacyScanner Input, PrivacyScanner Output, IPiiModel? Model);

/// <summary>
/// Una configurazione e cio' che se ne costruisce: scanner, coordinatore, candidati, dati di lancio.
///
/// Una richiesta prende una vista all'inizio e la usa fino alla fine. Se nel frattempo i file
/// cambiano, la richiesta successiva prende la vista nuova: nessuna richiesta mescola la regola di
/// prima con quella di dopo.
/// </summary>
public sealed class StackView
{
    private readonly RouterStack stack;

    internal StackView(RouterStack stack, ConfigurationSnapshot snapshot, Lazy<ScannerSet> scanners, long generation)
    {
        this.stack = stack;
        Snapshot = snapshot;
        Scanners = scanners;
        Generation = generation;
    }

    /// <summary>Configurazione della vista.</summary>
    public ConfigurationSnapshot Snapshot { get; }

    /// <summary>Numero della vista: cresce a ogni rilettura riuscita.</summary>
    public long Generation { get; }

    internal Lazy<ScannerSet> Scanners { get; }

    /// <summary>Scanner privacy di questa configurazione.</summary>
    public PrivacyScanner Scanner() => Scanners.Value.Input;

    /// <summary>Coordinatore pronto all'uso, con gli adapter disponibili in questa build.</summary>
    public ExecutionCoordinator Coordinator() =>
        new(
            stack.SchedulerFor(Snapshot.Config.Limits.MaxConcurrency),
            new PrivacyPipeline(Scanners.Value.Input, stack.Digest),
            new OutputScreener(Scanners.Value.Output),
            new PolicyEvaluator(SystemClock.Instance),
            new GrantService(SystemClock.Instance),
            stack.Store,
            stack.Digest,
            SystemClock.Instance,
            GuidIdGenerator.Instance,
            AdapterRegistry.Current)
        {
            // Una quota finita mette il servizio in pausa fino alla data del fornitore, o finche' l'operatore non lo riattiva.
            QuotaExhausted = (serviceId, until) =>
                QuotaPauses.Pause(StorePaths.For(Snapshot.DataDirectory), serviceId, SystemClock.Instance.UtcNow, until),
        };

    /// <summary>
    /// Candidati: servizi del registro proiettati sulle osservazioni.
    ///
    /// Un servizio senza osservazione fresca non e' utilizzabile: la policy nega con PROBE_STALE.
    /// Le osservazioni dei servizi locali arrivano da <see cref="AdapterRegistry.Observe"/>, che
    /// interroga il servizio ogni volta invece di fidarsi di una probe vecchia.
    ///
    /// La qualita' di ciascun candidato viene dal suo posto nell'ordine dei modelli dell'operatore (ADR-032, fase 3,
    /// <see cref="ServiceOrder"/>): si risolve qui, una volta, cosi' ranking e racconto confrontano lo stesso numero.
    /// </summary>
    /// <param name="probes">Osservazioni dei servizi.</param>
    public IReadOnlyList<PolicyCandidate> Candidates(IReadOnlyDictionary<string, ProbeResult> probes) =>
        CandidatesFor(Snapshot, probes);

    /// <summary>Candidati di una configurazione: la stessa regola per il turno e per la diagnosi.</summary>
    public static IReadOnlyList<PolicyCandidate> CandidatesFor(
        ConfigurationSnapshot snapshot,
        IReadOnlyDictionary<string, ProbeResult> probes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(probes);

        List<PolicyCandidate> candidates = [];
        IReadOnlyDictionary<string, OrderPosition> order = ServiceOrder.Positions(snapshot);

        foreach (ServiceDescriptor service in snapshot.Registry.Services)
        {
            probes.TryGetValue(service.Id, out ProbeResult? probe);
            EffectiveService effective = RegistryProjection.Project(service, probe, SystemClock.Instance);

            candidates.Add(new PolicyCandidate(
                service.Id,
                service.Capability,
                service.Adapter,
                service.AuthMode,
                service.InferencePlacement,
                service.Enabled,
                effective.Capabilities,
                effective.Controls,
                service.ProcessingRegions,
                probe is not null && probe.IsFreshAt(SystemClock.Instance.UtcNow),
                probe?.AuthStatus ?? AuthStatus.Unknown,
                probe?.QuotaState ?? QuotaState.Unknown)
            {
                ContextWindowTokens = effective.ContextWindowTokens,
                SupportsTools = ToolCallingSupport.Of(service, probe),
                MaxOutputTokens = service.MaxOutputTokens,
                Calibration = new TargetCalibration(
                    ServiceOrder.QualityScore(order.TryGetValue(service.Id, out OrderPosition place) ? place : null),
                    service.Latency?.Value,
                    // Costo e quota non si scrivono a mano (ADR-032): il costo lo calcola il ranking da `pricing`,
                    // relativo al piu' economico; della quota conta lo stato che la sonda osserva.
                    CostScore: null,
                    QuotaScore: null,
                    probe?.QuotaState ?? QuotaState.Unknown,
                    service.MarginalPrice),
            });
        }

        return candidates;
    }

    /// <summary>Dati di lancio per ciascun servizio del registro.</summary>
    public IReadOnlyDictionary<string, TargetDispatchInfo> Dispatch() =>
        Snapshot.Registry.Services.ToDictionary(
            service => service.Id,
            service => new TargetDispatchInfo(
                service.Model,
                service.Endpoint,
                service.Executable,
                service.MaxConcurrent,
                service.SecretRef,
                service.Arguments,
                service.MaxOutputTokens)
            {
                PromptCaching = service.PromptCaching,
                Thinking = service.Thinking,
                KeepAliveMinutes = service.KeepAliveMinutes,
                Pricing = service.Pricing,
                NativeSession = service.NativeSession,
            },
            StringComparer.Ordinal);
}

/// <summary>
/// Adapter disponibili nel processo corrente.
///
/// Nel binario consegnato ci sono **sei** adapter: il modello locale (S09), le tre CLI ufficiali
/// (S14, S32) e i due contratti remoti (S27, S33). Registrare un adapter e abilitare un servizio
/// restano due cose diverse: il registro consegnato nasce con segnaposto e `enabled: false`, e un
/// target senza adapter produce TARGET_UNAVAILABLE, che e' preferibile a una demo che non
/// corrisponde al prodotto.
///
/// Questo commento diceva «il solo adapter del modello locale, gli altri assenti fino a S13 e S14»
/// per oltre sessanta step dopo che quegli adapter erano stati scritti: corretto in S78.
/// </summary>
public static class AdapterRegistry
{
    private static LocalAdapters local = new(SystemClock.Instance);

    /// <summary>
    /// Collega la directory dati aperta: il vault dei segreti e la radice dei modelli locali. Un
    /// processo serve una directory sola, quindi non c'e' ambiguita' su quale vault o quali pesi
    /// siano in uso. Senza questo collegamento un servizio che dichiara un segreto resta
    /// inutilizzabile e lo dichiara, invece di chiamare in forma anonima.
    /// </summary>
    public static void UseStore(StorePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        UseDataDirectory(
            reference => LocalSecretVault.TryRead(paths, PlatformKeyProtector.Create(), reference),
            paths.ModelsDirectory);
    }

    /// <summary>
    /// Variante esplicita, per chi ha gia' un risolutore di segreti proprio. Il prodotto usa
    /// <see cref="UseStore"/>: una sola directory dati, una sola radice dei modelli.
    /// </summary>
    public static void UseDataDirectory(Func<string, string?> secrets, string modelsDirectory)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);

        lock (registryGate)
        {
            // Stessa directory: non si ricostruisce nulla. Ogni `RouterStack.Open` passa di qui, e
            // un comando che apre lo store mentre il REPL e' vivo non deve rifare i client HTTP.
            if (string.Equals(boundDirectory, modelsDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Il vecchio insieme **non** viene dismesso: un altro stack aperto puo' averlo ancora in
            // uso, e chiudergli sotto l'`HttpClient` produceva `ObjectDisposedException` a meta' di
            // un'operazione altrui. Succedeva davvero — il gate lo ha fatto emergere con due
            // aperture vicine — perche' questo registro e' statico e di processo, mentre gli stack
            // sono molti. Lasciarlo al raccoglitore costa una manciata di socket inattivi; dismetterlo
            // costa il guasto di chi sta lavorando.
            local = new LocalAdapters(SystemClock.Instance, secrets, modelsDirectory);
            boundDirectory = modelsDirectory;
        }
    }

    /// <summary>Directory dei modelli a cui gli adapter locali sono collegati adesso.</summary>
    private static string? boundDirectory;

    /// <summary>Protegge la sostituzione: piu' stack possono aprirsi insieme nello stesso processo.</summary>
    private static readonly Lock registryGate = new();

    private static IReadOnlyDictionary<AdapterKind, IExecutionAdapter>? overrideAdapters;

    private static IReadOnlyDictionary<string, ProbeResult>? overrideProbes;

    /// <summary>Adapter attualmente registrati.</summary>
    public static IReadOnlyDictionary<AdapterKind, IExecutionAdapter> Current => overrideAdapters ?? local.Adapters;

    /// <summary>
    /// Osserva adesso i target locali abilitati. Non esiste una cache: la freschezza dura quindici
    /// minuti, e un servizio spento un minuto fa non deve risultare disponibile.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, ProbeResult>> ObserveAsync(
        ConfigurationSnapshot snapshot,
        CancellationToken cancellationToken,
        TimeSpan? reuse = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        IReadOnlyDictionary<string, ProbeResult> probes =
            overrideProbes ?? await local.ObserveAsync(snapshot.Registry.Services, cancellationToken, reuse).ConfigureAwait(false);

        // Un servizio in pausa per quota finita si osserva con la quota esaurita: la policy lo esclude (24 settembre 2026).
        return QuotaPauses.Apply(probes, QuotaPauses.Active(StorePaths.For(snapshot.DataDirectory), SystemClock.Instance.UtcNow));
    }

    /// <summary>Per quanto un turno riusa un esito positivo di una sonda appena osservato (23 settembre 2026).</summary>
    public static readonly TimeSpan TurnProbeReuse = TimeSpan.FromMinutes(1);

    /// <summary>Variante sincrona per i comandi diagnostici, che non hanno un contesto async.</summary>
    public static IReadOnlyDictionary<string, ProbeResult> Observe(ConfigurationSnapshot snapshot) =>
        ObserveAsync(snapshot, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Sostituisce adapter e osservazioni. Usato dai test end-to-end per collegare un LLM finto;
    /// il prodotto non chiama mai questo metodo.
    /// </summary>
    public static IDisposable Use(
        IReadOnlyDictionary<AdapterKind, IExecutionAdapter> adapters,
        IReadOnlyDictionary<string, ProbeResult>? observed = null)
    {
        // Sotto lo stesso lock della sostituzione degli adapter (difetto trovato dall'audit, S77).
        //
        // `UseDataDirectory` prendeva `registryGate` e queste due scritture no, mentre `Current` le
        // legge da qualunque turno: due prove che si sostituiscono gli adapter mentre una terza
        // esegue potevano vedere un insieme a meta'. Il lock c'era gia': mancava su una delle due
        // strade che toccano lo stesso stato di processo.
        IReadOnlyDictionary<AdapterKind, IExecutionAdapter>? previousAdapters;
        IReadOnlyDictionary<string, ProbeResult>? previousProbes;

        lock (registryGate)
        {
            previousAdapters = overrideAdapters;
            previousProbes = overrideProbes;

            overrideAdapters = adapters;
            overrideProbes = observed ?? new Dictionary<string, ProbeResult>(StringComparer.Ordinal);
        }

        return new Restore(() =>
        {
            lock (registryGate)
            {
                overrideAdapters = previousAdapters;
                overrideProbes = previousProbes;
            }
        });
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
