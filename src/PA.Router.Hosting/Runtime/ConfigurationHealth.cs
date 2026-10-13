using System.Globalization;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Policy;
using PA.Router.Registry.Loading;
using PA.Router.Registry.Probing;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Prontezza della configurazione: se e quanto si puo' lavorare con quella che c'e'.
///
/// Distinto da <see cref="ConfigurationState"/>, che risponde a un'altra domanda — se la
/// configurazione **in uso** combacia ancora con i file sul disco mentre il processo vive (S39).
/// Qui non esiste una configurazione in uso: c'e' una directory, e si tratta di capire se basta
/// per partire.
/// </summary>
public enum ConfigurationReadiness
{
    /// <summary>Si carica e non ha problemi: il router puo' lavorare.</summary>
    Valid,

    /// <summary>Non esiste un file di configurazione nella directory dati.</summary>
    Missing,

    /// <summary>Si carica, ma manca qualcosa che serve per un turno vero.</summary>
    Incomplete,

    /// <summary>Esiste e non si carica: forma, schema o riferimenti.</summary>
    Invalid,

    /// <summary>Esiste in una forma superata: <c>pa config migrate</c> la converte.</summary>
    MigrationRequired,
}

/// <summary>Gravita' di una sezione, dal punto di vista di chi deve decidere se intervenire.</summary>
public enum SectionSeverity
{
    /// <summary>A posto.</summary>
    Ok,

    /// <summary>Scelta dichiarata, non un difetto: va detta, non corretta.</summary>
    Info,

    /// <summary>Funziona, ma non come l'operatore probabilmente si aspetta.</summary>
    Warning,

    /// <summary>Manca qualcosa di necessario per lavorare.</summary>
    Missing,

    /// <summary>C'e' un errore vero: il router non parte cosi'.</summary>
    Error,
}

/// <summary>Sezioni in cui si divide la configurazione per l'operatore.</summary>
public enum ConfigurationSection
{
    /// <summary>Servizi del registro: chi risponde alle richieste.</summary>
    Provider,

    /// <summary>Modelli dichiarati dai servizi abilitati.</summary>
    Models,

    /// <summary>Profilo predefinito e servizi che ammette.</summary>
    Routing,

    /// <summary>Controlli richiesti dal profilo e attestazioni che li rendono effettivi.</summary>
    Privacy,

    /// <summary>Directory dati e conservazione dei contenuti.</summary>
    Storage,
}

/// <summary>Stato di una sezione, con il perche' e cosa farci.</summary>
/// <param name="Section">Sezione.</param>
/// <param name="Severity">Gravita'.</param>
/// <param name="Summary">Che cosa risulta, in una riga.</param>
/// <param name="Hint">Che cosa farebbe l'operatore per sistemarla; null se non c'e' nulla da fare.</param>
public sealed record SectionReport(
    ConfigurationSection Section,
    SectionSeverity Severity,
    string Summary,
    string? Hint = null);

/// <summary>
/// Che cosa sappiamo della configurazione, in una forma sola.
/// </summary>
/// <param name="Readiness">Prontezza complessiva.</param>
/// <param name="Issues">Problemi di validazione, quando il documento non si carica.</param>
/// <param name="Sections">Stato per sezione; vuoto quando non esiste uno snapshot da esaminare.</param>
/// <param name="Snapshot">Configurazione caricata, oppure null.</param>
public sealed record ConfigurationReport(
    ConfigurationReadiness Readiness,
    IReadOnlyList<ValidationIssue> Issues,
    IReadOnlyList<SectionReport> Sections,
    ConfigurationSnapshot? Snapshot)
{
    /// <summary>Stato dichiarativo per servizio; la disponibilita' osservata resta una probe.</summary>
    public IReadOnlyList<ServiceConfigurationReport> Services { get; init; } = [];
    /// <summary>
    /// Vero se il router puo' partire.
    ///
    /// Una sezione incompleta **non** impedisce l'avvio: senza un servizio ammesso un turno fallira'
    /// con il proprio codice, ma l'operatore deve poter entrare, guardare e correggere. Impediscono
    /// l'avvio solo i casi in cui uno snapshot non esiste affatto, perche' non c'e' nulla con cui
    /// costruire una sessione.
    /// </summary>
    public bool CanStart => Readiness is ConfigurationReadiness.Valid or ConfigurationReadiness.Incomplete;

    /// <summary>Vero se qualche sezione chiede un intervento.</summary>
    public bool NeedsAttention =>
        Readiness is not ConfigurationReadiness.Valid
        || Sections.Any(section => section.Severity is SectionSeverity.Missing or SectionSeverity.Error);

    /// <summary>Sezioni che l'operatore deve toccare, nell'ordine in cui conviene farlo.</summary>
    public IReadOnlyList<SectionReport> Attention =>
    [
        .. Sections
            .Where(section => section.Severity is SectionSeverity.Missing or SectionSeverity.Error or SectionSeverity.Warning)
            .OrderByDescending(section => section.Severity)
    ];
}

