using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Abstractions.Observability;

/// <summary>
/// Telemetria del router (03-specs/OBSERVABILITY.md).
///
/// Due flussi distinti: l'audit e' affidabile e non campionato, la telemetria e' best-effort. Un
/// fallimento della telemetria non ferma un'inferenza; un fallimento dell'audit prima del dispatch
/// si'.
///
/// Gli attributi sono una allowlist chiusa. Fuori restano prompt, percorsi, identificatori di
/// sessione ed esecuzione, messaggi del provider e qualunque testo: un'etichetta con
/// <c>executionId</c> renderebbe collegabili le metriche, e una con il prompt le renderebbe un
/// archivio di contenuti. <c>executionId</c> resta disponibile per la correlazione locale come
/// identificativo dello span, mai come etichetta di metrica.
///
/// Nessun exporter remoto e' attivo per default e nessun collector viene incluso nel rilascio.
/// </summary>
public static class RouterTelemetry
{
    /// <summary>Nome della sorgente di tracce e degli strumenti di misura.</summary>
    public const string SourceName = "PA.Router";

    /// <summary>Sorgente delle tracce.</summary>
    public static ActivitySource Source { get; } = new(SourceName);

    /// <summary>Strumenti di misura.</summary>
    public static Meter Meter { get; } = new(SourceName);

