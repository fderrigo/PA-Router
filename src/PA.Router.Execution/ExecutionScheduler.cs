using PA.Router.Abstractions;
using PA.Router.Abstractions.Observability;

namespace PA.Router.Execution;

/// <summary>Risorse prenotate all'ammissione di una esecuzione.</summary>
/// <param name="SessionId">Sessione, esclusiva: una esecuzione per volta.</param>
/// <param name="WorkspaceKey">
/// Chiave canonica del workspace, esclusiva per gli agenti. Null per chi non tocca un repository.
/// </param>
public sealed record ScheduleRequest(string SessionId, string? WorkspaceKey);

/// <summary>
/// Prenotazione riuscita. Rilasciarla libera insieme sessione, workspace, slot globale ed
/// eventuale target riservato.
/// </summary>
public sealed class SchedulerLease : IDisposable
{
    private readonly ExecutionScheduler scheduler;
    private bool released;

    internal SchedulerLease(ExecutionScheduler scheduler, ScheduleRequest request)
    {
        this.scheduler = scheduler;
        Request = request;
    }

    /// <summary>Risorse prenotate all'ammissione.</summary>
    public ScheduleRequest Request { get; }

    /// <summary>Target attualmente riservato da questa lease, se presente.</summary>
    public string? ReservedTargetId { get; internal set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (released)
        {
            return;
        }

        released = true;
        scheduler.Release(this);
    }
}

/// <summary>
/// Ammissione delle esecuzioni concorrenti.
///
/// L'ammissione e' atomica e senza attese: si prenota nell'ordine sessione, workspace, slot
/// globale, e alla prima risorsa occupata si annulla tutto. Restare in attesa tenendo
/// prenotazioni parziali e' il modo classico di costruire un deadlock fra due richieste che si
/// aspettano a vicenda (03-specs/LOCAL-RUNTIME.md).
///
/// Il target si riserva piu' tardi, quando il ranking lo ha scelto, e viene rilasciato dalla
/// stessa lease. Occupato significa busy subito: nessuna execution creata, nessuna idempotency
/// key consumata.
/// </summary>
public sealed class ExecutionScheduler
{
    /// <summary>Slot globali di default.</summary>
    public const int DefaultMaxConcurrency = 4;

    /// <summary>Minimo configurabile.</summary>
    public const int MinConcurrency = 1;

    /// <summary>Massimo configurabile.</summary>
    public const int MaxConcurrency = 8;

    private readonly Lock gate = new();
    private readonly HashSet<string> sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> workspaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> targets = new(StringComparer.Ordinal);
    private int maxConcurrency;
    private int inFlight;

    public ExecutionScheduler(int maxConcurrency = DefaultMaxConcurrency)
    {
        if (maxConcurrency is < MinConcurrency or > MaxConcurrency)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrency),
                "Global concurrency must stay between 1 and 8.");
        }

        this.maxConcurrency = maxConcurrency;

        // Le gauge leggono da qui: lo scheduler e' l'unico che conosce il numero di esecuzioni
        // ammesse in questo istante.
        RouterTelemetry.BindGauge("scheduler_active", () => InFlight);
        RouterTelemetry.BindGauge("active_execution", () => InFlight);
    }

    /// <summary>Slot globali configurati.</summary>
    public int Capacity
    {
        get
        {
            lock (gate)
            {
                return maxConcurrency;
            }
        }
    }

    /// <summary>
    /// Aggiorna gli slot globali quando la configurazione viene riletta. Le esecuzioni gia' ammesse restano dove sono:
    /// una riduzione vale per le prossime.
    /// </summary>
    public void Resize(int maxConcurrency)
    {
        if (maxConcurrency is < MinConcurrency or > MaxConcurrency)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrency),
                "Global concurrency must stay between 1 and 8.");
        }

        lock (gate)
        {
            this.maxConcurrency = maxConcurrency;
        }
    }

    /// <summary>
    /// Chiave canonica di un workspace: percorso completo, senza separatore finale, e senza distinzione fra maiuscole
    /// dove il sistema non la fa. Due grafie della stessa cartella sono lo stesso recinto, e un agente per volta.
    /// </summary>
    public static string WorkspaceKeyFor(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    /// <summary>Esecuzioni attualmente ammesse.</summary>
    public int InFlight
    {
        get
        {
            lock (gate)
            {
                return inFlight;
            }
        }
    }

    /// <summary>
    /// Prenota sessione, workspace e slot globale. Restituisce false senza trattenere nulla se
    /// una risorsa e' occupata; <paramref name="busy"/> dice quale.
    /// </summary>
    public bool TryAcquire(ScheduleRequest request, out SchedulerLease? lease, out ErrorCode busy)
    {
        ArgumentNullException.ThrowIfNull(request);

        lease = null;
        busy = ErrorCode.Busy;

        lock (gate)
        {
            if (!sessions.Add(request.SessionId))
            {
                busy = ErrorCode.SessionBusy;
                return false;
            }

            if (request.WorkspaceKey is string workspace && !workspaces.Add(workspace))
            {
                sessions.Remove(request.SessionId);
                busy = ErrorCode.WorkspaceBusy;
                return false;
            }

            if (inFlight >= maxConcurrency)
            {
                sessions.Remove(request.SessionId);

                if (request.WorkspaceKey is string reserved)
                {
                    workspaces.Remove(reserved);
                }

                busy = ErrorCode.Busy;
                return false;
            }

            inFlight++;
        }

        lease = new SchedulerLease(this, request);
        return true;
    }

    /// <summary>
    /// Riserva il target scelto dal ranking. Il limite e' quello del registro: lo scheduler
    /// applica il minimo fra i limiti, non il massimo.
    /// </summary>
    public bool TryReserveTarget(SchedulerLease lease, string targetId, int maxConcurrent)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        lock (gate)
        {
            if (lease.ReservedTargetId is not null)
            {
                throw new InvalidOperationException("The lease already reserves a target.");
            }

            int used = targets.GetValueOrDefault(targetId);

            if (used >= maxConcurrent)
            {
                return false;
            }

            targets[targetId] = used + 1;
            lease.ReservedTargetId = targetId;
            return true;
        }
    }

    /// <summary>Rilascia il solo target, quando un tentativo cede il posto al successivo.</summary>
    public void ReleaseTarget(SchedulerLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        lock (gate)
        {
            ReleaseTargetCore(lease);
        }
    }

    internal void Release(SchedulerLease lease)
    {
        lock (gate)
        {
            sessions.Remove(lease.Request.SessionId);

            if (lease.Request.WorkspaceKey is string workspace)
            {
                workspaces.Remove(workspace);
            }

            ReleaseTargetCore(lease);

            if (inFlight > 0)
            {
                inFlight--;
            }
        }
    }

    private void ReleaseTargetCore(SchedulerLease lease)
    {
        if (lease.ReservedTargetId is not string targetId)
        {
            return;
        }

        if (targets.TryGetValue(targetId, out int used))
        {
            if (used <= 1)
            {
                targets.Remove(targetId);
            }
            else
            {
                targets[targetId] = used - 1;
            }
        }

        lease.ReservedTargetId = null;
    }
}
