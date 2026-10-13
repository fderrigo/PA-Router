using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Execution;
using PA.Router.Policy;
using PA.Router.Privacy;

namespace PA.Router.Core;

/// <summary>
/// Dati di dispatch di un target, presi dal registro. Non sono autorizzazioni: servono solo a
/// sapere che cosa lanciare una volta che la policy ha ammesso il target.
/// </summary>
/// <param name="Model">Modello esatto dichiarato.</param>
/// <param name="Endpoint">Endpoint locale, per gli adapter HTTP.</param>
/// <param name="Executable">Programma ufficiale, per gli adapter a processo.</param>
/// <param name="MaxConcurrent">Esecuzioni simultanee ammesse sul target.</param>
/// <param name="MaxOutputTokens">
/// Tetto di uscita dichiarato dal registro, in token. Serve agli adapter che devono dichiararlo
/// nella richiesta invece di scoprirlo alla risposta; zero significa non dichiarato.
/// </param>
public sealed record TargetDispatchInfo(
    string? Model,
    Uri? Endpoint,
    string? Executable,
    int MaxConcurrent,
    string? SecretRef = null,
    IReadOnlyList<string>? Arguments = null,
    int MaxOutputTokens = 0)
{
    /// <summary>Cache del prefisso chiesta dall'operatore per questo servizio (ADR-022). Non e' un'autorizzazione.</summary>
    public bool PromptCaching { get; init; }

    /// <summary>Se il modello deve ragionare prima di rispondere; null lascia la scelta al modello.</summary>
    public bool? Thinking { get; init; }

    /// <summary>Minuti di permanenza in memoria scelti dall'operatore (-1 sempre); null lascia decidere al fornitore.</summary>
    public int? KeepAliveMinutes { get; init; }

    /// <summary>Prezzo del registro, per il costo calcolato del turno; null se non dichiarato.</summary>
    public ServicePricing? Pricing { get; init; }

    /// <summary>L'operatore ha scelto la sessione nativa per questo agente (ADR-022). Non e' un'autorizzazione.</summary>
    public bool NativeSession { get; init; }
}

