namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Codici di motivazione di una decisione di policy (03-specs/POLICY.md). Sono stabili e vanno
/// in explain e audit: descrivono il controllo che ha negato, non il dato che lo ha fatto
/// scattare.
/// </summary>
public enum PolicyReason
{
    [WireValue("TARGET_DISABLED")]
    TargetDisabled,

    [WireValue("TARGET_NOT_ALLOWED")]
    TargetNotAllowed,

    [WireValue("CHANNEL_MISMATCH")]
    ChannelMismatch,

    [WireValue("AUTH_UNAVAILABLE")]
    AuthUnavailable,

    [WireValue("CAPABILITY_MISSING")]
    CapabilityMissing,

    [WireValue("CAPABILITY_FORBIDDEN")]
    CapabilityForbidden,

    [WireValue("EXTERNAL_FORBIDDEN")]
    ExternalForbidden,

    [WireValue("DATA_CLASS_FORBIDDEN")]
    DataClassForbidden,

    [WireValue("CONTROL_UNAVAILABLE")]
    ControlUnavailable,

    [WireValue("PROBE_STALE")]
    ProbeStale,

    [WireValue("QUOTA_EXHAUSTED")]
    QuotaExhausted,

    [WireValue("BUDGET_EXCEEDED")]
    BudgetExceeded,

    [WireValue("POLICY_INVALID")]
    PolicyInvalid,

    [WireValue("EXTENSION_CHANGED")]
    ExtensionChanged,

    /// <summary>La finestra di contesto non basta per il payload e la risposta: si nega, non si tronca.</summary>
    [WireValue("CONTEXT_WINDOW_INSUFFICIENT")]
    ContextWindowInsufficient,

    [WireValue("ZONE_NOT_ALLOWED")]
    ZoneNotAllowed,

    [WireValue("EXTERNAL_CONFIRMATION_REQUIRED")]
    ExternalConfirmationRequired,

    /// <summary>
    /// Quanto puo' costare al massimo la richiesta su questo servizio supera la spesa massima per richiesta
    /// (<c>limits.maxCostPerRequest</c>, ADR-032 fase 5), oppure il servizio e' di terzi senza prezzo e con un tetto non si
    /// puo' dimostrare che ci stia sotto.
    /// </summary>
    [WireValue("COST_LIMIT")]
    CostLimit,

    // QUALITY_INSUFFICIENT e' stato tolto il 24 settembre 2026 (ADR-032, fase 3): senza qualita' minima non puo' scattare.
    // AUTH_NOT_ALLOWED e REGION_UNVERIFIED sono stati tolti il 25 settembre 2026 (ADR-032, fase 4): autenticazioni e regioni
    // richieste dal profilo non esistono piu', dove vanno i dati lo decide la zona.
}

/// <summary>Intento della richiesta, scelto dal comando o dalle capability richieste.</summary>
public enum Intent
{
    [WireValue("chat")]
    Chat,

    [WireValue("code_analysis")]
    CodeAnalysis,

    [WireValue("code_change")]
    CodeChange,
}

/// <summary>
/// Obbligo che il profilo impone prima del dispatch. Un obbligo non e' una preferenza: finche'
/// non risulta soddisfatto non esiste alcun target ammissibile.
/// </summary>
public enum Obligation
{
    [WireValue("redact_contacts")]
    RedactContacts,
}

/// <summary>
/// Scopo per cui un grant e' stato emesso. Un grant di memoria non e' riutilizzabile per una
/// esecuzione foreground e viceversa (03-specs/POLICY.md, gate delle elaborazioni ausiliarie).
/// </summary>
public enum GrantPurpose
{
    [WireValue("foreground")]
    Foreground,

    [WireValue("memory_embed")]
    MemoryEmbed,
}

/// <summary>
/// Esito della valutazione di un singolo candidato. Le ragioni sono tutte quelle raccolte, non
/// la prima: chi legge l'explain deve sapere quanti controlli avrebbe dovuto sistemare, non solo
/// il primo che ha ceduto.
/// </summary>
/// <param name="TargetId">Candidato valutato.</param>
/// <param name="Allowed">Vero se nessun controllo ha negato.</param>
/// <param name="Reasons">Codici di negazione, ordinati per codice.</param>
/// <param name="Obligations">Obblighi soddisfatti che rendono ammissibile il candidato.</param>
/// <param name="SnapshotDigest">Digest della configurazione su cui la decisione e' stata presa.</param>
public sealed record PolicyDecision(
    string TargetId,
    bool Allowed,
    IReadOnlyList<PolicyReason> Reasons,
    IReadOnlySet<Obligation> Obligations,
    string SnapshotDigest)
{
    /// <summary>Resa compatta per explain e log: solo identificatore e codici.</summary>
    public override string ToString() =>
        TargetId + " " + (Allowed ? "allow" : "deny")
        + (Reasons.Count == 0 ? string.Empty : " [" + string.Join(',', Reasons.Select(reason => reason.ToWire())) + "]");
}