/// <summary>Stato condiviso da /config e setup; non sostituisce i gate di abilitazione o esecuzione.</summary>
public sealed record ServiceConfigurationReport(string Id, string State, SectionSeverity Severity, IReadOnlyList<string> Reasons)
{
    /// <summary>Origine della copertura dei controlli richiesti; stessi gradi di doctor.</summary>
    public IReadOnlyDictionary<Control, string> ControlGrades { get; init; } = new Dictionary<Control, string>();
    /// <summary>Disponibilita' osservata: non equivale ad ammissibilita' per una richiesta.</summary>
    public EffectiveService? Effective { get; init; }
    /// <summary>Motivi del valutatore per capacita' dichiarate e classe minima del profilo, senza budget o strumenti.</summary>
    public IReadOnlyList<PolicyReason> ScenarioReasons { get; init; } = [];
}

/// <summary>
/// Stato della configurazione in un punto solo (INV-22).
///
/// Prima esisteva soltanto la domanda binaria di <see cref="ConfigurationLoadResult.IsValid"/>: o si
/// carica, o si stampano i problemi e si esce. Bastava a un comando non interattivo e non basta a
/// nessun altro, perche' confonde tre casi che l'operatore vive in modo diverso — **non c'e' niente**,
/// **c'e' e non si legge**, **si legge e non serve a nulla** — e perche' non dice mai *quale* pezzo
/// manca.
///
/// Qui la risposta e' una sola e la usano tutti: il controllo d'avvio, il configuratore e la
/// visualizzazione. Non c'e' un secondo posto dove si decide che cosa significhi «configurato».
/// </summary>
public static class ConfigurationHealth
{
    /// <summary>Esamina la directory dati.</summary>
    public static ConfigurationReport Inspect(string dataDirectory, IClock clock, IReadOnlyDictionary<string, ProbeResult>? probes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(clock);

        ConfigurationReport report = Describe(new ConfigurationLoader(clock).Load(dataDirectory), clock, probes);

        // «Convertibile» non e' una proprieta' del codice del problema: lo sa solo chi la conversione
        // la sa fare. Fino alla S89 la prontezza guardava il solo codice `MIGRATION_REQUIRED`, e una
        // configurazione che `pa config migrate` avrebbe sistemato veniva dichiarata non valida —
        // con il configuratore che rispondeva «non posso correggere da solo» su un caso che si chiude
        // con un comando. Chiederlo al pianificatore e' l'unica risposta che non puo' divergere.
        return report.Readiness is ConfigurationReadiness.Invalid
            && ConfigMigration.Plan(dataDirectory, clock.UtcNow).IsApplicable
                ? report with { Readiness = ConfigurationReadiness.MigrationRequired }
                : report;
    }

    /// <summary>
    /// Esamina un caricamento gia' fatto.
    ///
    /// Serve ai percorsi che la configurazione l'hanno gia' letta: rileggerla darebbe una seconda
    /// verita' possibile, e due letture dello stesso file a pochi istanti di distanza possono
    /// differire.
    /// </summary>
    public static ConfigurationReport Describe(ConfigurationLoadResult result, IClock clock, IReadOnlyDictionary<string, ProbeResult>? probes = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(clock);

        if (!result.IsValid)
        {
            return new ConfigurationReport(ReadinessOf(result.Issues), result.Issues, [], null);
        }

        ConfigurationSnapshot snapshot = result.Require();
        ServiceConfigurationReport[] services = [.. snapshot.Registry.Services.Select(service =>
            DescribeService(snapshot, service, clock, probes?.GetValueOrDefault(service.Id)))];
        IReadOnlyList<SectionReport> sections = Sections(snapshot, services);

        bool incomplete = sections.Any(
            section => section.Severity is SectionSeverity.Missing or SectionSeverity.Error);

        return new ConfigurationReport(
            incomplete ? ConfigurationReadiness.Incomplete : ConfigurationReadiness.Valid,
            result.Issues,
            sections,
            snapshot)
        {
            Services = services,
        };
    }

