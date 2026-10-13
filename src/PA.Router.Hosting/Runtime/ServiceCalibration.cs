using System.Globalization;
using Microsoft.Data.Sqlite;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Infrastructure.Storage;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Misura di un servizio, dalla storia delle esecuzioni gia' avvenute.
/// </summary>
/// <param name="ServiceId">Servizio misurato.</param>
/// <param name="Samples">Tentativi completati che compongono la misura.</param>
/// <param name="MedianMs">Mediana della durata osservata.</param>
/// <param name="P95Ms">Novantacinquesimo percentile della durata osservata.</param>
/// <param name="LatencyScore">
/// Punteggio 0..1 da scrivere nel registro, oppure null quando i campioni non bastano.
/// </param>
public sealed record ServiceMeasurement(
    string ServiceId,
    int Samples,
    long MedianMs,
    long P95Ms,
    double? LatencyScore);

/// <summary>
/// Esito di una campagna di calibrazione.
/// </summary>
/// <param name="Window">Finestra di storia considerata.</param>
/// <param name="Measurements">Misure per servizio, in ordine di identificatore.</param>
/// <param name="MinimumSamples">Campioni sotto i quali non si scrive nulla.</param>
public sealed record CalibrationReport(
    TimeSpan Window,
    IReadOnlyList<ServiceMeasurement> Measurements,
    int MinimumSamples)
{
    /// <summary>Misure che hanno abbastanza campioni per diventare un valore del registro.</summary>
    public IReadOnlyList<ServiceMeasurement> Usable =>
        [.. Measurements.Where(measurement => measurement.LatencyScore is not null)];
}

/// <summary>
/// Calibrazione della latenza a partire dalle esecuzioni gia' avvenute.
///
/// Il ranking pesa quattro dimensioni e finora tutte e quattro erano `declared`, cioe' numeri
/// scelti dall'operatore. Finche' i servizi confrontabili erano pochi la classifica non decideva
/// quasi mai; con piu' servizi remoti comincia a decidere, e una classifica costruita su numeri
/// inventati sceglie male senza dirlo.
///
/// Qui si misura **solo cio' che il router osserva davvero**: quanto ci mette un servizio a
/// rispondere. Il dato e' gia' nello store — ogni tentativo ha inizio e fine — quindi la misura non
/// consuma quota, non inventa un carico di prova e non richiede che l'operatore faccia nulla di
/// diverso dal proprio lavoro.
///
/// **La qualita' non si misura qui e non viene toccata.** Misurarla richiederebbe un banco di prova
/// con risposte attese, cioe' decidere che cosa sia una buona risposta: e' una scelta di prodotto,
/// non un calcolo, e un numero prodotto da un banco inventato sarebbe peggio di un numero
/// dichiarato — perche' sembrerebbe una misura.
/// </summary>
public static class ServiceCalibration
{
    /// <summary>
    /// Campioni minimi perche' una misura diventi un valore del registro.
    ///
    /// Una mediana su tre tentativi non e' una misura: e' un aneddoto. Sotto questa soglia la
    /// misura viene mostrata con il proprio conteggio e non viene scritta.
    /// </summary>
    public const int MinimumSamples = 10;

    /// <summary>Finestra predefinita: la storia recente, non l'intera vita dello store.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// Misura i servizi sulla storia delle esecuzioni.
    /// </summary>
    /// <param name="stack">Composizione aperta.</param>
    /// <param name="window">Quanta storia considerare.</param>
    /// <param name="now">Istante di riferimento.</param>
    public static CalibrationReport Measure(RouterStack stack, TimeSpan window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(stack);

        Dictionary<string, List<long>> durations = ReadDurations(stack.ConnectionFactory, window, now);

        // Il punteggio e' **relativo** ai servizi osservati: il piu' veloce prende 1, gli altri la
        // propria frazione. E' la forma giusta perche' il ranking confronta i servizi fra loro, non
        // con una soglia assoluta che qualcuno dovrebbe inventare. Conseguenza dichiarata: quando
        // l'insieme dei servizi cambia, la calibrazione va rifatta.
        long fastest = durations.Values
            .Where(samples => samples.Count >= MinimumSamples)
            .Select(Median)
            .DefaultIfEmpty(0)
            .Where(median => median > 0)
            .DefaultIfEmpty(0)
            .Min();

        List<ServiceMeasurement> measurements = [];

        foreach ((string serviceId, List<long> samples) in durations.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            samples.Sort();

            long median = Median(samples);
            long p95 = Percentile(samples, 0.95);

            double? score = samples.Count >= MinimumSamples && fastest > 0 && median > 0
                ? Math.Round(Math.Clamp((double)fastest / median, 0.01, 1.0), 3)
                : null;

            measurements.Add(new ServiceMeasurement(serviceId, samples.Count, median, p95, score));
        }

        return new CalibrationReport(window, measurements, MinimumSamples);
    }

