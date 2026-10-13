using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using PA.Router.Abstractions.Contracts;
using PA.Router.Abstractions.Observability;

namespace PA.Router.Infrastructure.Observability;

/// <summary>Cosa fa la telemetria all'avvio.</summary>
public enum TelemetryDecision
{
    /// <summary>Nessun ascoltatore: e' il default.</summary>
    Disabled,

    /// <summary>Ascolto in processo, scrittura sul log locale.</summary>
    LocalFile,

    /// <summary>
    /// Esportazione remota chiesta ma vietata perche' il profilo iniziale tiene ogni classe di dato in casa: nessuna classe
    /// supera la rete locale (<see cref="TelemetryOptions.KeepsDataHome"/>).
    /// </summary>
    RemoteRefusedDataStaysHome,

    /// <summary>Esportazione remota chiesta verso un endpoint non TLS.</summary>
    RemoteRefusedInsecure,

    /// <summary>Esportazione remota ammissibile ma non implementata in questa beta.</summary>
    RemoteUnsupported,
}

/// <summary>
/// Configurazione della telemetria (03-specs/OBSERVABILITY.md).
///
/// Disattivata per default. L'esportazione remota e' un'opzione esplicita, richiede TLS e un
/// endpoint amministrato, ed e' vietata comunque quando il profilo iniziale tiene ogni classe di dato in casa.
///
/// Dal 24 settembre 2026 il divieto e' una **regola** del profilo, non il suo nome: nessun identificatore di profilo sta
/// nel codice. Un profilo le cui zone non portano nessuna classe oltre la rete locale ha detto che niente esce di casa, e
/// la telemetria non fa eccezione — con qualunque nome l'operatore l'abbia chiamato.
/// </summary>
/// <param name="Enabled">Ascolto in processo attivo.</param>
/// <param name="DataStaysHome">Il profilo iniziale non porta nessuna classe di dato oltre la rete locale.</param>
/// <param name="OtlpEndpoint">Endpoint remoto richiesto, se presente.</param>
public sealed record TelemetryOptions(bool Enabled = false, bool DataStaysHome = false, string? OtlpEndpoint = null)
{
    /// <summary>Impostazione di partenza: nessun ascolto e nessuna uscita.</summary>
    public static TelemetryOptions Default { get; } = new();

    /// <summary>
    /// Vero se nessuna classe di dato puo' andare oltre la rete locale: ogni <c>maxZone</c> e' al massimo <c>lan</c>
    /// (<c>secret</c> e <c>unknown</c> non la superano mai). Le zone sono quelle del profilo compilato.
    /// </summary>
    public static bool KeepsDataHome(PolicyZones zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        return Enum.GetValues<DataClass>().All(dataClass => zones.For(dataClass) <= TrustZone.Lan);
    }

    /// <summary>
    /// Opzioni derivate dalla configurazione validata e dalle zone del profilo iniziale compilato. L'ascolto in processo
    /// si accende quando l'operatore ha configurato un esportatore: senza configurazione non si misura nulla.
    /// </summary>
    public static TelemetryOptions FromConfig(ConfigDocument config, PolicyZones defaultProfileZones)
    {
        ArgumentNullException.ThrowIfNull(config);

        bool home = KeepsDataHome(defaultProfileZones);

        return config.Telemetry.Exporter is TelemetryExporter.None
            ? new TelemetryOptions(Enabled: false, DataStaysHome: home)
            : new TelemetryOptions(Enabled: true, home, config.Telemetry.Endpoint?.ToString());
    }

    /// <summary>
    /// Decide che cosa e' consentito. Un endpoint remoto non fa mai decadere il divieto: se il profilo iniziale tiene i
    /// dati in casa la richiesta viene rifiutata e detta, non ignorata in silenzio.
    /// </summary>
    public TelemetryDecision Resolve()
    {
        if (OtlpEndpoint is { Length: > 0 } endpoint)
        {
            if (DataStaysHome)
            {
                return TelemetryDecision.RemoteRefusedDataStaysHome;
            }

            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            {
                return TelemetryDecision.RemoteRefusedInsecure;
            }

            // La beta non include un collector e non implementa OTLP: dichiararlo e' piu' onesto
            // che aprire una connessione che non esiste.
            return TelemetryDecision.RemoteUnsupported;
        }

        return Enabled ? TelemetryDecision.LocalFile : TelemetryDecision.Disabled;
    }
}

