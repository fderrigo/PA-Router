namespace PA.Router.Abstractions.Contracts;

/// <summary>Stati di una esecuzione (02-architecture/DOMAIN-MODEL.md).</summary>
public enum ExecutionState
{
    [WireValue("accepted")]
    Accepted,

    [WireValue("analyzing")]
    Analyzing,

    [WireValue("evaluating")]
    Evaluating,

    [WireValue("routing")]
    Routing,

    [WireValue("running")]
    Running,

    /// <summary>
    /// Il modello ha chiesto strumenti e il router li sta eseguendo (ADR-020). Un evento con questo stato non dice
    /// quali strumenti ne' con che cosa: solo la fase del turno.
    /// </summary>
    [WireValue("tool_calling")]
    ToolCalling,

    [WireValue("screening")]
    Screening,

    [WireValue("completed")]
    Completed,

    [WireValue("denied")]
    Denied,

    [WireValue("failed")]
    Failed,

    [WireValue("cancelled")]
    Cancelled,

    [WireValue("uncertain")]
    Uncertain,
}

/// <summary>Stati di un tentativo.</summary>
public enum AttemptState
{
    [WireValue("prepared")]
    Prepared,

    [WireValue("dispatching")]
    Dispatching,

    [WireValue("running")]
    Running,

    [WireValue("completed")]
    Completed,

    [WireValue("failed")]
    Failed,

    [WireValue("cancelled")]
    Cancelled,

    [WireValue("uncertain")]
    Uncertain,
}

/// <summary>
/// Conoscenza sugli effetti collaterali di un tentativo.
///
/// Un agente passa a <c>Possible</c> al dispatch, non quando dichiara il primo file modificato:
/// il momento in cui l'effetto puo' essere avvenuto e' quello in cui il processo e' partito.
/// </summary>
public enum SideEffectState
{
    [WireValue("none")]
    None,

    [WireValue("possible")]
    Possible,

    [WireValue("observed")]
    Observed,

    [WireValue("unknown")]
    Unknown,
}

/// <summary>Limiti effettivi di una esecuzione, gia' composti dal profilo.</summary>
/// <param name="TimeoutSeconds">Deadline globale, condivisa da tutti i tentativi.</param>
/// <param name="MaxOutputBytes">Tetto del buffer di output.</param>
/// <param name="MaxAttempts">Tentativi totali ammessi.</param>
public sealed record ExecutionLimits(int TimeoutSeconds, int MaxOutputBytes, int MaxAttempts);

/// <summary>
/// Workspace concesso a un adapter agentico. Un worktree separa le modifiche ma non e' un
/// confine di sicurezza del sistema operativo: qui si dichiara cosa e' stato autorizzato, non
/// cosa e' tecnicamente impedito (02-architecture/EXECUTION-PIPELINE.md).
/// </summary>
/// <param name="RootPath">Radice canonica autorizzata.</param>
/// <param name="Writable">Vero se la scrittura e' stata autorizzata dal profilo.</param>
/// <param name="Shell">
/// Vero se il turno ha ricevuto i comandi di sistema. Il recinto e' cartella, scrittura e comandi (ADR-019): senza
/// questo campo la concessione restava nel piano del turno e non raggiungeva l'adapter, che eseguiva comunque senza.
/// </param>
public sealed record WorkspaceDescriptor(string RootPath, bool Writable, bool Shell = false);

/// <summary>Consumo dichiarato dal provider. Null significa non misurato; zero significa misurato zero.</summary>
/// <param name="InputTokens">Token di ingresso, se osservati.</param>
/// <param name="OutputTokens">Token di uscita, se osservati.</param>
/// <param name="BilledCost">Costo fatturato, se disponibile.</param>
public sealed record UsageReport(long? InputTokens, long? OutputTokens, decimal? BilledCost)
{
    /// <summary>
    /// Tempo che il servizio ha speso a caricare il modello prima di generare, se lo dichiara (Ollama lo fa). Dice
    /// perche' una risposta breve e' arrivata tardi: non e' la generazione, e' il modello che entrava in memoria.
    /// </summary>
    public long? LoadMilliseconds { get; init; }

    /// <summary>
    /// Caratteri di ragionamento che il modello ha scritto senza mostrarli, se il servizio li separa (Ollama li manda nel
    /// campo <c>thinking</c>). Dice perche' una risposta di una riga e' costata minuti su una macchina senza GPU: qwen3:4b ha
    /// scritto 1381 token per rispondere «Ciao!» (23 settembre 2026). Mai il testo, solo quanto.
    /// </summary>
    public long? ReasoningCharacters { get; init; }

