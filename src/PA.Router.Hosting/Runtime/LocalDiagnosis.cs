using Microsoft.Data.Sqlite;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Crypto;
using PA.Router.Infrastructure.Process;
using PA.Router.Infrastructure.Storage;
using PA.Router.Policy;
using PA.Router.Registry.Loading;
using PA.Router.Registry.Probing;

namespace PA.Router.Hosting.Runtime;

/// <summary>Stato del rilevatore PII con modello, quando un servizio dedicato e' abilitato.</summary>
/// <param name="ServiceId">Servizio che lo offre.</param>
/// <param name="Model">Nome del modello dichiarato dal registro, mai un percorso.</param>
/// <param name="Installed">Osservato adesso: i file ci sono e la probe risponde.</param>
/// <param name="FailureCode">Codice del guasto osservato, quando c'e'.</param>
/// <param name="WeightsDigest">Digest dei pesi, quando la probe lo ha letto.</param>
public sealed record PiiModelDiagnosis(
    string ServiceId,
    string? Model,
    bool Installed,
    string? FailureCode,
    string? WeightsDigest);

/// <summary>
/// Un servizio osservato adesso, con cio' che se ne puo' dire **senza** dichiararlo.
/// </summary>
/// <param name="Id">Identificatore del servizio.</param>
/// <param name="Probe">Osservazione appena eseguita.</param>
/// <param name="Service">Descrittore del registro, quando la configurazione e' valida.</param>
/// <param name="EndpointSatisfiesPlacement">
/// L'indirizzo rispetta la regola della collocazione dichiarata. **Non** dice che nessun byte sia
/// uscito: quella resta una verifica d'ambiente (ADR-016).
/// </param>
/// <param name="LocalInferenceAttested">Vero solo con un'attestazione dell'operatore valida adesso.</param>
/// <param name="LocalInferenceGrade">Grado del controllo: osservato, attestato, scaduto, ignoto.</param>
/// <param name="PrivacyReady">Verdetto per i soli servizi agentici, con le condizioni cadute.</param>
/// <param name="ControlGrades">Grado di conoscenza di ciascun controllo richiesto.</param>
public sealed record ServiceDiagnosis(
    string Id,
    ProbeResult Probe,
    ServiceDescriptor? Service,
    bool? EndpointSatisfiesPlacement,
    bool LocalInferenceAttested,
    string? LocalInferenceGrade,
    PrivacyReadyVerdict? PrivacyReady,
    IReadOnlyDictionary<string, string>? ControlGrades)
{
    /// <summary>
    /// Zona di fiducia del servizio (ADR-029), dai controlli effettivi adesso; null per i servizi ausiliari, che non
    /// rispondono alle richieste e non ricevono dati da una decisione di zona.
    /// </summary>
    public TrustZone? Zone { get; init; }
}

/// <summary>Zone e gradazione di un profilo, come le vede la valutazione (ADR-029).</summary>
/// <param name="ProfileId">Profilo.</param>
/// <param name="Grading">Regola della gradazione del dato personale.</param>
/// <param name="MaxZone">Zona piu' lontana per classe, dopo il tetto su segreti e testi non classificati.</param>
/// <param name="Perimeter">Zone interne: il perimetro fisso (ADR-032, fase 4).</param>
/// <param name="ConfirmOutside">Classi che escono dal perimetro solo con conferma.</param>
/// <param name="AttachmentsOutsidePerimeter">Vero se un turno con allegati puo' uscire dal perimetro.</param>
public sealed record ProfileZonesDiagnosis(
    string ProfileId,
    PersonalDataGrading Grading,
    IReadOnlyDictionary<DataClass, TrustZone> MaxZone,
    IReadOnlyList<TrustZone> Perimeter,
    IReadOnlyList<DataClass> ConfirmOutside,
    bool AttachmentsOutsidePerimeter)
{
    /// <summary>Nome dato dall'operatore al profilo (<c>name</c>); null: la resa mostra l'identificatore.</summary>
    public string? ProfileName { get; init; }
}

