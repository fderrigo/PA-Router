using System.Text.Json;
using System.Text.Json.Serialization;
using PA.Router.Abstractions.Contracts;
using PA.Router.Infrastructure.Storage;

namespace PA.Router.Hosting.Runtime;

/// <summary>Un servizio messo in pausa perche' ha finito la quota.</summary>
/// <param name="ServiceId">Servizio.</param>
/// <param name="Since">Quando il fornitore lo ha detto.</param>
/// <param name="Until">Quando torna da solo, se il fornitore lo ha detto; null finche' l'operatore non lo riattiva.</param>
public sealed record QuotaPause(string ServiceId, DateTimeOffset Since, DateTimeOffset? Until);

/// <summary>
/// Pause per quota finita, in un punto solo (24 settembre 2026, decisione dell'operatore: «lo deve ricordare fino al primo
/// ottobre, ma l'utente dal setup lo può sbloccare»). A fine credito Anthropic rispondeva «quota esaurita» a ogni turno, e
/// ogni domanda difficile perdeva un tentativo prima di passare al servizio successivo.
///
/// Quando un fornitore dice che la quota e' finita per piu' di un momento — credito esaurito o limite di spesa, non un limite
/// di frequenza — il servizio va in pausa: fino alla data che il fornitore indica, oppure finche' l'operatore non lo
/// riattiva. In pausa, le sue osservazioni dicono quota esaurita e la policy lo esclude con `QUOTA_EXHAUSTED`, la regola che
/// esisteva gia'. La pausa e' stato del router, non configurazione: sta in un file della directory dati, scritto per
/// intero su un temporaneo e poi spostato. Contiene servizio e date, nessun contenuto.
/// </summary>
public static class QuotaPauses
{
    /// <summary>Esito di osservazione che dice perche' il servizio e' fermo.</summary>
    public const string FailureCode = "quota_paused";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Lock Gate = new();

    private static string FileOf(StorePaths paths) => Path.Combine(paths.DataDirectory, "quota-pauses.json");

    /// <summary>Pause in corso adesso. Quelle scadute non contano: il servizio torna da solo.</summary>
    public static IReadOnlyList<QuotaPause> Active(StorePaths paths, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return [.. ReadAll(paths).Where(pause => pause.Until is not DateTimeOffset until || until > now)];
    }

    /// <summary>La pausa in corso di un servizio, se c'e'.</summary>
    public static QuotaPause? Of(StorePaths paths, string serviceId, DateTimeOffset now) =>
        Active(paths, now).FirstOrDefault(pause => string.Equals(pause.ServiceId, serviceId, StringComparison.Ordinal));

    /// <summary>Mette in pausa un servizio; una pausa gia' presente si aggiorna.</summary>
    public static void Pause(StorePaths paths, string serviceId, DateTimeOffset now, DateTimeOffset? until)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        lock (Gate)
        {
            List<QuotaPause> pauses = [.. ReadAll(paths).Where(pause =>
                !string.Equals(pause.ServiceId, serviceId, StringComparison.Ordinal)
                && (pause.Until is not DateTimeOffset expiry || expiry > now))];

            pauses.Add(new QuotaPause(serviceId, now, until));
            Write(paths, pauses);
        }
    }

    /// <summary>Riattiva un servizio prima della data: vero se era in pausa.</summary>
    public static bool Resume(StorePaths paths, string serviceId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        lock (Gate)
        {
            List<QuotaPause> pauses = [.. ReadAll(paths)];
            int removed = pauses.RemoveAll(pause => string.Equals(pause.ServiceId, serviceId, StringComparison.Ordinal));

            if (removed > 0) { Write(paths, pauses); }

            return removed > 0;
        }
    }

    /// <summary>
    /// Le osservazioni con le pause applicate: un servizio in pausa ha la quota esaurita, e la policy lo esclude. Le altre
    /// restano come sono.
    /// </summary>
    public static IReadOnlyDictionary<string, ProbeResult> Apply(
        IReadOnlyDictionary<string, ProbeResult> probes,
        IReadOnlyList<QuotaPause> pauses)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(pauses);

        if (pauses.Count == 0) { return probes; }

        Dictionary<string, ProbeResult> paused = new(probes, StringComparer.Ordinal);

        foreach (QuotaPause pause in pauses)
        {
            if (paused.TryGetValue(pause.ServiceId, out ProbeResult? probe))
            {
                paused[pause.ServiceId] = probe with { QuotaState = QuotaState.Exhausted, FailureCode = FailureCode };
            }
        }

        return paused;
    }

    private static List<QuotaPause> ReadAll(StorePaths paths)
    {
        string file = FileOf(paths);

        if (!File.Exists(file)) { return []; }

        try
        {
            return JsonSerializer.Deserialize<List<QuotaPause>>(File.ReadAllText(file), Options) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Un file illeggibile non ferma il router: vale come nessuna pausa, e il servizio viene riprovato. Il peggio che
            // succede e' un tentativo in piu', che rimette la pausa.
            return [];
        }
    }

    private static void Write(StorePaths paths, List<QuotaPause> pauses)
    {
        string file = FileOf(paths);
        string temporary = file + "." + Guid.NewGuid().ToString("n") + ".tmp";

        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(temporary, JsonSerializer.Serialize(pauses, Options));
        File.Move(temporary, file, overwrite: true);
    }
}