    /// <summary>
    /// Costo calcolato dal router: token dichiarati per il prezzo del registro (23 settembre 2026). Non e' una fattura —
    /// quella e' <see cref="BilledCost"/>, quando il programma la dichiara — ma e' cio' che serve per sapere quanto si spende:
    /// prima `/cost` diceva «nessun servizio lo ha dichiarato» anche dopo turni pagati su un'API con il prezzo nel registro.
    /// </summary>
    public decimal? PricedCost { get; init; }

    /// <summary>Valuta di <see cref="PricedCost"/>, quella del registro.</summary>
    public string? Currency { get; init; }

    /// <summary>
    /// Vero se il servizio ha smesso di scrivere perche' ha raggiunto il tetto di uscita, non perche' aveva finito (23
    /// settembre 2026): la risposta si interrompeva a meta' frase e il router la dichiarava completata.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>Il consumo con il costo calcolato dal prezzo, se ci sono token e un prezzo.</summary>
    public UsageReport PricedWith(ServicePricing? pricing)
    {
        decimal? cost = null;
        if (pricing is not null && InputTokens is >= 0 && OutputTokens is >= 0
            && double.IsFinite(pricing.InputPerMillionTokens) && pricing.InputPerMillionTokens >= 0
            && double.IsFinite(pricing.OutputPerMillionTokens) && pricing.OutputPerMillionTokens >= 0)
        {
            try
            {
                cost = ((InputTokens.Value * (decimal)pricing.InputPerMillionTokens)
                    + (OutputTokens.Value * (decimal)pricing.OutputPerMillionTokens)) / 1_000_000m;
            }
            catch (OverflowException)
            {
                // Unrepresentable prices remain unknown; diagnostics cannot fail an already executed request.
            }
        }

        return this with { PricedCost = cost, Currency = cost is null ? null : pricing!.Currency };
    }
}

/// <summary>
/// Evento normalizzato prodotto da un adapter. La sequenza non e' qui: la assegna il
/// coordinatore, cosi' due adapter non possono negoziarla o riusarla.
/// </summary>
public abstract record AdapterEvent;

/// <summary>Il target ha accettato la richiesta e ha iniziato a lavorare.</summary>
public sealed record AdapterStarted : AdapterEvent;

/// <summary>Avanzamento con codice allowlisted; nessun testo libero del provider.</summary>
/// <param name="Code">Codice di avanzamento previsto dal contratto.</param>
public sealed record AdapterProgress(string Code) : AdapterEvent;

/// <summary>
/// Frammento di output. Non raggiunge mai il chiamante: entra nel buffer interno e viene
/// esaminato solo a risposta completa (03-specs/PRIVACY.md).
/// </summary>
/// <param name="Text">Frammento di testo.</param>
public sealed record AdapterOutputChunk(string Text) : AdapterEvent
{
    /// <summary>Nessun contenuto nella resa testuale.</summary>
    public override string ToString() =>
        "AdapterOutputChunk { Length = " + Text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " }";
}

/// <summary>Consumo osservato.</summary>
/// <param name="Usage">Valori dichiarati dal provider.</param>
public sealed record AdapterUsage(UsageReport Usage) : AdapterEvent;

/// <summary>Modifica di artefatto osservata; solo per adapter agentici e sempre da verificare.</summary>
/// <param name="RelativePath">Percorso relativo dichiarato dal provider.</param>
public sealed record AdapterArtifactChanged(string RelativePath) : AdapterEvent;

/// <summary>Terminale riuscito.</summary>
/// <param name="ExitCode">Codice di uscita del programma o dello scambio.</param>
public sealed record AdapterCompleted(int ExitCode) : AdapterEvent;

/// <summary>Terminale fallito, gia' tradotto in un codice stabile.</summary>
/// <param name="Code">Codice errore.</param>
/// <param name="SafeMessage">Messaggio privo di contenuto e di segreti.</param>
public sealed record AdapterFailed(ErrorCode Code, string SafeMessage) : AdapterEvent
{
    /// <summary>
    /// Stato HTTP della risposta del fornitore, quando c'e' (23 settembre 2026): un numero, nessun contenuto. A fine credito
    /// il turno diceva solo «non disponibile», e senza lo stato non si poteva sapere che cosa avesse risposto il servizio.
    /// </summary>
    public int? HttpStatus { get; init; }