/// <summary>
/// Cio' che <c>pa doctor</c> ha osservato, come **dati**.
///
/// Nessuna stringa di resa e nessun JSON: chi mostra decide come scriverlo. Prima lo stato del
/// rilevatore PII veniva costruito come documento JSON e poi **riletto** dalla resa testuale, che e'
/// il segno che calcolo e disegno erano mescolati.
/// </summary>
public sealed record DiagnosisReport(
    string DataDirectory,
    bool ConfigurationValid,
    IReadOnlyList<ValidationIssue> Issues,
    int SchemaVersion,
    string? KeyProtector,
    string ProcessContainment,
    bool ProcessContainmentSupported,
    string? ContentMode,
    int EnabledServices,
    IReadOnlyList<ServiceDiagnosis> Services,
    bool MemoryConfigured,
    bool MemoryLexicalOnly,
    bool MemoryNeverRecords,
    bool MemorySessionScopedOnly,
    int MemoryJobsPending,
    int MemoryJobsFailed,
    bool EmbeddingMismatch,
    PiiModelDiagnosis? PiiModel,
    ToolDiagnosis? Tools)
{
    /// <summary>
    /// Conferme dell'operatore date da almeno un anno, da ricontrollare (ADR-032, fase 4): un promemoria, non una
    /// scadenza.
    /// </summary>
    public IReadOnlyList<ConfirmationReminder> ConfirmationReminders { get; init; } = [];

    /// <summary>Stato dell'analizzatore della richiesta (ADR-026): spento, in ombra, o in ombra senza servizio usabile.</summary>
    public string? Analyzer { get; init; }


    /// <summary>Zone e gradazione di ogni profilo (ADR-029).</summary>
    public IReadOnlyList<ProfileZonesDiagnosis> Profiles { get; init; } = [];

    /// <summary>
    /// Modelli distinti che il router usa su Ollama in questa macchina: chat, embedding, analizzatore (ADR-031 D). Senza
    /// scheda grafica Ollama ne tiene in memoria al massimo tre (<c>OLLAMA_MAX_LOADED_MODELS</c>); con di piu' li alterna,
    /// e l'analizzatore ricaricato a freddo costa una quindicina di secondi a turno.
    /// </summary>
    public int LocalOllamaModels { get; init; }

    /// <summary>
    /// Quanti modelli Ollama puo' tenere caricati questo computer, dalla sua memoria e dai modelli accesi
    /// (<see cref="OllamaMemoryPlan"/>); null se il router non usa modelli di Ollama qui.
    /// </summary>
    public OllamaMemoryProposal? OllamaProposal { get; init; }

    /// <summary>Il valore attuale di <c>OLLAMA_MAX_LOADED_MODELS</c> per l'utente; null se non impostato.</summary>
    public int? OllamaMaxLoaded { get; init; }

    /// <summary>Spesa massima per richiesta (<c>limits.maxCostPerRequest</c>, ADR-032 fase 5); null senza tetto.</summary>
    public double? MaxCostPerRequest { get; init; }

    /// <summary>Valuta dei prezzi del registro, null se nessun servizio ha un prezzo.</summary>
    public string? Currency { get; init; }
}

/// <summary>
/// La diagnosi locale come operazione (S63).
///
/// Fino a qui viveva dentro <c>pa doctor</c>, cioe' dentro un front end: il REPL non poteva
/// mostrarla senza ricostruirne una copia, e una copia decide diversamente senza fallire (INV-22).
/// Qui c'e' l'ordine delle osservazioni; il testo, il JSON e la tabella del terminale restano a chi
/// mostra.
///
/// Nessuna di queste letture esegue un turno, apre un grant o contatta un fornitore per generare.
/// Le probe osservano e basta, e un modello assente viene **dichiarato**, mai scaricato: un pull
/// durante una diagnosi sarebbe un egress che nessuno ha autorizzato.
/// </summary>
public static class LocalDiagnosis
{
    /// <summary>Codice con cui un lavoro di indicizzazione dichiara il disallineamento del modello.</summary>
    private const string EmbeddingMismatchCode = "EMBEDDING_VERSION_MISMATCH";

    /// <summary>Osserva la directory dati indicata e riporta che cosa si e' visto.</summary>
    public static async Task<DiagnosisReport> RunAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        IKeyProtector protector = PlatformKeyProtector.Create();
        StorePaths paths = StorePaths.For(dataDirectory);

        ConfigurationLoadResult configuration = new ConfigurationLoader(SystemClock.Instance).Load(dataDirectory);

        int schemaVersion = File.Exists(paths.DatabaseFile)
            ? new MigrationRunner(new SqliteConnectionFactory(paths), SystemClock.Instance).ReadCurrentVersion()
            : 0;

        (int pending, int failed, bool mismatched) = MemoryJobs(paths, configuration);