/// <summary>
/// Tutto cio' che serve a eseguire una richiesta, gia' risolto dal chiamante: sessione, policy
/// compilata, candidati proiettati sul probe e dati di dispatch.
///
/// Il coordinatore non legge configurazione ne' registro: riceve uno snapshot e lavora su quello
/// per tutto il turno, cosi' una modifica a meta' esecuzione non cambia le regole sotto una
/// richiesta gia' avviata (03-specs/CONFIG.md).
/// </summary>
/// <param name="SessionId">Sessione proprietaria.</param>
/// <param name="Input">Testo della richiesta.</param>
/// <param name="Context">Elementi di contesto autorizzati per quella sessione.</param>
/// <param name="Policy">Policy gia' compilata per il profilo della sessione.</param>
/// <param name="Candidates">Candidati con capacita' e controlli effettivi.</param>
/// <param name="Dispatch">Dati di lancio per ciascun candidato.</param>
/// <param name="RequiredCapabilities">Capacita' necessarie alla richiesta.</param>
/// <param name="Intent">Intento del comando.</param>
/// <param name="PreferSubscription">Preferenza di ranking dell'operatore.</param>
/// <param name="Limits">Limiti privacy del profilo.</param>
/// <param name="PolicyDigest">Digest della policy applicata.</param>
/// <param name="RegistryDigest">Digest del registro applicato.</param>
/// <param name="SnapshotDigest">Digest complessivo dello snapshot.</param>
/// <param name="DeclaredClass">Classe dichiarata dal chiamante; puo' solo alzare.</param>
/// <param name="RequestedTargetId">Target richiesto esplicitamente.</param>
/// <param name="WorkspaceKey">Chiave del workspace da bloccare, per gli agenti.</param>
/// <param name="Workspace">Workspace autorizzato da consegnare all'adapter.</param>
/// <param name="RequestDigest">
/// Digest della richiesta intera a cui la chiave di idempotenza e' legata (S74). Null per i
/// chiamanti senza idempotenza.
/// </param>
/// <param name="IdempotencyKey">Chiave di idempotenza dell'host API.</param>
/// <param name="Band">
/// Requisiti della banda di difficolta' stimata, gia' composti con quelli del profilo. Null
/// quando il chiamante non stima una banda: il gate corrispondente non si applica.
/// </param>
/// <param name="Difficulty">
/// Banda di difficolta' assegnata alla richiesta, in forma wire. Le soglie viaggiano gia' in
/// <c>Band</c>, ma senza il **nome** un rifiuto mostra tre soglie e nessuna causa: la difficolta' e'
/// la cosa che l'operatore puo' cambiare, ed e' la prima da dire.
/// </param>
/// <param name="EstimatedTokens">Token stimati della richiesta, per stimare il costo di ciascun candidato.</param>
/// <param name="DifficultyReasons">
/// Da che cosa viene la difficolta', come coppie <c>chiave=valore</c> senza contenuti: token stimati, banda per
/// dimensione, minimo delle regole, modifica di file. Il motore la passa a chi guarda il turno cosi' com'e'.
/// </param>
/// <param name="Instructions">Istruzioni dell'applicazione (ADR-020), null se la richiesta non ne porta.</param>
/// <param name="Tools">Strumenti preparati per il turno con i limiti effettivi, null se nessuno.</param>
public sealed record ExecutionRequestContext(
    string SessionId,
    string Input,
    IReadOnlyList<ContextItem> Context,
    CompiledPolicy Policy,
    IReadOnlyList<PolicyCandidate> Candidates,
    IReadOnlyDictionary<string, TargetDispatchInfo> Dispatch,
    IReadOnlySet<Capability> RequiredCapabilities,
    Intent Intent,
    bool PreferSubscription,
    PrivacyLimits Limits,
    string PolicyDigest,
    string RegistryDigest,
    string SnapshotDigest,
    DataClass? DeclaredClass = null,
    SchedulerLease? Admission = null,
    string? RequestedTargetId = null,
    string? WorkspaceKey = null,
    WorkspaceDescriptor? Workspace = null,
    string? IdempotencyKey = null,
    string? RequestDigest = null,
    string? PreassignedExecutionId = null,
    BandRequirements? Band = null,
    string? Difficulty = null,
    int EstimatedTokens = 0,
    string? Instructions = null,
    ToolSession? Tools = null,
    ITurnObserver? Observer = null,
    string? DifficultyReasons = null)
{
    /// <summary>
    /// Che cosa dice la cronologia dell'ultimo turno (ADR-022). Null quando il router non conserva la cronologia:
    /// senza di lei non si sa chi abbia fatto il turno prima, e un agente non puo' proseguire nulla.
    /// </summary>
    public NativeHistory? NativeHistory { get; init; }

    /// <summary>
    /// Il profilo di domanda del turno, in ombra (ADR-025): con <see cref="CapabilityLevels"/> fa calcolare la scelta del
    /// minimo sufficiente accanto a quella che decide. Null quando l'analizzatore e' spento.
    /// </summary>
    public DemandProfile? Demand { get; init; }

    /// <summary>
    /// Vero se il profilo di domanda **decide** (ADR-031): fra gli ammessi sceglie il minimo sufficiente, e se nessuno basta
    /// il piu' capace, dichiarandolo. Falso in ombra: sceglie <c>rank-v3</c> e il minimo sufficiente si racconta soltanto.
    /// </summary>
    public bool DemandDecides { get; init; }

    /// <summary>
    /// Vero se l'operatore ha confermato nel terminale, per questo turno, l'uscita dal perimetro che il profilo sottopone a
    /// conferma (ADR-029, decisione I). Il canale di servizio non la imposta mai: li' si fallisce chiusi.
    /// </summary>
    public bool ExternalConfirmed { get; init; }


    /// <summary>Livelli di capacita' dei servizi che rispondono, per identificatore (ADR-027).</summary>
    public IReadOnlyDictionary<string, PA.Router.Routing.Demand.CapabilityLevels>? CapabilityLevels { get; init; }
}