    /// <summary>
    /// Vero se la quota esaurita non torna da sola fra un minuto (24 settembre 2026): credito finito o limite di spesa
    /// dell'operatore, non un limite di frequenza. Il router mette in pausa il servizio invece di riprovarlo a ogni turno.
    /// </summary>
    public bool QuotaLasting { get; init; }

    /// <summary>Quando il fornitore dice che il servizio torna disponibile; null se non lo dice.</summary>
    public DateTimeOffset? QuotaUntil { get; init; }
}

/// <summary>
/// Esecuzione autorizzata consegnata a un adapter.
///
/// Il costruttore e' interno alle astrazioni e visibile al solo modulo Execution: un adapter, un
/// host o un plugin non possono costruirne una. Contiene il payload gia' analizzato e il grant,
/// mai la richiesta originale o la configurazione del repository (03-specs/ADAPTERS.md).
/// </summary>
public sealed record AuthorizedExecution
{
    internal AuthorizedExecution(
        string executionId,
        string attemptId,
        ExecutionGrant grant,
        SafePayload payload,
        string targetId,
        AdapterKind adapter,
        TargetKind kind,
        string? model,
        Uri? endpoint,
        Placement inferencePlacement,
        string? secretRef,
        string? executable,
        ExecutionLimits limits,
        WorkspaceDescriptor? workspace,
        IReadOnlyList<string> extraArguments,
        int maxOutputTokens,
        IReadOnlyList<ToolRound> rounds)
    {
        Rounds = rounds;
        MaxOutputTokens = maxOutputTokens;
        ExecutionId = executionId;
        AttemptId = attemptId;
        Grant = grant;
        Payload = payload;
        TargetId = targetId;
        Adapter = adapter;
        Kind = kind;
        Model = model;
        Endpoint = endpoint;
        InferencePlacement = inferencePlacement;
        SecretRef = secretRef;
        Executable = executable;
        Limits = limits;
        Workspace = workspace;
        ExtraArguments = extraArguments;
    }

    public string ExecutionId { get; }

    public string AttemptId { get; }

    /// <summary>Grant monouso che autorizza questo singolo dispatch.</summary>
    public ExecutionGrant Grant { get; }

    /// <summary>Payload gia' analizzato e minimizzato.</summary>
    public SafePayload Payload { get; }

    public string TargetId { get; }

    public AdapterKind Adapter { get; }

    public TargetKind Kind { get; }

    /// <summary>Modello esatto, quando il target ne dichiara uno.</summary>
    public string? Model { get; }

    /// <summary>Endpoint locale, per gli adapter HTTP.</summary>
    public Uri? Endpoint { get; }

    /// <summary>
    /// Collocazione dell'inferenza ammessa dalla policy. L'adapter la usa per scegliere la regola
    /// di rete: dove l'inferenza promette una macchina precisa, un nome non basta (ADR-016).
    /// </summary>
    public Placement InferencePlacement { get; }

    /// <summary>
    /// Nome del segreto nel vault locale, mai il valore: i nomi possono stare in un DTO che
    /// attraversa coordinatore e audit, i valori no.
    /// </summary>
    public string? SecretRef { get; }

    /// <summary>Programma da lanciare, per gli adapter a processo.</summary>
    public string? Executable { get; }

    public ExecutionLimits Limits { get; }

    /// <summary>
    /// Tetto di uscita in token dichiarato dal registro per questo servizio; zero significa non
    /// dichiarato.
    ///
    /// Non sostituisce <see cref="ExecutionLimits.MaxOutputBytes"/>, che resta il limite della
    /// policy e si applica a cio' che torna. Serve agli adapter delle API che pretendono il tetto
    /// **nella richiesta**: senza questo numero l'unica alternativa sarebbe dedurlo dai byte con
    /// un rapporto inventato.
    /// </summary>
    public int MaxOutputTokens { get; }

    /// <summary>
    /// Finestra di contesto del servizio in token, quella con cui la policy lo ha ammesso; zero se non dichiarata.
    ///
    /// Serve agli adapter dei modelli locali che la fissano **nella richiesta**: Ollama altrimenti usa la propria
    /// finestra predefinita, spesso piu' piccola, e taglia l'inizio della conversazione in silenzio mentre il router
    /// crede di averla mandata tutta (S117).
    /// </summary>
    public int ContextWindowTokens { get; init; }