        // Gli adapter locali leggono segreti e modelli dalla directory che stiamo diagnosticando:
        // senza questo collegamento il rilevatore PII non verrebbe osservato e la diagnosi tacerebbe
        // proprio sulla cosa che le si chiede.
        AdapterRegistry.UseStore(paths);

        IReadOnlyDictionary<string, ProbeResult> observed = configuration.IsValid
            ? AdapterRegistry.Observe(configuration.Require())
            : new Dictionary<string, ProbeResult>(StringComparer.Ordinal);

        ToolDiagnosis? tools = configuration.IsValid
            ? await ToolDiagnostics.DescribeAsync(
                    configuration.Require(),
                    observed,
                    schemaVersion >= 6
                        ? SqliteToolAttestationStore.ListAt(new SqliteConnectionFactory(paths), configuration.Require().Config.Api.ProjectId)
                        : [],
                    cancellationToken)
                .ConfigureAwait(false)
            : null;

        ConfigurationSnapshot? snapshot = configuration.IsValid ? configuration.Require() : null;

        // La stessa proposta del setup (Sistema > Memoria di Ollama): doctor la confronta con cio' che Ollama usa oggi.
        IReadOnlyList<OllamaModelNeed> ollamaModels = snapshot is null
            ? []
            : await OllamaMemoryPlan.ObserveAsync(snapshot.Registry.Services.Where(service => service.Enabled), SystemClock.Instance, cancellationToken)
                .ConfigureAwait(false);