    /// <summary>
    /// Durate dei tentativi **completati**, per servizio.
    ///
    /// Un tentativo fallito o annullato misura il guasto, non il servizio: includerlo farebbe
    /// sembrare velocissimo un servizio che rifiuta subito.
    /// </summary>
    private static Dictionary<string, List<long>> ReadDurations(
        SqliteConnectionFactory factory,
        TimeSpan window,
        DateTimeOffset now)
    {
        Dictionary<string, List<long>> durations = new(StringComparer.Ordinal);

        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT a.target_id, a.ended_at_ms - a.started_at_ms AS duration "
            + "FROM attempts a "
            + "WHERE a.state = 'completed' "
            + "  AND a.started_at_ms IS NOT NULL AND a.ended_at_ms IS NOT NULL "
            + "  AND a.ended_at_ms >= a.started_at_ms "
            + "  AND a.started_at_ms >= $since;";

        command.Parameters.AddWithValue("$since", (now - window).ToUnixTimeMilliseconds());

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            string serviceId = reader.GetString(0);

            if (!durations.TryGetValue(serviceId, out List<long>? samples))
            {
                samples = [];
                durations[serviceId] = samples;
            }

            samples.Add(reader.GetInt64(1));
        }

        return durations;
    }

    private static long Median(List<long> sorted)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        List<long> ordered = [.. sorted];
        ordered.Sort();

        return ordered[ordered.Count / 2];
    }

    private static long Percentile(List<long> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        List<long> ordered = [.. sorted];
        ordered.Sort();

        int index = (int)Math.Ceiling(percentile * ordered.Count) - 1;

        return ordered[Math.Clamp(index, 0, ordered.Count - 1)];
    }

    /// <summary>
    /// Applica le misure al documento del registro, toccando **solo** la latenza dei servizi
    /// misurati. Qualita', costo e quota restano come sono: non sono stati misurati, e riscriverli
    /// significherebbe spacciare per misura un numero che nessuno ha osservato.
    /// </summary>
    /// <returns>Servizi effettivamente aggiornati.</returns>
    public static IReadOnlyList<string> Apply(
        System.Text.Json.Nodes.JsonObject registry,
        CalibrationReport report,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(report);

        Dictionary<string, ServiceMeasurement> byId = report.Usable
            .ToDictionary(measurement => measurement.ServiceId, StringComparer.Ordinal);

        List<string> updated = [];

        if (registry["services"] is not System.Text.Json.Nodes.JsonArray services)
        {
            return updated;
        }

        foreach (System.Text.Json.Nodes.JsonNode? node in services)
        {
            if (node is not System.Text.Json.Nodes.JsonObject service
                || service["id"]?.GetValue<string>() is not string id
                || !byId.TryGetValue(id, out ServiceMeasurement? measurement))
            {
                continue;
            }

            if (service["calibration"] is not System.Text.Json.Nodes.JsonObject calibration)
            {
                calibration = [];
                service["calibration"] = calibration;
            }

            calibration["latency"] = new System.Text.Json.Nodes.JsonObject
            {
                ["value"] = measurement.LatencyScore!.Value,
                ["source"] = CalibrationSource.Measured.ToWire(),
                ["observedAt"] = observedAt.ToString("O", CultureInfo.InvariantCulture),
            };

            updated.Add(id);
        }

        return updated;
    }
}
