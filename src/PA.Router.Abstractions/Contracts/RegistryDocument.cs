namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Registro dei servizi validato (03-specs/REGISTRY.md, registry.schema.json). E' una
/// dichiarazione dell'operatore: la validita' strutturale non implica che un servizio sia
/// utilizzabile, cosa che dipende dal probe e dalla certificazione.
///
/// Dalla versione 2 il registro elenca servizi, uno per capacita' offerta (ADR-015): cio' che il
/// router puo' usare e' esattamente cio' che questo documento dichiara, e nessun nome di modello
/// vive nel codice.
/// </summary>
/// <param name="SchemaVersion">Versione dello schema, sempre 2.</param>
/// <param name="Revision">Revisione dichiarata dall'operatore.</param>
/// <param name="Services">Servizi dichiarati, con ID unico.</param>
public sealed record RegistryDocument(
    int SchemaVersion,
    string Revision,
    IReadOnlyList<ServiceDescriptor> Services)
{
    /// <summary>
    /// Valuta dei prezzi del registro, null se nessun servizio ha un prezzo. E' una sola: la lettura rifiuta un registro
    /// che ne mescola due.
    /// </summary>
    public string? Currency =>
        Services.Select(service => service.Pricing?.Currency).FirstOrDefault(value => value is not null);
}

/// <summary>
/// Valore di calibrazione con la propria origine. Un valore <c>declared</c> e' una preferenza
/// dell'operatore, non un benchmark; un valore assente vale <see cref="TargetCalibration.NeutralScore"/>
/// e non viene stimato (03-specs/ROUTING.md).
/// </summary>
/// <param name="Value">Valore 0..1, dove maggiore e' preferibile.</param>
/// <param name="Source">Origine dichiarata o misurata.</param>
/// <param name="ObservedAt">Istante della misura, null per i valori dichiarati.</param>
public readonly record struct CalibrationValue(
    double Value,
    CalibrationSource Source,
    DateTimeOffset? ObservedAt);