/// <summary>
/// Target ammissibile: descrittore piu' obblighi soddisfatti piu' il digest del payload
/// candidato.
///
/// Il costruttore e' interno ad <c>PA.Router.Abstractions</c> e visibile solo a
/// <c>PA.Router.Policy</c>: nessun altro modulo puo' fabbricare un'ammissibilita'. E' il modo in
/// cui il tipo stesso impedisce a un ranking, a un adapter o a un host di inventare un target
/// autorizzato (INV-02).
/// </summary>
/// <param name="TargetId">Target ammesso.</param>
/// <param name="Kind">Tipo di target.</param>
/// <param name="Adapter">Adapter che lo raggiunge.</param>
/// <param name="AuthMode">Autenticazione dichiarata.</param>
/// <param name="Boundary">Confine di elaborazione.</param>
/// <param name="Capabilities">Capacita' effettive del target.</param>
/// <param name="Controls">Controlli effettivi osservati.</param>
/// <param name="Obligations">Obblighi soddisfatti.</param>
/// <param name="PayloadDigest">Digest del payload autorizzato.</param>
/// <param name="EffectiveClass">Classe effettiva del payload.</param>
/// <param name="TimeoutSeconds">Deadline effettiva.</param>
/// <param name="MaxOutputBytes">Output massimo effettivo.</param>
/// <param name="SnapshotDigest">Digest della configurazione usata.</param>
public sealed record AdmissibleTarget
{
    internal AdmissibleTarget(
        string targetId,
        ServiceCapability capability,
        AdapterKind adapter,
        AuthMode authMode,
        Placement inferencePlacement,
        IReadOnlySet<Capability> capabilities,
        IReadOnlySet<Control> controls,
        IReadOnlySet<Obligation> obligations,
        string payloadDigest,
        DataClass effectiveClass,
        int timeoutSeconds,
        int maxOutputBytes,
        string snapshotDigest,
        TargetCalibration calibration)
    {
        Calibration = calibration;
        TargetId = targetId;
        Capability = capability;
        Adapter = adapter;
        AuthMode = authMode;
        InferencePlacement = inferencePlacement;
        Capabilities = capabilities;
        Controls = controls;
        Obligations = obligations;
        PayloadDigest = payloadDigest;
        EffectiveClass = effectiveClass;
        TimeoutSeconds = timeoutSeconds;
        MaxOutputBytes = maxOutputBytes;
        SnapshotDigest = snapshotDigest;
    }

    public string TargetId { get; }

    /// <summary>Capacita' per cui il servizio e' stato ammesso.</summary>
    public ServiceCapability Capability { get; }

    public AdapterKind Adapter { get; }

    public AuthMode AuthMode { get; }

    /// <summary>Collocazione dell'inferenza ammessa dal profilo.</summary>
    public Placement InferencePlacement { get; }

    /// <summary>Tipo derivato dalla capacita'; non e' un campo del registro.</summary>
    public TargetKind Kind => Capability is ServiceCapability.CodeAgent ? TargetKind.Agent : TargetKind.Llm;

    /// <summary>Confine derivato dalla collocazione dell'inferenza.</summary>
    public Boundary Boundary =>
        ServiceDescriptor.LeavesMachine(InferencePlacement) ? Boundary.Cloud : Boundary.Local;

    public IReadOnlySet<Capability> Capabilities { get; }

    public IReadOnlySet<Control> Controls { get; }

    public IReadOnlySet<Obligation> Obligations { get; }

    public string PayloadDigest { get; }

    public DataClass EffectiveClass { get; }

    public int TimeoutSeconds { get; }

    public int MaxOutputBytes { get; }

    public string SnapshotDigest { get; }

    /// <summary>
    /// Calibrazione del target, l'unico ingrediente numerico che il ranking riceve. Viaggia
    /// dentro l'ammissibilita' proprio perche' un punteggio non possa esistere per un target che
    /// la policy non ha ammesso.
    /// </summary>
    public TargetCalibration Calibration { get; }
}
