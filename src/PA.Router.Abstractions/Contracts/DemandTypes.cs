namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Il tipo di risposta che una richiesta chiede (ADR-025): descrive il **contenuto**, non concede nulla. Permessi,
/// recinto e conferme restano del classificatore dell'azione (ADR-019). L'elenco e' chiuso e versionato: un campo
/// libero produrrebbe etichette diverse per la stessa cosa.
/// </summary>
public enum TaskType
{
    [WireValue("coding")]
    Coding,

    [WireValue("writing")]
    Writing,

    [WireValue("analysis")]
    Analysis,

    [WireValue("math")]
    Math,

    [WireValue("extraction")]
    Extraction,

    [WireValue("translation")]
    Translation,

    [WireValue("conversation")]
    Conversation,

    [WireValue("planning")]
    Planning,

    [WireValue("qa")]
    Qa,

    [WireValue("creative")]
    Creative,
}

/// <summary>Esito del controllo deterministico che precede l'analizzatore (ADR-025).</summary>
public enum PreCheckOutcome
{
    /// <summary>La richiesta sta nell'elenco minimo dei casi banali.</summary>
    [WireValue("trivial")]
    Trivial,

    /// <summary>Le misure certe impongono gia' un livello minimo.</summary>
    [WireValue("forced")]
    ForcedMinimumTier,

    /// <summary>Tutto il resto: decide l'analizzatore.</summary>
    [WireValue("unknown")]
    Unknown,
}

/// <summary>Modo dell'analizzatore della richiesta (ADR-026, ADR-031).</summary>
public enum AnalyzerMode
{
    /// <summary>Spento: nessun profilo di domanda, nessun costo.</summary>
    [WireValue("off")]
    Off,

    /// <summary>In ombra: il profilo si calcola, si registra e si racconta, e non decide nulla.</summary>
    [WireValue("shadow")]
    Shadow,

    /// <summary>
    /// Decisivo (ADR-031): il profilo di domanda fissa il livello richiesto e sceglie il minimo sufficiente. Vale solo per
    /// un modello e un prompt con un esito del benchmark consegnato; altrimenti il turno gira in ombra.
    /// </summary>
    [WireValue("decisive")]
    Decisive,
}

/// <summary>
/// Che cosa dice l'analizzatore di una richiesta: tipo di risposta e livelli da 1 a 3. Nessun testo libero.
/// </summary>
/// <param name="Task">Tipo di risposta.</param>
/// <param name="Reasoning">Passi di ragionamento necessari.</param>
/// <param name="Knowledge">Profondita' della conoscenza necessaria.</param>
/// <param name="Synthesis">Quanto si deve combinare o produrre.</param>
/// <param name="Context">Quanto la risposta dipende da testo fornito.</param>
/// <param name="Sensitive">Categoria particolare dell'art. 9 riferita a una persona (ADR-028): segnale, non classe.</param>
/// <param name="Confidence">Probabilita' minima dei token dei livelli, se il runtime la dichiara; null se non osservata.</param>
public sealed record AnalyzerSignals(
    TaskType Task,
    int Reasoning,
    int Knowledge,
    int Synthesis,
    int Context,
    int Sensitive,
    double? Confidence);

/// <summary>
/// Il profilo di domanda (ADR-025): quanto deve essere capace il modello che risponde, per dimensione. Lo produce una
/// trasformazione deterministica e versionata dai segnali del pre-check e dell'analizzatore.
/// </summary>
/// <param name="Version">Versione unica di soglie, elenco dei casi banali, prompt e trasformazione.</param>
/// <param name="PreCheck">Esito del pre-check.</param>
/// <param name="Task">Tipo di risposta; null se non determinato (casi banali, analizzatore assente).</param>
/// <param name="BaseTier">Livello complessivo, 1-3: interfaccia, log e ripiego.</param>
/// <param name="Reasoning">Livello di ragionamento richiesto.</param>
/// <param name="Coding">Livello di codice richiesto.</param>
/// <param name="Context">Livello di dipendenza dal contesto.</param>
/// <param name="Source">Chi ha completato il profilo: <c>trivial</c>, <c>forced</c>, <c>analyzer</c> o <c>conservative</c>.</param>
/// <param name="Raised">Vero se la bassa confidenza ha alzato le dimensioni.</param>
/// <param name="Signals">Segnali dell'analizzatore, se ha risposto.</param>
public sealed record DemandProfile(
    string Version,
    PreCheckOutcome PreCheck,
    TaskType? Task,
    int BaseTier,
    int Reasoning,
    int Coding,
    int Context,
    string Source,
    bool Raised,
    AnalyzerSignals? Signals);

/// <summary>Che cosa fa un modello prima di rispondere.</summary>
public enum ThinkingSupport
{
    /// <summary>Non si e' potuto sapere: Ollama non ha risposto o il modello non c'e'.</summary>
    Unknown,

    /// <summary>Risponde subito: non ragiona.</summary>
    None,

    /// <summary>Ragiona, e con <c>think: false</c> smette (qwen3:1.7b).</summary>
    Switchable,

    /// <summary>Ragiona sempre, anche chiedendo di non farlo (qwen3:4b Thinking-2507).</summary>
    Always,
}