    /// <summary>
    /// L'operatore ha chiesto per questo servizio la cache del prefisso presso il fornitore (ADR-022). L'adapter che
    /// non la sa esprimere la ignora. Non allarga nulla: riguarda come il fornitore fa pagare cio' che riceve comunque.
    /// </summary>
    public bool PromptCaching { get; init; }

    /// <summary>Se il modello deve ragionare prima di rispondere; null lascia la scelta al modello.</summary>
    public bool? Thinking { get; init; }

    /// <summary>Minuti di permanenza in memoria scelti dall'operatore (-1 sempre); null lascia decidere al fornitore.</summary>
    public int? KeepAliveMinutes { get; init; }

    /// <summary>
    /// Sessione propria dell'agente da aprire o da proseguire (ADR-022), quando l'operatore l'ha scelta per questo
    /// servizio e il router conserva la cronologia. Null: l'agente non deve conservare nulla.
    /// </summary>
    public NativeSession? NativeSession { get; init; }

    /// <summary>Workspace autorizzato, solo per gli adapter agentici.</summary>
    public WorkspaceDescriptor? Workspace { get; }

    /// <summary>
    /// Argomenti dichiarati dal registro per questo servizio. **Si sommano** a quelli certificati
    /// dall'adapter e non li sostituiscono: sono il modo in cui un operatore rende locale un
    /// agente (per esempio <c>--oss</c>), non un modo per togliergli un controllo. Gli argomenti
    /// vietati sono rifiutati alla lettura della configurazione e di nuovo qui dall'adapter
    /// (03-specs/NESTED-AGENTS.md).
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; }

    /// <summary>
    /// Giri modello-strumento gia' avvenuti nello stesso turno, con i risultati gia' esaminati (ADR-020).
    /// Vuoto al primo giro. L'adapter li rende nella forma del fornitore; il digest del payload li comprende.
    /// </summary>
    public IReadOnlyList<ToolRound> Rounds { get; }

    /// <summary>Resa compatta: nessun payload e nessun grant nel log.</summary>
    public override string ToString() =>
        "AuthorizedExecution { Execution = " + ExecutionId + ", Attempt = " + AttemptId
        + ", Target = " + TargetId + " }";
}

/// <summary>
/// Adapter di esecuzione. Riceve solo un'esecuzione autorizzata e restituisce eventi
/// normalizzati; non conosce la richiesta originale, la policy o la configurazione.
/// </summary>
public interface IExecutionAdapter
{
    /// <summary>Identificatore dell'adapter.</summary>
    AdapterKind AdapterId { get; }

    /// <summary>Vero se l'adapter espone strumenti di memoria nativi.</summary>
    bool SupportsMemoryTools => false;

    /// <summary>Vero se l'adapter espone skill native.</summary>
    bool SupportsNativeSkills => false;

    /// <summary>
    /// Esegue e produce eventi. L'ultimo evento logico deve essere esattamente uno fra
    /// <see cref="AdapterCompleted"/> e <see cref="AdapterFailed"/>.
    /// </summary>
    IAsyncEnumerable<AdapterEvent> ExecuteAsync(AuthorizedExecution execution, CancellationToken cancellationToken);
}

/// <summary>
/// La sessione che un agente tiene per conto proprio, con l'identificatore che **il router** le ha assegnato.
/// </summary>
/// <param name="Id">UUID assegnato dal router al primo turno. Mai scelto dall'operatore, mai «l'ultima».</param>
/// <param name="Resume">Vero per proseguire quella sessione, falso per aprirla con questo identificatore.</param>
public sealed record NativeSession(string Id, bool Resume);

/// <summary>
/// Che cosa la cronologia dice dell'ultimo turno, per decidere se un agente puo' proseguire (ADR-022): si prosegue
/// solo se l'ultimo turno lo ha fatto **lo stesso servizio** e aveva una sessione nativa; altrimenti si riparte con
/// il contesto impacchettato, che e' la regola di S15.
/// </summary>
/// <param name="LastServiceId">Servizio che ha eseguito l'ultimo turno conservato; null se non ce ne sono.</param>
/// <param name="LastNativeSessionId">Sessione nativa di quel turno, quando ne aveva una.</param>
public sealed record NativeHistory(string? LastServiceId, string? LastNativeSessionId);