/// <summary>Una misura osservata dal sink.</summary>
/// <param name="Kind">"span" o "metric".</param>
/// <param name="Name">Nome dello span o dello strumento.</param>
/// <param name="Value">Valore numerico, per le metriche.</param>
/// <param name="Tags">Attributi, gia' ridotti alla allowlist.</param>
public sealed record TelemetryRecord(
    string Kind,
    string Name,
    double Value,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>
/// Ascoltatore in processo di tracce e metriche <c>PA.Router</c>.
///
/// Serve a due cose: il sink locale su file e l'exporter finto dei test. In entrambi i casi filtra
/// gli attributi con la stessa allowlist della telemetria — una seconda volta, di proposito. Se un
/// giorno uno span mettesse un tag non previsto, l'exporter non lo porterebbe comunque fuori.
///
/// Gli identificatori di correlazione non vengono esportati: restano nel log locale.
/// </summary>
public sealed class LocalTelemetrySink : IDisposable
{
    private readonly ActivityListener activities;
    private readonly MeterListener meters;
    private readonly Action<TelemetryRecord> observe;
    private bool disposed;

    public LocalTelemetrySink(Action<TelemetryRecord> observe)
    {
        ArgumentNullException.ThrowIfNull(observe);

        this.observe = observe;

        activities = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, RouterTelemetry.SourceName, StringComparison.Ordinal),
            // Nessun campionamento: la telemetria e' best-effort, ma quando e' accesa non deve
            // mostrare un sottoinsieme scelto a caso di cio' che e' successo.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = OnActivityStopped,
        };

        ActivitySource.AddActivityListener(activities);

        meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, RouterTelemetry.SourceName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        meters.SetMeasurementEventCallback<long>(OnLong);
        meters.SetMeasurementEventCallback<int>(OnInt);
        meters.SetMeasurementEventCallback<double>(OnDouble);
        meters.Start();
    }

    /// <summary>Apre un sink che scrive sul log locale, alla decisione data.</summary>
    public static LocalTelemetrySink? ForDecision(TelemetryDecision decision, RouterLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (decision is not TelemetryDecision.LocalFile)
        {
            return null;
        }

        return new LocalTelemetrySink(record =>
        {
            (string Key, string? Value)[] metadata =
            [
                .. record.Tags.Select(tag => (tag.Key, (string?)tag.Value)),
            ];

            log.Write(
                RouterLogLevel.Debug,
                "telemetry:" + record.Kind + ":" + record.Name
                    + "=" + record.Value.ToString("0.###", CultureInfo.InvariantCulture),
                metadata);
        });
    }

    /// <summary>Forza la raccolta delle gauge osservabili.</summary>
    public void Collect() => meters.RecordObservableInstruments();

    private void OnActivityStopped(Activity activity) =>
        Emit("span", activity.OperationName, activity.Duration.TotalMilliseconds, Filter(activity.TagObjects));

    private void OnLong(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
        Emit("metric", instrument.Name, measurement, Filter(tags));

    private void OnInt(Instrument instrument, int measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
        Emit("metric", instrument.Name, measurement, Filter(tags));

    private void OnDouble(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
        Emit("metric", instrument.Name, measurement, Filter(tags));

    private void Emit(string kind, string name, double value, IReadOnlyDictionary<string, string> tags)
    {
        if (disposed)
        {
            return;
        }

        try
        {
            observe(new TelemetryRecord(kind, name, value, tags));
        }
        catch (Exception)
        {
            // Un exporter che fallisce non deve fermare cio' che stava misurando.
        }
    }

    private static Dictionary<string, string> Filter(IEnumerable<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, string> filtered = new(StringComparer.Ordinal);

        foreach ((string key, object? value) in tags)
        {
            if (RouterTelemetry.AllowedAttributes.Contains(key))
            {
                filtered[key] = RouterLog.Sanitize(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            }
        }

        return filtered;
    }

    private static Dictionary<string, string> Filter(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, string> filtered = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (RouterTelemetry.AllowedAttributes.Contains(tag.Key))
            {
                filtered[tag.Key] = RouterLog.Sanitize(
                    Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty);
            }
        }

        return filtered;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        activities.Dispose();
        meters.Dispose();
    }
}
