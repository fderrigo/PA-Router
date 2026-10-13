namespace PA.Router.Abstractions.Contracts;

/// <summary>Canale della sessione (config, policy, sessions.channel).</summary>
public enum Channel
{
    [WireValue("personal_cli")]
    PersonalCli,

    [WireValue("chatbot")]
    Chatbot,
}

/// <summary>
/// Classe del dato. L'ordine di dichiarazione e' crescente per sensibilita' e serve al confronto
/// di soglia; <c>Unknown</c> resta fuori dalla scala: significa deny, non un livello intermedio.
/// </summary>
public enum DataClass
{
    [WireValue("public")]
    Public,

    [WireValue("internal")]
    Internal,

    [WireValue("personal")]
    Personal,

    [WireValue("sensitive")]
    Sensitive,

    [WireValue("secret")]
    Secret,

    [WireValue("unknown")]
    Unknown,
}

/// <summary>
/// Capacita' offerta da un servizio del registro: e' la dimensione su cui il router instrada
/// (ADR-015). L'elenco e' chiuso e versionato con lo schema: una capacita' nuova richiede una
/// versione di contratto, perche' un profilo di policy deve poter ragionare in modo esaustivo su
/// cio' che autorizza.
/// </summary>
public enum ServiceCapability
{
    [WireValue("chat")]
    Chat,

    [WireValue("embedding")]
    Embedding,

    [WireValue("pii-detection")]
    PiiDetection,

    [WireValue("sensitive-category-detection")]
    SensitiveCategoryDetection,

    [WireValue("summarize")]
    Summarize,

    [WireValue("rerank")]
    Rerank,

    [WireValue("code-agent")]
    CodeAgent,

    /// <summary>
    /// Analizza la richiesta per il profilo di domanda (ADR-026): lavora per il router, non risponde a nessuno. Gira solo
    /// sulla macchina dell'utente.
    /// </summary>
    [WireValue("request-analysis")]
    RequestAnalysis,
}

/// <summary>
/// Dove gira un processo, o dove avviene un'inferenza (ADR-016). Sono due domande distinte e il
/// registro le tiene separate: i gate della classe del dato guardano l'inferenza, perche' e' li'
/// che finiscono i token.
/// </summary>
public enum Placement
{
    [WireValue("loopback")]
    Loopback,

    [WireValue("lan")]
    Lan,

    [WireValue("private_remote")]
    PrivateRemote,

    [WireValue("third_party")]
    ThirdParty,
}

/// <summary>
/// Origine di un valore di calibrazione. Un valore <c>declared</c> non diventa <c>measured</c> per
/// il fatto di essere vecchio (03-specs/ROUTING.md).
/// </summary>
public enum CalibrationSource
{
    [WireValue("declared")]
    Declared,

    [WireValue("measured")]
    Measured,
}

/// <summary>
/// Tipo di servizio, **derivato** dalla capacita': non e' un campo del registro. Serve a dispatch,
/// audit e presentazione, che distinguono un programma agentico da un modello servito via HTTP.
/// </summary>
public enum TargetKind
{
    [WireValue("llm")]
    Llm,

    [WireValue("agent")]
    Agent,
}

/// <summary>Adapter previsti dalla beta.</summary>
public enum AdapterKind
{
    [WireValue("ollama")]
    Ollama,

    [WireValue("ollama-embed")]
    OllamaEmbed,

    [WireValue("openai-compatible")]
    OpenAiCompatible,

    // Non e' compatibile OpenAI: percorso, autenticazione, versione obbligatoria e forma della
    // risposta sono diversi. Ha un adapter suo proprio perche' riusare quello compatibile
    // avrebbe dichiarato una compatibilita' inesistente.
    [WireValue("anthropic")]
    Anthropic,

    [WireValue("onnx-local")]
    OnnxLocal,

    [WireValue("deterministic")]
    Deterministic,

    [WireValue("codex-cli")]
    CodexCli,

    [WireValue("claude-code-cli")]
    ClaudeCodeCli,

    [WireValue("opencode-cli")]
    OpenCodeCli,
}

/// <summary>Modalita' di autenticazione dichiarata dal target.</summary>
public enum AuthMode
{
    [WireValue("none")]
    None,

    [WireValue("subscription")]
    Subscription,

    [WireValue("api_key")]
    ApiKey,
}

/// <summary>
/// Confine di elaborazione, **derivato** da <see cref="Placement"/>: loopback e lan restano sulla
/// rete dell'operatore, private_remote e third_party escono dalla macchina. Non e' un campo del
/// registro e non viene usato dai gate, che guardano la collocazione (ADR-016).
/// </summary>
public enum Boundary
{
    [WireValue("local")]
    Local,

    [WireValue("cloud")]
    Cloud,
}

/// <summary>Capacita' dichiarate da un target.</summary>
public enum Capability
{
    [WireValue("chat")]
    Chat,

    [WireValue("code_read")]
    CodeRead,

    [WireValue("file_write")]
    FileWrite,

    [WireValue("shell")]
    Shell,
}

/// <summary>Controlli verificabili su un target.</summary>
public enum Control
{
    [WireValue("closed_payload")]
    ClosedPayload,

    [WireValue("local_inference")]
    LocalInference,

    [WireValue("native_agent")]
    NativeAgent,

    [WireValue("workspace_write")]
    WorkspaceWrite,

    [WireValue("shell_control")]
    ShellControl,

    [WireValue("config_isolated")]
    ConfigIsolated,

    [WireValue("no_nested_tools")]
    NoNestedTools,

    [WireValue("no_persistent_transcript")]
    NoPersistentTranscript,

    [WireValue("no_nested_fallback")]
    NoNestedFallback,

    /// <summary>
    /// Accordo di trattamento (DPA) con il fornitore (ADR-029). Non e' osservabile da questo processo: conta solo
    /// attestato dall'operatore, con scadenza (S30), e fa di un servizio di terzi la zona <c>third_party_dpa</c>.
    /// </summary>
    [WireValue("processing_agreement")]
    ProcessingAgreement,
}

/// <summary>
/// Zona di fiducia di una destinazione (ADR-029, decisione H), dalla piu' vicina alla piu' lontana. Si ricava da
/// collocazione dell'inferenza e controlli effettivi: non si dichiara. L'ordine dei valori e' la distanza.
/// </summary>
public enum TrustZone
{
    /// <summary>Stesso host: <c>inferencePlacement: loopback</c>.</summary>
    [WireValue("same_host")]
    SameHost,

    /// <summary>Rete locale: <c>inferencePlacement: lan</c>.</summary>
    [WireValue("lan")]
    Lan,

    /// <summary>Macchina dell'operatore fuori sede, con regione in UE verificata e non scaduta.</summary>
    [WireValue("private_eu")]
    PrivateEu,

    /// <summary>Servizio di terzi con l'accordo di trattamento attestato e non scaduto.</summary>
    [WireValue("third_party_dpa")]
    ThirdPartyDpa,

    /// <summary>Qualunque altra destinazione remota: API generica, o macchina propria senza regione UE verificata.</summary>
    [WireValue("third_party")]
    ThirdParty,
}

/// <summary>Posizione del profilo sull'elaborazione esterna.</summary>
public enum ExternalProcessing
{
    [WireValue("forbidden")]
    Forbidden,

    [WireValue("allowed")]
    Allowed,
}

/// <summary>Esportatore di telemetria. Il default e' <c>none</c>.</summary>
public enum TelemetryExporter
{
    [WireValue("none")]
    None,

    [WireValue("otlp")]
    Otlp,
}
