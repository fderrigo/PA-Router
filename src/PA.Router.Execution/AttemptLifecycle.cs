using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Execution;

/// <summary>
/// Transizioni ammesse di execution e attempt.
///
/// Uno stato terminale non torna mai a running: un recovery crea una nuova execution. La regola
/// e' applicata qui, non lasciata alla disciplina del chiamante, perche' e' cio' che impedisce a
/// un errore di far ripartire un tentativo che potrebbe avere gia' prodotto effetti
/// (02-architecture/DOMAIN-MODEL.md).
/// </summary>
public static class AttemptLifecycle
{
    private static readonly Dictionary<ExecutionState, ExecutionState[]> ExecutionTransitions = new()
    {
        [ExecutionState.Accepted] = [ExecutionState.Analyzing, ExecutionState.Failed, ExecutionState.Cancelled],
        [ExecutionState.Analyzing] = [ExecutionState.Evaluating, ExecutionState.Failed, ExecutionState.Cancelled],
        [ExecutionState.Evaluating] = [ExecutionState.Routing, ExecutionState.Denied, ExecutionState.Failed, ExecutionState.Cancelled],
        [ExecutionState.Routing] = [ExecutionState.Running, ExecutionState.Denied, ExecutionState.Failed, ExecutionState.Cancelled],
        [ExecutionState.Running] = [ExecutionState.Screening, ExecutionState.Routing, ExecutionState.ToolCalling, ExecutionState.Denied, ExecutionState.Failed, ExecutionState.Cancelled, ExecutionState.Uncertain],
        [ExecutionState.ToolCalling] = [ExecutionState.Running, ExecutionState.Denied, ExecutionState.Failed, ExecutionState.Cancelled, ExecutionState.Uncertain],
        [ExecutionState.Screening] = [ExecutionState.Completed, ExecutionState.Failed, ExecutionState.Cancelled, ExecutionState.Uncertain],
        [ExecutionState.Completed] = [],
        [ExecutionState.Denied] = [],
        [ExecutionState.Failed] = [],
        [ExecutionState.Cancelled] = [],
        [ExecutionState.Uncertain] = [],
    };

    private static readonly Dictionary<AttemptState, AttemptState[]> AttemptTransitions = new()
    {
        [AttemptState.Prepared] = [AttemptState.Dispatching, AttemptState.Cancelled, AttemptState.Failed],
        [AttemptState.Dispatching] = [AttemptState.Running, AttemptState.Failed, AttemptState.Cancelled, AttemptState.Uncertain],
        [AttemptState.Running] = [AttemptState.Completed, AttemptState.Failed, AttemptState.Cancelled, AttemptState.Uncertain],
        [AttemptState.Completed] = [],
        [AttemptState.Failed] = [],
        [AttemptState.Cancelled] = [],
        [AttemptState.Uncertain] = [],
    };

    /// <summary>Stati terminali di una execution.</summary>
    public static IReadOnlySet<ExecutionState> TerminalExecutionStates { get; } = new HashSet<ExecutionState>
    {
        ExecutionState.Completed,
        ExecutionState.Denied,
        ExecutionState.Failed,
        ExecutionState.Cancelled,
        ExecutionState.Uncertain,
    };

    /// <summary>Stati terminali di un tentativo.</summary>
    public static IReadOnlySet<AttemptState> TerminalAttemptStates { get; } = new HashSet<AttemptState>
    {
        AttemptState.Completed,
        AttemptState.Failed,
        AttemptState.Cancelled,
        AttemptState.Uncertain,
    };

    /// <summary>Vero se la transizione di execution e' ammessa.</summary>
    public static bool CanTransition(ExecutionState from, ExecutionState to) =>
        ExecutionTransitions[from].Contains(to);

    /// <summary>Vero se la transizione di tentativo e' ammessa.</summary>
    public static bool CanTransition(AttemptState from, AttemptState to) =>
        AttemptTransitions[from].Contains(to);

    /// <summary>Applica la transizione o lancia un errore tipizzato.</summary>
    public static ExecutionState Transition(ExecutionState from, ExecutionState to) =>
        CanTransition(from, to)
            ? to
            : throw new PaRouterException(ErrorCode.ProtocolError, "The execution state transition is not allowed.")
                .WithMetadata("from", from.ToWire())
                .WithMetadata("to", to.ToWire());

    /// <summary>Applica la transizione o lancia un errore tipizzato.</summary>
    public static AttemptState Transition(AttemptState from, AttemptState to) =>
        CanTransition(from, to)
            ? to
            : throw new PaRouterException(ErrorCode.ProtocolError, "The attempt state transition is not allowed.")
                .WithMetadata("from", from.ToWire())
                .WithMetadata("to", to.ToWire());

    /// <summary>
    /// Effetti collaterali attribuiti a un tentativo appena dispatchato. Un agente passa a
    /// <c>possible</c> nel momento in cui il processo parte, non quando dichiara una modifica.
    /// </summary>
    public static SideEffectState OnDispatch(TargetKind kind) =>
        kind is TargetKind.Agent ? SideEffectState.Possible : SideEffectState.None;

    /// <summary>
    /// Stato terminale dopo un guasto successivo al dispatch.
    ///
    /// Con effetti possibili l'esito e' <c>uncertain</c> e richiede recovery dell'operatore: un
    /// exit code o un 429 non provano che non sia successo nulla (03-specs/ERRORS.md).
    /// </summary>
    public static ExecutionState AfterDispatchFailure(SideEffectState sideEffects) =>
        sideEffects is SideEffectState.None ? ExecutionState.Failed : ExecutionState.Uncertain;

    /// <summary>
    /// Vero se un secondo tentativo e' ammesso. Serve un esito senza effetti possibili, un
    /// budget di tentativi residuo e un target alternativo.
    /// </summary>
    /// <summary>
    /// Guasti dopo i quali un secondo servizio puo' essere tentato: il primo non ha risposto, non ha accettato le
    /// credenziali o non ha quota. Tutto il resto — risposta bloccata, errore di protocollo, limiti, controlli mancanti —
    /// si ripeterebbe uguale o manderebbe lo stesso payload dove non doveva andare (ERRORS.md).
    /// </summary>
    public static IReadOnlySet<ErrorCode> RetryableCodes { get; } = new HashSet<ErrorCode>
    {
        ErrorCode.TargetUnavailable,
        ErrorCode.AuthUnavailable,
        ErrorCode.QuotaExhausted,
        ErrorCode.ProviderTimeout,
    };

    public static bool CanRetry(
        int attemptsUsed,
        int maxAttempts,
        SideEffectState sideEffects,
        bool outputPublished,
        ErrorCode? errorCode = null) =>
        attemptsUsed < maxAttempts
        && sideEffects is SideEffectState.None
        && !outputPublished
        && (errorCode is null || RetryableCodes.Contains(errorCode.Value));
}