/// <summary>
/// Un servizio e' la combinazione concreta di capacita', adapter, autenticazione e collocazione:
/// lo stesso programma con inferenza locale e con inferenza ospitata sono due servizi distinti.
/// </summary>
/// <param name="Id">Identificatore stabile.</param>
/// <param name="Capability">Capacita' offerta; e' la dimensione su cui il router instrada.</param>
/// <param name="Adapter">Adapter che lo raggiunge.</param>
/// <param name="Enabled">Abilitazione dell'operatore; il default degli esempi e' false.</param>
/// <param name="InferencePlacement">Dove avviene l'inferenza; e' la collocazione che i gate guardano.</param>
/// <param name="Model">Modello esatto, oppure null per le CLI che lo scelgono nativamente.</param>
/// <param name="Dimension">Dimensione dei vettori, solo per la capacita' embedding.</param>
/// <param name="Fingerprint">Fingerprint osservato del modello di embedding.</param>
/// <param name="AuthMode">Autenticazione usata dal programma, distinta dal trasporto.</param>
/// <param name="SecretRef">Nome del segreto nel vault locale, mai il valore.</param>
/// <param name="Capabilities">Capacita' agentiche dichiarate, distinte da <paramref name="Capability"/>.</param>
/// <param name="Controls">Controlli dichiarati.</param>
/// <param name="ProcessingRegions">Regioni dichiarate; l'elenco vuoto non significa <c>eu</c>.</param>
/// <param name="Attestations">
/// Conferme dell'operatore, **una per garanzia**: chi l'ha confermata e quando. Dal 25 settembre 2026 (ADR-032, fase 4)
/// una conferma vale finche' l'operatore non la toglie; ogni anno <c>doctor</c> e l'avvio ricordano di ricontrollarla.
/// </param>
/// <param name="Latency">Calibrazione di latenza, null se non dichiarata.</param>
/// <param name="ContextWindowTokens">Finestra dichiarata; il budget effettivo e' il minimo con l'osservato.</param>
/// <param name="MaxOutputTokens">Output massimo dichiarato, minore della finestra.</param>
/// <param name="MaxConcurrent">Esecuzioni simultanee ammesse su questo servizio.</param>
/// <param name="ThroughputKibPerSecond">Velocita' osservata, per i servizi con budget di latenza.</param>
/// <param name="Endpoint">Endpoint di rete, richiesto dagli adapter HTTP.</param>
/// <param name="Executable">Percorso assoluto del programma, richiesto dalle CLI.</param>
/// <param name="Arguments">Argomenti aggiunti all'invocazione del programma.</param>
/// <param name="Pricing">Prezzo a consumo dichiarato, null se il registro non ne porta uno.</param>
/// <param name="ToolCalling">
/// Capacita' di usare strumenti **dichiarata** dall'operatore, per le API remote che non la lasciano osservare.
/// Per un modello locale non conta: si osserva dal modello installato (ADR-020).
/// </param>
public sealed record ServiceDescriptor(
    string Id,
    ServiceCapability Capability,
    AdapterKind Adapter,
    bool Enabled,
    Placement InferencePlacement,
    string? Model,
    int? Dimension,
    string? Fingerprint,
    AuthMode AuthMode,
    string? SecretRef,
    IReadOnlySet<Capability> Capabilities,
    IReadOnlySet<Control> Controls,
    IReadOnlySet<ProcessingRegion> ProcessingRegions,
    IReadOnlyDictionary<Control, PropertyAttestation> Attestations,
    CalibrationValue? Latency,
    int ContextWindowTokens,
    int MaxOutputTokens,
    int MaxConcurrent,
    double? ThroughputKibPerSecond,
    Uri? Endpoint,
    string? Executable,
    IReadOnlyList<string> Arguments,
    ServicePricing? Pricing = null,
    bool ToolCalling = false)
{
    /// <summary>
    /// Nome dato dall'operatore all'istanza: «il mio computer», «server di casa». Null significa che
    /// non ne ha dato uno e la resa ne calcola uno predefinito.
    ///
    /// Non e' l'identificatore e non lo sostituisce. L'identificatore e' opaco e stabile perche' a lui
    /// sono legate le conferme firmate, l'elenco dei servizi che un uso accetta e i permessi: un nome
    /// che descrive il contenuto diventa falso appena il contenuto cambia, e rinominarlo scollega in
    /// silenzio tutto cio' che lo citava. Il nome invece si cambia quando si vuole, perche' nessuno ci
    /// si appoggia.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// L'operatore chiede al fornitore di tenere in cache l'inizio della conversazione (ADR-022). Fa pagare meno i
    /// token riletti a ogni turno, e significa che il fornitore **conserva il prompt per alcuni minuti**: per questo
    /// e' spento finche' non lo accende chi configura, e non e' mai una scelta del router.
    /// </summary>
    public bool PromptCaching { get; init; }

    /// <summary>
    /// Se il modello ragiona prima di rispondere; null lascia la scelta al modello. false lo spegne: senza GPU il
    /// ragionamento costa minuti anche per un saluto (23 settembre 2026). Solo per la chat dell'adapter ollama.
    /// </summary>
    public bool? Thinking { get; init; }

    /// <summary>
    /// Quanto il modello resta in memoria di Ollama dopo l'ultimo uso, in minuti: -1 finche' Ollama e' acceso, 0 si scarica
    /// subito; null lascia decidere a Ollama. Lo propone il setup dalla memoria del computer e lo conferma l'operatore
    /// (23 settembre 2026). Solo per gli adapter ollama e ollama-embed.
    /// </summary>
    public int? KeepAliveMinutes { get; init; }

    /// <summary>
    /// L'operatore lascia che l'agente tenga una sessione propria e la prosegua da un turno all'altro (ADR-022).
    /// L'agente non rilegge da capo cio' che ha gia' letto; in cambio il suo transcript resta **in chiaro nella sua
    /// cartella**, fuori dallo store del router. Spento, l'agente riceve l'ordine di non conservare nulla.
    /// </summary>
    public bool NativeSession { get; init; }

    /// <summary>
    /// Posizione nell'ordine dei modelli dell'operatore, dal piu' capace (1) al meno capace (ADR-027): chi configura sa
    /// ordinare i propri modelli anche quando non sa dare un punteggio. Pari posizione, pari capacita'. Solo per i
    /// servizi che rispondono alle richieste; null se l'operatore non l'ha dato.
    /// </summary>
    public int? Rank { get; init; }

    /// <summary>
    /// Correzioni per dimensione dell'operatore (ADR-027), da 1 a 3; ogni valore assente si ricava dall'ordine. Solo per
    /// i servizi che rispondono alle richieste.
    /// </summary>
    public ServiceLevels? Levels { get; init; }

    /// <summary>Prefisso dei segnaposto degli esempi, da sostituire prima di abilitare.</summary>
    public const string PlaceholderPrefix = "REPLACE_WITH";

    /// <summary>
    /// Prezzo marginale di una richiesta. Quello dichiarato, quando c'e'. Senza prezzo dichiarato, un
    /// servizio in abbonamento o con l'inferenza su una macchina dell'operatore non ha un costo a
    /// consumo, e vale zero; un servizio di terzi senza prezzo resta sconosciuto, e il ranking usa il
    /// costo dichiarato in calibrazione o il neutro, senza inventare un prezzo.
    /// </summary>
    public TokenPrice? MarginalPrice =>
        Pricing?.Price
        ?? (AuthMode is AuthMode.Subscription || !IsThirdParty(InferencePlacement) ? TokenPrice.Free : null);

    /// <summary>Vero se modello o eseguibile sono ancora segnaposto di esempio.</summary>
    public bool HasPlaceholders =>
        (Model is not null && Model.Contains(PlaceholderPrefix, StringComparison.Ordinal))
        || (Executable is not null && Executable.Contains(PlaceholderPrefix, StringComparison.Ordinal));

    /// <summary>
    /// Tipo derivato dalla capacita': un servizio <c>code-agent</c> e' un programma agentico, ogni
    /// altro e' un modello raggiunto dall'adapter. Non e' un campo del registro.
    /// </summary>
    public TargetKind Kind => Capability is ServiceCapability.CodeAgent ? TargetKind.Agent : TargetKind.Llm;

    /// <summary>
    /// Confine derivato dalla collocazione dell'inferenza. I gate non lo usano: guardano la
    /// collocazione, che distingue una GPU in LAN da un servizio di terzi.
    /// </summary>
    public Boundary Boundary => LeavesMachine(InferencePlacement) ? Boundary.Cloud : Boundary.Local;

    /// <summary>Vero se l'inferenza esce dalla macchina dell'operatore.</summary>
    public static bool LeavesMachine(Placement placement) =>
        placement is Placement.PrivateRemote or Placement.ThirdParty;

    /// <summary>Vero se l'inferenza e' affidata a un terzo, non a una macchina dell'operatore.</summary>
    public static bool IsThirdParty(Placement placement) => placement is Placement.ThirdParty;
}

