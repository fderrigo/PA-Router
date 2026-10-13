namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Configurazione operativa validata (03-specs/CONFIG.md, config.schema.json). Contiene percorsi,
/// limiti e preferenze: non contiene autorizzazioni, che stanno nei profili di policy, ne' segreti,
/// che restano nel vault locale referenziati per nome.
/// </summary>
/// <param name="SchemaVersion">Versione dello schema, sempre 2.</param>
/// <param name="DefaultProfileId">Profilo applicato alle sessioni personali.</param>
/// <param name="PolicyFiles">Percorsi dei profili, relativi alla directory dati.</param>
/// <param name="RegistryFile">Percorso del registro, relativo alla directory dati.</param>
/// <param name="ContentMode">Conservazione dei contenuti.</param>
/// <param name="PreferSubscription">Preferenza di ranking per i target a subscription.</param>
public sealed record ConfigDocument(
    int SchemaVersion,
    string DefaultProfileId,
    IReadOnlyList<string> PolicyFiles,
    string RegistryFile,
    ContentMode ContentMode,
    bool PreferSubscription,
    ApiConfig Api,
    TelemetryConfig Telemetry,
    RetentionConfig Retention,
    LimitsConfig Limits,
    MemoryConfig Memory,
    ExtensionsConfig Extensions,
    RoutingConfig Routing,
    ToolsConfig? Tools = null,
    string Language = "en");

/// <summary>
/// Impostazioni dell'analisi della richiesta. Dal 24 settembre 2026 (ADR-032, fase 3) non porta piu' le soglie per
/// difficolta' (<c>bands</c>): la qualita' di un modello e' il suo posto nell'ordine, e la finestra minima si calcola dal
/// payload con un pavimento fisso (<c>RequestAnalyzer.MinimumWindowTokens</c>).
/// </summary>
/// <param name="PiiLatencyBudgetMs">Budget del rilevamento PII inline, 0 se non dichiarato.</param>
/// <param name="Analyzer">Analizzatore della richiesta; null se non configurato.</param>
public sealed record RoutingConfig(
    int PiiLatencyBudgetMs,
    AnalyzerConfig? Analyzer = null)
{
    /// <summary>
    /// Quante volte il budget della richiesta vale per gli allegati (ADR-032, fase 2). Con il budget consegnato di 1500 ms
    /// sono i due minuti che fino al 24 settembre 2026 si dichiaravano a parte (<c>piiAttachmentBudgetMs</c>): su un PC
    /// comune il modello legge da 0,4 a 1 KB al secondo, e un allegato di qualche pagina deve potersi esaminare per intero
    /// invece di rendere il turno non classificabile.
    /// </summary>
    public const int AttachmentBudgetFactor = 80;

    /// <summary>
    /// Tempo del rilevatore a modello sugli allegati: il budget della richiesta per <see cref="AttachmentBudgetFactor"/>.
    /// Un budget solo da impostare; quello degli allegati si ricava, qui e in nessun altro posto.
    /// </summary>
    public int PiiAttachmentBudgetMs => PiiLatencyBudgetMs * AttachmentBudgetFactor;
}

/// <summary>
/// Banda di difficolta' stimata dall'analisi della richiesta, dalla dimensione e dalle regole. Dal 24 settembre 2026
/// (ADR-032, fase 3) non impone piu' un voto ne' una finestra: resta come concetto del racconto e della diagnosi.
/// </summary>
public enum DifficultyBand
{
    [WireValue("trivial")]
    Trivial,

    [WireValue("standard")]
    Standard,

    [WireValue("heavy")]
    Heavy,
}

/// <summary>
/// Host API locale. L'host e' fissato a loopback dallo schema: la beta non espone rete.
/// </summary>
/// <param name="Host">Sempre 127.0.0.1.</param>
/// <param name="Port">Porta locale, 1024..65535.</param>
/// <param name="ServiceProfileId">Profilo imposto dal backend alle sessioni chatbot.</param>
/// <param name="ServiceTokenRef">Nome del segreto nel vault locale, mai il valore.</param>
/// <param name="ProjectId">Progetto dedicato al chatbot.</param>
public sealed record ApiConfig(
    string Host,
    int Port,
    string ServiceProfileId,
    string ServiceTokenRef,
    Guid ProjectId);