        return new DiagnosisReport(
            paths.DataDirectory,
            configuration.IsValid,
            configuration.Issues,
            schemaVersion,
            protector.IsAvailable ? protector.Name : null,
            PlatformProcessGuardFactory.Instance.Mechanism,
            PlatformProcessGuardFactory.Instance.IsSupported,
            snapshot?.Config.ContentMode.ToWire(),
            snapshot is null ? 0 : snapshot.Registry.Services.Count(service => service.Enabled),
            Describe(observed, snapshot),
            snapshot is not null && !snapshot.Config.Memory.IsUnconfigured,
            snapshot is not null && MemoryQueries.LexicalOnly(snapshot),
            snapshot is not null && MemoryQueries.NeverRecords(snapshot),
            snapshot is not null && MemoryQueries.SessionScopedOnly(snapshot),
            pending,
            failed,
            mismatched,
            PiiModel(snapshot, observed),
            tools)
        {
            ConfirmationReminders = snapshot is null ? [] : ServiceAttestations.Reminders(snapshot.Registry.Services, SystemClock.Instance.UtcNow),
            Analyzer = snapshot is null ? null : RequestAnalyzerModel.Describe(snapshot),
            Profiles = snapshot is null ? [] : Zones(snapshot),
            OllamaProposal = ollamaModels.Count == 0 ? null : OllamaMemoryPlan.Propose(OllamaMemoryPlan.TotalMemoryBytes(), ollamaModels),
            OllamaMaxLoaded = OllamaMemoryPlan.CurrentMaxLoaded(),
            MaxCostPerRequest = snapshot?.Config.Limits.MaxCostPerRequest,
            Currency = snapshot?.Registry.Currency,
            LocalOllamaModels = snapshot is null ? 0 : snapshot.Registry.Services
                .Where(service => service.Enabled && service.Adapter is AdapterKind.Ollama && service.InferencePlacement is Placement.Loopback)
                .Select(service => service.Model)
                .Where(model => model is not null)
                .Distinct(StringComparer.Ordinal)
                .Count(),
        };
    }

    /// <summary>Zone e gradazione dei profili, compilati come li compila un turno.</summary>
    private static List<ProfileZonesDiagnosis> Zones(ConfigurationSnapshot snapshot) =>
    [
        .. snapshot.Profiles.Values
            .OrderBy(profile => profile.ProfileId, StringComparer.Ordinal)
            .Select(profile =>
            {
                CompiledPolicy policy = PolicyCompiler.Compile(profile, snapshot.Config);
                return new ProfileZonesDiagnosis(
                    profile.ProfileId,
                    policy.PersonalDataGrading,
                    Enum.GetValues<DataClass>().ToDictionary(dataClass => dataClass, policy.Zones.For),
                    [.. policy.Zones.Perimeter.Order()],
                    [.. policy.Zones.ConfirmOutside.Order()],
                    policy.Zones.AttachmentsOutsidePerimeter)
                {
                    ProfileName = profile.Name,
                };
            }),
    ];

    /// <summary>
    /// Servizi osservati, in ordine stabile, con i gradi di conoscenza dei controlli.
    ///
    /// I tre gradi non si confondono mai in una spunta: <c>osservato</c> lo ha verificato il router
    /// adesso, <c>attestato</c> lo ha confermato l'operatore con nome e data, <c>ignoto</c> non lo
    /// ha affermato nessuno (ADR-017).
    /// </summary>
    private static List<ServiceDiagnosis> Describe(
        IReadOnlyDictionary<string, ProbeResult> observed,
        ConfigurationSnapshot? snapshot)
    {
        DateTimeOffset now = SystemClock.Instance.UtcNow;
        List<ServiceDiagnosis> services = [];

        foreach ((string id, ProbeResult probe) in observed.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            ServiceDescriptor? service = snapshot?.FindService(id);

            Dictionary<string, string>? grades = null;
            PrivacyReadyVerdict? verdict = null;

            if (service is { Kind: TargetKind.Agent })
            {
                verdict = PrivacyReadiness.Evaluate(service, probe, now);
                grades = [];

                foreach (Control control in PrivacyReadiness.RequiredControls)
                {
                    grades[control.ToWire()] = PrivacyReadiness.GradeOf(service, probe, control, now);
                }
            }

            TrustZone? zone = service is not null && ServiceSetupCatalog.AnswersRequests(service.Capability)
                ? TrustZones.Of(
                    service.InferencePlacement,
                    RegistryProjection.Project(service, probe, SystemClock.Instance).Controls,
                    service.ProcessingRegions)
                : null;

            services.Add(new ServiceDiagnosis(
                id,
                probe,
                service,
                service is null ? null : PlacementRules.IsAcceptable(service.InferencePlacement, service.Endpoint),
                service is not null
                    && service.Attestations.TryGetValue(Control.LocalInference, out PropertyAttestation? attestation)
                    && attestation.IsValidAt(now),
                service is not null && service.Controls.Contains(Control.LocalInference)
                    ? PrivacyReadiness.GradeOf(service, probe, Control.LocalInference, now)
                    : null,
                verdict,
                grades)
            {
                Zone = zone,
            });
        }

        return services;
    }

    /// <summary>
    /// Rilevatore PII con modello: null quando nessun servizio dedicato e' abilitato, e in quel caso
    /// la scansione resta quella deterministica — che non e' un ripiego ma l'insieme di base.
    /// </summary>
    private static PiiModelDiagnosis? PiiModel(
        ConfigurationSnapshot? snapshot,
        IReadOnlyDictionary<string, ProbeResult> observed)
    {
        if (snapshot?.Registry.Services.FirstOrDefault(candidate =>
            candidate is { Enabled: true, Capability: ServiceCapability.PiiDetection, Adapter: AdapterKind.OnnxLocal })
            is not ServiceDescriptor service)
        {
            return null;
        }

        observed.TryGetValue(service.Id, out ProbeResult? probe);

        return new PiiModelDiagnosis(
            service.Id,
            service.Model,
            probe is { Health: ProbeHealth.Healthy },
            probe?.FailureCode,
            probe?.ExecutableHash);
    }

    /// <summary>
    /// Legge la coda di indicizzazione senza toccarla. Su una directory non ancora inizializzata non
    /// c'e' nulla da leggere, e dirlo e' meglio che far fallire una diagnosi.
    /// </summary>
    private static (int Pending, int Failed, bool Mismatched) MemoryJobs(
        StorePaths paths,
        ConfigurationLoadResult configuration)
    {
        if (!configuration.IsValid || !File.Exists(paths.DatabaseFile))
        {
            return (0, 0, false);
        }

        try
        {
            using SqliteConnection connection = new SqliteConnectionFactory(paths).Open();
            using SqliteCommand command = connection.CreateCommand();

            command.CommandText =
                "SELECT state, error_code, COUNT(*) FROM memory_jobs "
                + "WHERE state IN ('pending','failed') GROUP BY state, error_code;";

            int pending = 0;
            int failed = 0;
            bool mismatched = false;

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                string state = reader.GetString(0);
                string? error = reader.IsDBNull(1) ? null : reader.GetString(1);
                int count = reader.GetInt32(2);

                if (state is "pending")
                {
                    pending += count;
                    continue;
                }

                failed += count;
                mismatched |= error is EmbeddingMismatchCode;
            }

            return (pending, failed, mismatched);
        }
        catch (SqliteException)
        {
            // Schema non ancora migrato: la diagnosi continua senza la coda.
            return (0, 0, false);
        }
    }
}