    /// <summary>Configurazione e copertura osservata, senza fabbricare prove.</summary>
    public static ServiceConfigurationReport DescribeService(ConfigurationSnapshot snapshot, ServiceDescriptor service, IClock clock, ProbeResult? probe = null)
    {
        // An observation of another target must never certify this service.
        if (probe?.TargetId != service.Id) { probe = null; }
        EffectiveService effective = RegistryProjection.Project(service, probe, clock);
        IReadOnlyList<PolicyReason> scenario = ServiceSetupCatalog.ObservedCompatibility(snapshot, effective, clock);
        // Le garanzie riguardano chi risponde a una richiesta, e vengono dal tipo di servizio (ADR-032, fase 4): a un
        // servizio ausiliario non si chiedono.
        IReadOnlySet<Control> required = ServiceSetupCatalog.Guarantees(service);
        var grades = required.OrderBy(control => control.ToWire(), StringComparer.Ordinal).ToDictionary(control => control,
            control => !ServiceSetupCatalog.Declares(service, control) ? "non dichiarato" : PrivacyReadiness.GradeOf(service, probe, control, clock.UtcNow));
        IReadOnlyList<string> blockers = ServiceSetupCatalog.Blockers(snapshot, service, clock);
        bool incompatible = ServiceSetupCatalog.Compatibility(snapshot, service, clock).Count > 0;
        bool unobserved = probe is null || !probe.IsFreshAt(clock.UtcNow);
        string state = incompatible ? "IncompatibleWithPolicy"
            : service.HasPlaceholders ? "NeedsConfiguration"
            : blockers.Any(reason => reason.Contains("attestazione", StringComparison.OrdinalIgnoreCase)) ? "NeedsAttestation"
            : blockers.Count > 0 ? "Invalid"
            : !service.Enabled ? "Disabled"
            : unobserved ? "NeedsVerification"
            : !effective.IsUsable ? "Unavailable"
            : scenario.Count > 0 ? "ScenarioBlocked" : "ScenarioVerified";
        SectionSeverity severity = !service.Enabled ? SectionSeverity.Info
            : blockers.Count > 0 ? SectionSeverity.Error
            : unobserved ? SectionSeverity.Warning
            : !effective.IsUsable || scenario.Count > 0 ? SectionSeverity.Error : SectionSeverity.Ok;
        return new(service.Id, state, severity, [.. blockers,
            .. effective.Blockers.Where(reason => reason != ServiceBlockers.Disabled),
            .. (unobserved ? [] : scenario.Select(reason => reason.ToWire()))])
        {
            ControlGrades = grades,
            Effective = effective,
            ScenarioReasons = scenario,
        };
    }

    /// <summary>
    /// Traduce i problemi di validazione in una prontezza.
    ///
    /// L'ordine conta: una forma superata e' **convertibile** e va detta come tale, perche' il
    /// rimedio e' un comando e non una riscrittura a mano. Il file assente e' l'altro caso con un
    /// rimedio proprio. Tutto il resto e' un documento che esiste e non si legge.
    /// </summary>
    private static ConfigurationReadiness ReadinessOf(IReadOnlyList<ValidationIssue> issues)
    {
        if (issues.Any(issue => string.Equals(issue.Code, ValidationCodes.MigrationRequired, StringComparison.Ordinal)))
        {
            return ConfigurationReadiness.MigrationRequired;
        }

        // Il file di configurazione assente e' «non configurato»; un **altro** file mancante — un
        // profilo, il registro — e' una configurazione rotta, non una installazione nuova.
        bool configMissing = issues.Any(issue =>
            string.Equals(issue.Code, ValidationCodes.MissingFile, StringComparison.Ordinal)
            && ConfigurationLoader.ConfigFileNames.Contains(issue.Source, StringComparer.OrdinalIgnoreCase));

        return configMissing ? ConfigurationReadiness.Missing : ConfigurationReadiness.Invalid;
    }

    private static IReadOnlyList<SectionReport> Sections(ConfigurationSnapshot snapshot, IReadOnlyList<ServiceConfigurationReport> services) =>
    [
        Provider(snapshot, services),
        Models(snapshot),
        Routing(snapshot),
        Privacy(services),
        Storage(snapshot),
    ];