/// <param name="Exporter">Esportatore attivo; il default e' <c>none</c>.</param>
/// <param name="Endpoint">Destinazione OTLP, null quando l'esportatore e' <c>none</c>.</param>
public sealed record TelemetryConfig(TelemetryExporter Exporter, Uri? Endpoint);

/// <param name="AuditDays">Conservazione dei metadata di audit, massimo 30.</param>
/// <param name="ContentDays">Conservazione dei contenuti cifrati, massimo 7.</param>
/// <param name="EventHours">Replay degli eventi di stato, massimo 24.</param>
public sealed record RetentionConfig(int AuditDays, int ContentDays, int EventHours);

/// <summary>
/// Limiti di esecuzione. Dal 24 settembre 2026 (ADR-032, fase 2) stanno **solo** qui: tempo massimo, tentativi e limiti in
/// byte non si dichiarano piu' anche nel profilo, e la compilazione della policy li legge da questa sezione.
/// </summary>
/// <param name="MaxConcurrency">Slot di esecuzione globali, 1..8.</param>
/// <param name="TimeoutSeconds">Deadline globale, massimo 300.</param>
/// <param name="MaxAttempts">Tentativi totali, massimo 2.</param>
public sealed record LimitsConfig(int MaxConcurrency, int TimeoutSeconds, int MaxAttempts)
{
    /// <summary>Byte UTF-8 dell'input quando il file non li dichiara: 64 KiB, il tetto della beta.</summary>
    public const int DefaultMaxInputBytes = 64 * 1024;

    /// <summary>Byte UTF-8 del contesto quando il file non li dichiara: 512 KiB, il tetto della beta.</summary>
    public const int DefaultMaxContextBytes = 512 * 1024;

    /// <summary>Byte UTF-8 della risposta quando il file non li dichiara: 512 KiB, il tetto della beta.</summary>
    public const int DefaultMaxOutputBytes = 512 * 1024;

    /// <summary>Limite byte UTF-8 dell'input (<c>limits.maxInputBytes</c>).</summary>
    public int MaxInputBytes { get; init; } = DefaultMaxInputBytes;

    /// <summary>Limite byte UTF-8 del contesto allegato (<c>limits.maxContextBytes</c>).</summary>
    public int MaxContextBytes { get; init; } = DefaultMaxContextBytes;

    /// <summary>Limite byte UTF-8 della risposta (<c>limits.maxOutputBytes</c>).</summary>
    public int MaxOutputBytes { get; init; } = DefaultMaxOutputBytes;

    /// <summary>Turni precedenti rimandati al servizio quando il file non lo dichiara.</summary>
    public const int DefaultHistoryTurns = 8;

    /// <summary>
    /// Quanti turni precedenti accompagnano una richiesta (<c>limits.historyTurns</c>, S116). Le API di chat sono
    /// senza stato: la conversazione si rimanda a ogni turno, e questo numero e' cio' che si paga in token su un
    /// servizio a consumo. Zero significa che ogni richiesta parte da sola. Il budget di contesto del profilo puo'
    /// tagliare prima, dai turni piu' vecchi.
    /// </summary>
    public int HistoryTurns { get; init; } = DefaultHistoryTurns;

    /// <summary>
    /// Spesa massima per richiesta (<c>limits.maxCostPerRequest</c>, ADR-032 fase 5), nella valuta dei prezzi del registro.
    /// Un servizio che su questa richiesta puo' costare di piu' — la stima con l'uscita limitata dal suo tetto dichiarato —
    /// resta fuori con COST_LIMIT; un servizio di terzi senza prezzo anche, perche' non si puo' dimostrare che ci stia
    /// sotto. I servizi senza costo a consumo passano sempre. Null: nessun tetto.
    /// </summary>
    public double? MaxCostPerRequest { get; init; }
}