/// <summary>
/// Quale capacita' instradare, dedotta dalle capacita' agentiche che la richiesta dichiara.
/// </summary>
public static class CapabilityRouting
{
    /// <summary>
    /// Una richiesta che ha bisogno di leggere codice, scrivere file o eseguire comandi vuole un
    /// agente; tutto il resto e' chat. Non si degrada: se serve <c>file_write</c> e nessun agente
    /// e' ammesso, il turno nega invece di rispondere con una chat (03-specs/ROUTING.md).
    /// </summary>
    public static ServiceCapability ForRequest(IReadOnlySet<Capability> required)
    {
        ArgumentNullException.ThrowIfNull(required);

        return required.Any(capability => capability is not Capability.Chat)
            ? ServiceCapability.CodeAgent
            : ServiceCapability.Chat;
    }

    /// <summary>
    /// Abilita' per cui un servizio di questa capacita' puo' essere scelto, quindi le sole a cui un
    /// voto serve. Una chat risponde solo alla chat; un agente all'analisi e alla modifica del
    /// codice, perche' una richiesta senza capacita' agentiche non viene mai instradata a un agente
    /// (<see cref="ForRequest"/>). Le altre capacita' non passano dal ranking.
    /// </summary>
    public static IReadOnlyList<Intent> AbilitiesOf(ServiceCapability capability) => capability switch
    {
        ServiceCapability.Chat => [Intent.Chat],
        ServiceCapability.CodeAgent => [Intent.CodeAnalysis, Intent.CodeChange],
        _ => [],
    };
}

/// <summary>
/// Conferma dell'operatore su cio' che il processo non puo' osservare da solo: chi l'ha data e quando.
///
/// Dal 25 settembre 2026 (ADR-032, fase 4) **una conferma non scade**: vale finche' l'operatore non la toglie, e ogni
/// anno <c>doctor</c> e l'avvio del REPL ricordano di ricontrollarla (<see cref="ReminderDue"/>). Supera la regola di S30
/// «senza scadenza l'attestazione viene rifiutata»: una scadenza che spegneva un servizio in silenzio a meta' di una
/// giornata di lavoro proteggeva meno di un promemoria che si legge. <see cref="ExpiresAt"/> resta come dato, se c'e', e
/// non decide nulla.
/// </summary>
/// <param name="By">Chi conferma.</param>
/// <param name="ObservedAt">Quando ha verificato.</param>
/// <param name="ExpiresAt">Data scritta prima della fase 4; non decide piu' nulla.</param>
/// <param name="Note">Nota dell'operatore.</param>
public sealed record PropertyAttestation(
    string By,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? ExpiresAt,
    string? Note)
{
    /// <summary>Dopo quanto tempo una conferma va ricontrollata: un anno.</summary>
    public static TimeSpan ReminderAfter { get; } = TimeSpan.FromDays(365);

    /// <summary>
    /// Vero se la conferma vale all'istante indicato: qualcuno l'ha data con il proprio nome (non un segnaposto) e con la
    /// data della verifica, non nel futuro. Non scade.
    /// </summary>
    public bool IsValidAt(DateTimeOffset instant) =>
        !string.IsNullOrWhiteSpace(By)
        && !By.Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal)
        && ObservedAt is DateTimeOffset observed && observed <= instant;

    /// <summary>Vero se la conferma vale ed e' stata data da almeno un anno: e' ora di ricontrollarla.</summary>
    public bool ReminderDue(DateTimeOffset instant) =>
        IsValidAt(instant) && ObservedAt is DateTimeOffset observed && instant - observed >= ReminderAfter;
}

/// <summary>
/// Livelli di capacita' di un servizio dichiarati dall'operatore (ADR-027), da 1 a 3. Null significa «ricavalo»: dal
/// suo posto nell'ordine dei modelli, e per il contesto dalla finestra dichiarata.
/// </summary>
/// <param name="General">Livello generale.</param>
/// <param name="Reasoning">Ragionamento.</param>
/// <param name="Coding">Codice.</param>
/// <param name="Context">Lavoro su testi lunghi.</param>
public sealed record ServiceLevels(int? General, int? Reasoning, int? Coding, int? Context);