    /// <summary>
    /// Attributi ammessi. Un nome fuori da questo insieme non viene registrato: la lista e' chiusa
    /// apposta, perche' aggiungere un'etichetta e' facile e toglierla dopo una fuga non serve.
    /// </summary>
    public static IReadOnlySet<string> AllowedAttributes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "channel",
        "target_kind",
        "target_id",
        "state",
        "reason",
        "adapter",
        "adapter_version",
        "policy_revision",
        "bytes_bucket",
        "memory_mode",
        "stage",
    };

    /// <summary>Nomi degli span previsti dalla specifica.</summary>
    public static IReadOnlyList<string> SpanNames { get; } =
    [
        "execution",
        "context.build",
        "privacy.analyze",
        "policy.evaluate",
        "routing.rank",
        "adapter.attempt",
        "output.screen",
        "storage.commit",
    ];

    /// <summary>Nomi delle gauge osservabili previste dalla specifica.</summary>
    public static IReadOnlyList<string> GaugeNames { get; } =
    [
        "active_execution",
        "scheduler_active",
        "storage_queue_depth",
        "memory_job_pending",
        "memory_job_failed",
    ];

    private static readonly Counter<long> Executions = Meter.CreateCounter<long>("execution_count");
    private static readonly Counter<long> Denials = Meter.CreateCounter<long>("policy_denial_count");
    private static readonly Counter<long> Attempts = Meter.CreateCounter<long>("attempt_count");
    private static readonly Counter<long> Busy = Meter.CreateCounter<long>("busy_count");
    private static readonly Counter<long> AuditFailures = Meter.CreateCounter<long>("audit_write_failure_count");
    private static readonly Counter<long> ProtocolErrors = Meter.CreateCounter<long>("protocol_error_count");
    private static readonly Counter<long> OutputBlocked = Meter.CreateCounter<long>("output_blocked_count");
    private static readonly Counter<long> PluginDenied = Meter.CreateCounter<long>("plugin_denied_count");
    private static readonly Counter<long> MemoryIngest = Meter.CreateCounter<long>("memory_ingest_count");
    private static readonly Histogram<double> StageDuration = Meter.CreateHistogram<double>("stage_duration_ms");
    private static readonly Histogram<double> MemorySearch = Meter.CreateHistogram<double>("memory_search_duration_ms");

    private static readonly ConcurrentDictionary<string, Func<int>> Gauges = new(StringComparer.Ordinal);

    // Le gauge leggono attraverso un indirizzamento: il processo che conosce il valore — scheduler,
    // writer, coda dei job — si registra all'avvio, e la telemetria non tiene un riferimento a nulla
    // che possa tenere vivo un host gia' chiuso.
    private static readonly ObservableGauge<int> ActiveExecution =
        Meter.CreateObservableGauge("active_execution", () => Read("active_execution"));

    private static readonly ObservableGauge<int> SchedulerActive =
        Meter.CreateObservableGauge("scheduler_active", () => Read("scheduler_active"));

    private static readonly ObservableGauge<int> StorageQueueDepth =
        Meter.CreateObservableGauge("storage_queue_depth", () => Read("storage_queue_depth"));

    private static readonly ObservableGauge<int> MemoryJobPending =
        Meter.CreateObservableGauge("memory_job_pending", () => Read("memory_job_pending"));

    private static readonly ObservableGauge<int> MemoryJobFailed =
        Meter.CreateObservableGauge("memory_job_failed", () => Read("memory_job_failed"));

    /// <summary>Confini degli istogrammi in millisecondi, fissati dalla specifica.</summary>
    public static IReadOnlyList<double> HistogramBoundaries { get; } =
        [5, 10, 25, 50, 100, 250, 500, 1000, 5000, 30000, 300000];

    /// <summary>
    /// Collega una gauge alla sua sorgente. Un nome non previsto viene rifiutato: le gauge sono
    /// parte dello schema esportabile quanto gli attributi.
    /// </summary>
    public static void BindGauge(string name, Func<int> reader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(reader);

        if (!GaugeNames.Contains(name))
        {
            throw new ArgumentOutOfRangeException(nameof(name), "Gauge not in the schema: " + name);
        }

        Gauges[name] = reader;
    }

    /// <summary>Scollega una gauge: un host che si chiude non deve restare leggibile.</summary>
    public static void UnbindGauge(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Gauges.TryRemove(name, out _);
    }

    /// <summary>
    /// Avvia uno span con soli attributi ammessi. Gli attributi non in allowlist vengono scartati
    /// in silenzio: rifiutare la chiamata renderebbe la telemetria capace di far fallire un turno.
    /// </summary>
    public static Activity? StartSpan(string name, params (string Key, object? Value)[] attributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(attributes);

        Activity? activity = Source.StartActivity(name);

        if (activity is null)
        {
            return null;
        }

        foreach ((string key, object? value) in attributes)
        {
            if (AllowedAttributes.Contains(key))
            {
                activity.SetTag(key, value);
            }
        }

        return activity;
    }

    /// <summary>Registra la conclusione di un'esecuzione.</summary>
    public static void RecordExecution(ExecutionState state, Channel channel) =>
        Executions.Add(1, Tag("state", state.ToWire()), Tag("channel", channel.ToWire()));

    /// <summary>Registra un diniego con il suo motivo.</summary>
    public static void RecordDenial(string reason) => Denials.Add(1, Tag("reason", reason));

    /// <summary>Registra un tentativo verso un target.</summary>
    public static void RecordAttempt(string targetId, AdapterKind adapter) =>
        Attempts.Add(1, Tag("target_id", targetId), Tag("adapter", adapter.ToWire()));

    /// <summary>Registra un rifiuto per risorsa occupata.</summary>
    public static void RecordBusy(string reason) => Busy.Add(1, Tag("reason", reason));

    /// <summary>Registra il fallimento di una scrittura di audit.</summary>
    public static void RecordAuditFailure() => AuditFailures.Add(1);

    /// <summary>Registra un errore di protocollo di un adapter o di uno strumento.</summary>
    public static void RecordProtocolError(string adapter) => ProtocolErrors.Add(1, Tag("adapter", adapter));

    /// <summary>Registra un output bloccato dallo screening.</summary>
    public static void RecordOutputBlocked(string reason) => OutputBlocked.Add(1, Tag("reason", reason));

    /// <summary>Registra un permesso negato a un'estensione.</summary>
    public static void RecordPluginDenied(string reason) => PluginDenied.Add(1, Tag("reason", reason));

    /// <summary>Registra l'ingresso di un'osservazione in memoria.</summary>
    public static void RecordMemoryIngest() => MemoryIngest.Add(1);

    /// <summary>Registra la durata di una fase della pipeline.</summary>
    public static void RecordStage(string stage, double milliseconds) =>
        StageDuration.Record(milliseconds, Tag("stage", stage));

    /// <summary>Registra la durata di una ricerca in memoria e il modo con cui e' avvenuta.</summary>
    public static void RecordMemorySearch(double milliseconds, string mode) =>
        MemorySearch.Record(milliseconds, Tag("memory_mode", mode));

    /// <summary>
    /// Riduce una dimensione a un intervallo: il numero esatto di byte di un payload e' esso stesso
    /// un'informazione sul contenuto.
    /// </summary>
    public static string BytesBucket(long bytes) => bytes switch
    {
        < 1024 => "lt_1k",
        < 8 * 1024 => "lt_8k",
        < 64 * 1024 => "lt_64k",
        < 512 * 1024 => "lt_512k",
        _ => "ge_512k",
    };

    private static int Read(string name) => Gauges.TryGetValue(name, out Func<int>? reader) ? Safe(reader) : 0;

    private static int Safe(Func<int> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception)
        {
            // Una gauge che solleva non deve propagare dentro il ciclo di raccolta: il valore
            // mancante e' un difetto della telemetria, non dell'esecuzione in corso.
            return 0;
        }
    }

    private static KeyValuePair<string, object?> Tag(string key, object? value) =>
        AllowedAttributes.Contains(key)
            ? new KeyValuePair<string, object?>(key, value)
            : throw new ArgumentOutOfRangeException(nameof(key), "Attribute not in the allowlist: " + key);
}