/// <summary>
/// Esito di una esecuzione, gia' filtrato.
/// </summary>
/// <param name="ExecutionId">Esecuzione eseguita; null se la richiesta non e' stata accettata.</param>
/// <param name="State">Stato terminale.</param>
/// <param name="Output">Testo restituibile, presente solo dopo lo screening.</param>
/// <param name="ErrorCode">Codice errore, quando presente.</param>
/// <param name="SideEffects">Effetti collaterali attribuiti.</param>
/// <param name="Decisions">Decisione per ogni candidato valutato.</param>
/// <param name="Usage">Consumo osservato, quando dichiarato.</param>
/// <param name="AttemptsUsed">Tentativi effettivamente spesi.</param>
public sealed record ExecutionResult(
    string? ExecutionId,
    ExecutionState State,
    string? Output,
    ErrorCode? ErrorCode,
    SideEffectState SideEffects,
    IReadOnlyList<PolicyDecision> Decisions,
    UsageReport? Usage,
    int AttemptsUsed)
{
    /// <summary>
    /// Riscontro senza contenuti su un rifiuto: quale strumento, sorgente, classe o limite (ADR-020). Null
    /// quando il codice basta.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Detail { get; init; }

    /// <summary>Servizio che ha risposto. Serve alla cronologia per sapere chi ha fatto quale turno (ADR-022).</summary>
    public string? TargetId { get; init; }

    /// <summary>
    /// Perche' nessun servizio ha preso la richiesta, con i numeri che lo hanno deciso (S116). Null
    /// quando il turno non e' stato negato dalla policy: le decisioni da sole dicono **che** cosa e'
    /// stato escluso, questo dice **contro quale valore**.
    /// </summary>
    public DenialDetail? Denial { get; init; }

    /// <summary>Sessione nativa dell'agente usata da questo turno, quando ce n'era una.</summary>
    public string? NativeSessionId { get; init; }

    /// <summary>
    /// Vero solo se la richiesta offriva strumenti, la risposta e' completata e nessuno strumento e' stato eseguito
    /// (S47). Un modello piccolo puo' rispondere senza usarli, anche scrivendo la chiamata come testo: l'applicazione
    /// deve poterlo sapere, perche' la risposta non contiene i dati che gli strumenti avrebbero dato.
    /// </summary>
    public bool ToolsUnused { get; init; }

    /// <summary>
    /// Vero se ha risposto un servizio dentro il perimetro sotto la soglia di qualita' della richiesta, perche' quelli
    /// capaci stanno fuori e il profilo sceglie di dichiararlo invece di rifiutare (ADR-029, regola «perimetro debole»).
    /// </summary>
    public bool WeakPerimeter { get; init; }

    /// <summary>
    /// Vero se ha risposto il piu' capace fra gli ammessi perche' nessuno bastava per il profilo di domanda (ADR-031, modo
    /// decisivo): la risposta c'e', ma sotto il livello che la domanda chiedeva, e ogni front end lo dice.
    /// </summary>
    public bool BelowDemand { get; init; }

    /// <summary>
    /// Byte di risposta arrivati prima di un guasto. Diversi da zero, il servizio ha gia' lavorato sul payload e il turno
    /// non si ripete altrove.
    /// </summary>
    public int OutputBytes { get; init; }

    /// <summary>
    /// Classe della conversazione a fine turno, compresa quella alzata dai risultati degli strumenti. Serve a non
    /// perderla: la cronologia e la memoria la ereditano, invece di riclassificare una parafrasi che non ha piu' forma.
    /// </summary>
    public DataClass? ConversationClass { get; init; }

    /// <summary>Vero se esiste un output restituibile.</summary>
    public bool HasOutput => Output is not null;

    /// <summary>Resa compatta: nessun output nel log.</summary>
    public override string ToString() =>
        "ExecutionResult { Execution = " + (ExecutionId ?? "none")
        + ", State = " + State.ToWire()
        + ", Error = " + (ErrorCode?.ToWire() ?? "none") + " }";
}