    /// <summary>
    /// Servizi abilitati e utilizzabili.
    ///
    /// Il registro consegnato nasce con i servizi **disabilitati** e con percorsi segnaposto: e' una
    /// scelta di sicurezza, non un difetto, ma significa che una installazione appena creata non puo'
    /// ancora rispondere. Dirlo qui e' la differenza fra un router che «non funziona» e uno che
    /// aspetta una decisione.
    /// </summary>
    private static SectionReport Provider(ConfigurationSnapshot snapshot, IReadOnlyList<ServiceConfigurationReport> services)
    {
        IReadOnlyList<ServiceDescriptor> enabled =
            [.. snapshot.Registry.Services.Where(service => service.Enabled)];

        if (enabled.Count == 0)
        {
            return new SectionReport(
                ConfigurationSection.Provider,
                SectionSeverity.Missing,
                "nessun servizio abilitato: il registro ne dichiara "
                    + snapshot.Registry.Services.Count.ToString(CultureInfo.InvariantCulture),
                "abilita un servizio e verificane percorso o endpoint");
        }

        // La regola su modello ed eseguibile e' gia' scritta in ServiceDescriptor.HasPlaceholders, ed
        // e' la stessa che ferma una probe (CliProbe): un secondo elenco di segnaposto scritto qui
        // sarebbe rimasto indietro al primo modello aggiunto ai file consegnati. L'endpoint non vi
        // rientra e va aggiunto: un indirizzo segnaposto non e' un servizio raggiungibile.
        IReadOnlyList<ServiceDescriptor> unusable =
        [
            .. enabled.Where(service =>
                service.HasPlaceholders
                || service.Endpoint?.OriginalString.Contains(
                    ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal) == true)
        ];

        if (unusable.Count > 0)
        {
            return new SectionReport(
                ConfigurationSection.Provider,
                SectionSeverity.Error,
                "segnaposto ancora da sostituire in " + string.Join(", ", unusable.Select(service => service.Id)),
                "indica il percorso verificato del programma, oppure l'indirizzo e il modello del servizio");
        }

        string[] blocked = [.. services.Where(service => service.Effective?.Service.Enabled == true
            && service.Severity == SectionSeverity.Error).Select(service => service.Id)];
        if (blocked.Length > 0)
        {
            return new(ConfigurationSection.Provider, SectionSeverity.Error,
                "servizi con requisiti da completare: " + string.Join(", ", blocked),
                "apri Requisiti e verifiche nel servizio per conoscere il motivo");
        }

        return new SectionReport(
            ConfigurationSection.Provider,
            SectionSeverity.Ok,
            enabled.Count.ToString(CultureInfo.InvariantCulture) + " abilitati: "
                + string.Join(", ", enabled.Select(service => service.Id)));
    }

    /// <summary>Modelli dei servizi abilitati: un segnaposto non e' un modello installato.</summary>
    private static SectionReport Models(ConfigurationSnapshot snapshot)
    {
        IReadOnlyList<ServiceDescriptor> enabled =
            [.. snapshot.Registry.Services.Where(service => service.Enabled)];

        if (enabled.Count == 0)
        {
            return new SectionReport(
                ConfigurationSection.Models,
                SectionSeverity.Info,
                "nessun servizio abilitato da cui leggere un modello");
        }

        IReadOnlyList<ServiceDescriptor> placeholder =
        [
            .. enabled.Where(service =>
                service.Model?.Contains(
                    ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal) == true)
        ];

        if (placeholder.Count > 0)
        {
            return new SectionReport(
                ConfigurationSection.Models,
                SectionSeverity.Error,
                "modello segnaposto in " + string.Join(", ", placeholder.Select(service => service.Id)),
                "indica un modello installato; 'pa doctor' dice quali ci sono");
        }

        return new SectionReport(
            ConfigurationSection.Models,
            SectionSeverity.Ok,
            string.Join(", ", enabled.Select(service => service.Model ?? service.Id)));
    }

