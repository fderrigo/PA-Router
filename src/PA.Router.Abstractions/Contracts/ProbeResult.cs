namespace PA.Router.Abstractions.Contracts;

/// <summary>Stato della quota osservato. Assente significa <c>Unknown</c>, mai una stima.</summary>
public enum QuotaState
{
    [WireValue("unknown")]
    Unknown,

    [WireValue("available")]
    Available,

    [WireValue("exhausted")]
    Exhausted,
}

/// <summary>Salute osservata del service.</summary>
public enum ProbeHealth
{
    [WireValue("healthy")]
    Healthy,

    [WireValue("degraded")]
    Degraded,

    [WireValue("unavailable")]
    Unavailable,
}

/// <summary>Stato dell'autenticazione osservata sul programma o sull'endpoint.</summary>
public enum AuthStatus
{
    [WireValue("unknown")]
    Unknown,

    [WireValue("active")]
    Active,

    [WireValue("inactive")]
    Inactive,
}

/// <summary>
/// Osservazione su un service. La configurazione e' una dichiarazione, il probe e' un fatto
/// misurato: i due non vanno confusi e l'effettivo e' sempre l'intersezione dei due
/// (03-specs/REGISTRY.md).
/// </summary>
/// <param name="TargetId">Target osservato.</param>
/// <param name="Health">Salute osservata.</param>
/// <param name="AuthStatus">Autenticazione osservata.</param>
/// <param name="QuotaState">Quota osservata.</param>
/// <param name="ExecutablePath">Percorso assoluto realmente eseguito, quando applicabile.</param>
/// <param name="ExecutableHash">Hash del programma osservato.</param>
/// <param name="Version">Versione del programma o del server osservata.</param>
/// <param name="ProtocolVersion">Versione di protocollo osservata.</param>
/// <param name="Model">Modello effettivo osservato.</param>
/// <param name="Capabilities">Capacita' osservate.</param>
/// <param name="Controls">Controlli osservati.</param>
/// <param name="ContextWindowTokens">Finestra osservata, null se non osservabile.</param>
/// <param name="CheckedAt">Istante dell'osservazione.</param>
/// <param name="ExpiresAt">Scadenza dell'osservazione.</param>
/// <param name="FailureCode">Codice del guasto osservato, quando presente.</param>
public sealed record ProbeResult(
    string TargetId,
    ProbeHealth Health,
    AuthStatus AuthStatus,
    QuotaState QuotaState,
    string? ExecutablePath,
    string? ExecutableHash,
    string? Version,
    string? ProtocolVersion,
    string? Model,
    IReadOnlySet<Capability> Capabilities,
    IReadOnlySet<Control> Controls,
    int? ContextWindowTokens,
    DateTimeOffset CheckedAt,
    DateTimeOffset ExpiresAt,
    string? FailureCode)
{
    /// <summary>
    /// Vero se il modello osservato sa usare strumenti. Solo una probe che lo osserva lo dichiara; assente vale
    /// no (ADR-020).
    /// </summary>
    public bool ToolCalling { get; init; }
    /// <summary>Vero se l'osservazione e' ancora valida all'istante indicato.</summary>
    public bool IsFreshAt(DateTimeOffset instant) => instant < ExpiresAt;
}

/// <summary>
/// Sorgente di osservazioni sui service. Il probing locale non carica prompt e non avvia
/// inferenza: e' una verifica di disponibilita', non un'esecuzione.
/// </summary>
public interface ITargetProbe
{
    /// <summary>Osserva un servizio dichiarato.</summary>
    ValueTask<ProbeResult> ProbeAsync(ServiceDescriptor service, CancellationToken cancellationToken);
}