/// <summary>
/// Memoria semantica locale. La durata dei ricordi e' <paramref name="RetentionDays"/>, indipendente dalla durata dei
/// transcript; dal 24 settembre 2026 (ADR-032, fase 2) nessun profilo ne dichiara un secondo tetto.
/// </summary>
/// <param name="Enabled">Memoria attiva.</param>
/// <param name="Required">Se true un errore di memoria fa fallire l'esecuzione.</param>
/// <param name="EmbeddingServiceId">Servizio con capacita' embedding; null lascia la ricerca lessicale dichiarata.</param>
/// <param name="TopK">Risultati restituiti dal retrieval.</param>
/// <param name="MaxChunksPerProject">Chunk attivi per progetto.</param>
/// <param name="CacheMiB">Tetto della cache di memoria e indici.</param>
/// <param name="RetentionDays">Durata richiesta dei ricordi.</param>
public sealed record MemoryConfig(
    bool Enabled,
    bool Required,
    string? EmbeddingServiceId,
    int TopK,
    int MaxChunksPerProject,
    int CacheMiB,
    int RetentionDays,
    int MaxEmbeddingDimension)
{
    /// <summary>
    /// Vero se nessun servizio di embedding e' stato scelto. La ricerca degrada in
    /// <c>lexical_only</c> dichiarato, oppure fallisce se il profilo richiede la memoria.
    /// </summary>
    public bool IsUnconfigured => EmbeddingServiceId is null;
}

/// <param name="Enabled">Estensioni attive.</param>
/// <param name="PluginRoot">Radice dei plugin, relativa alla directory dati.</param>
/// <param name="AllowExecutableTools">Strumenti eseguibili, disattivati per default.</param>
public sealed record ExtensionsConfig(bool Enabled, string PluginRoot, bool AllowExecutableTools);

/// <summary>
/// Analizzatore della richiesta (ADR-026): un modello linguistico piccolo del registro, su questa macchina, che estrae i
/// segnali del profilo di domanda. Assente significa nessun profilo e nessun costo.
///
/// Dal 24 settembre 2026 (ADR-032, fase 2) non ha piu' un modo da scegliere: configurato vuol dire acceso, e decide solo se
/// il modello e il prompt hanno un esito misurato (<c>RequestAnalyzerModel.MeasuredFor</c>); altrimenti gira in ombra da
/// solo.
/// </summary>
/// <param name="ServiceId">Servizio con capacita' <c>request-analysis</c>, su loopback.</param>
public sealed record AnalyzerConfig(string ServiceId);

/// <summary>
/// Strumenti delle applicazioni esterne (ADR-020). Assente significa spenti: una configurazione scritta prima
/// che esistessero continua a valere, e non chiama nulla.
/// </summary>
/// <param name="Enabled">Strumenti accesi.</param>
/// <param name="Limits">Limiti del giro modello-strumento; il profilo puo' solo restringerli.</param>
/// <param name="Sources">Sorgenti configurate dall'operatore; le uniche che possono essere stdio.</param>
/// <param name="Attestations">Attestazioni dell'operatore, per strumento e impronta.</param>
/// <param name="Revocations">Strumenti revocati dall'operatore: non si usano, qualunque cosa dichiari un'applicazione.</param>
public sealed record ToolsConfig(
    bool Enabled,
    ToolLimits Limits,
    IReadOnlyList<ToolSourceConfig> Sources,
    IReadOnlyList<ToolAttestationConfig> Attestations,
    IReadOnlyList<ToolRevocation> Revocations);

/// <summary>Sorgente degli strumenti configurata dall'operatore.</summary>
/// <param name="Id">Identificatore.</param>
/// <param name="Kind">Tipo.</param>
/// <param name="Url">Indirizzo, per le sorgenti HTTP.</param>
/// <param name="Command">Percorso assoluto del programma, per le sorgenti stdio.</param>
/// <param name="Arguments">Argomenti del programma.</param>
public sealed record ToolSourceConfig(string Id, ToolSourceKind Kind, Uri? Url, string? Command, IReadOnlyList<string> Arguments);

/// <summary>
/// Attestazione dell'operatore su uno strumento. <paramref name="Source"/> e' l'identificatore di una sorgente
/// configurata oppure l'indirizzo di una sorgente di un'applicazione. Solo l'operatore puo' dichiarare una
/// collocazione diversa da quella osservata.
/// </summary>
public sealed record ToolAttestationConfig(
    string Source,
    string Tool,
    string Fingerprint,
    DataClass Accepts,
    DataClass Returns,
    IReadOnlySet<ProcessingRegion> Regions,
    Placement? Placement,
    string By,
    DateTimeOffset ExpiresAt);

/// <summary>Strumento revocato dall'operatore.</summary>
public sealed record ToolRevocation(string Source, string Tool);