    /// <summary>
    /// Profilo predefinito, e se ammette almeno uno dei servizi abilitati.
    ///
    /// Un profilo che non ne ammette nessuno e' una configurazione che si carica e nega ogni turno:
    /// il difetto non si vede finche' qualcuno non prova a parlare, ed e' esattamente il caso che
    /// questo controllo esiste per anticipare.
    /// </summary>
    private static SectionReport Routing(ConfigurationSnapshot snapshot)
    {
        if (!snapshot.Profiles.TryGetValue(snapshot.Config.DefaultProfileId, out PolicyDocument? profile))
        {
            return new SectionReport(
                ConfigurationSection.Routing,
                SectionSeverity.Error,
                "il profilo predefinito '" + snapshot.Config.DefaultProfileId + "' non e' dichiarato",
                "scegli un profilo fra quelli presenti in policies/");
        }


        // «Ammette un servizio» significa uno che **risponde alle richieste**: con il solo embedding acceso il
        // profilo ammetterebbe qualcosa e il router non saprebbe comunque rispondere a nessuno.
        IReadOnlyList<string> usable =
        [
            .. snapshot.Registry.Services
                .Where(service => service.Enabled && profile.Constraints.AllowsService(service.Id)
                    && ServiceSetupCatalog.AnswersRequests(service.Capability))
                .Select(service => service.Id)
        ];

        if (usable.Count == 0)
        {
            return new SectionReport(
                ConfigurationSection.Routing,
                SectionSeverity.Missing,
                "il profilo '" + profile.ProfileId + "' non ammette nessun servizio abilitato",
                "aggiungi il servizio all'elenco del profilo, oppure abilitane uno che vi compare");
        }

        return new SectionReport(
            ConfigurationSection.Routing,
            SectionSeverity.Ok,
            profile.ProfileId + " ammette " + string.Join(", ", usable));
    }

    /// <summary>
    /// Garanzie che i servizi accesi devono avere e che nessuno ha ancora osservato o confermato.
    ///
    /// E' un **avviso**, non un errore: la configurazione e' coerente e il router parte. Semplicemente
    /// quei servizi verranno negati, o varranno come un terzo, e scoprirlo al primo turno invece che all'avvio e' la
    /// differenza fra una spiegazione e un mistero. Dal 25 settembre 2026 (ADR-032, fase 4) le garanzie vengono dal tipo di
    /// servizio (<see cref="ServiceSetupCatalog.Guarantees"/>), non da un elenco del profilo.
    /// </summary>
    private static SectionReport Privacy(IReadOnlyList<ServiceConfigurationReport> services)
    {
        // Le garanzie si chiedono a chi risponde a una richiesta: un embedding acceso non le dichiara per disegno (S11).
        ServiceConfigurationReport[] enabled = [.. services.Where(service => service.Effective?.Service.Enabled == true
            && ServiceSetupCatalog.AnswersRequests(service.Effective.Service.Capability))];
        if (enabled.Length == 0)
        {
            return new(ConfigurationSection.Privacy, SectionSeverity.Info, "nessun servizio abilitato da verificare");
        }
        string[] lacking = [.. enabled.Where(service => service.Severity != SectionSeverity.Ok
            || ServiceSetupCatalog.Guarantees(service.Effective!.Service).Any(control => !service.Effective.Controls.Contains(control)))
            .Select(service => service.Id)];

        if (lacking.Length > 0)
        {
            return new SectionReport(
                ConfigurationSection.Privacy,
                SectionSeverity.Warning,
                "conferme ancora da dare o da verificare in " + string.Join(", ", lacking),
                "apri il servizio: Test verifica le proprieta' osservabili; Conferme raccoglie cio' che solo tu puoi dire");
        }

        return new SectionReport(ConfigurationSection.Privacy, SectionSeverity.Ok, "garanzie coperte per ogni servizio acceso");
    }

    /// <summary>
    /// Directory dati e conservazione.
    ///
    /// <c>memory_only</c> non e' un difetto: e' una scelta di riservatezza, e va detta come tale —
    /// ma chi si aspetta una conversazione con contesto deve sapere prima che non ci sara'.
    /// </summary>
    private static SectionReport Storage(ConfigurationSnapshot snapshot) =>
        // Memoria accesa e classe minima `secret`: ogni osservazione nasce segreta e un segreto non entra in memoria
        // (S10). E' una configurazione coerente e silenziosa — la ricerca resta vuota per sempre senza un errore —
        // quindi va detta qui, dove l'operatore guarda prima di chiedersi perche'.
        MemoryQueries.NeverRecords(snapshot)
            ? new SectionReport(
                ConfigurationSection.Storage,
                SectionSeverity.Warning,
                "memoria attiva ma nessun ricordo verra' scritto: il profilo tratta ogni richiesta come un segreto",
                "alza minimumDataClass a personal o internal nelle regole dell'uso, oppure spegni la memoria")
        : snapshot.Config.ContentMode is ContentMode.Encrypted
            ? new SectionReport(
                ConfigurationSection.Storage,
                SectionSeverity.Ok,
                "contenuti cifrati in " + snapshot.DataDirectory)
            : new SectionReport(
                ConfigurationSection.Storage,
                SectionSeverity.Info,
                "contenuti " + snapshot.Config.ContentMode.ToWire() + ": nulla resta dopo la chiusura",
                "per una conversazione con contesto serve una directory creata con 'pa init --persist-content'");
}
