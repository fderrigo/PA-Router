using System.Globalization;
using System.Text;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Observability;

namespace PA.Router.Infrastructure.Observability;

/// <summary>Livelli usati dal log locale.</summary>
public enum RouterLogLevel
{
    /// <summary>Metadati di diagnosi. Non significa «contenuti».</summary>
    Debug,

    /// <summary>Ciclo di vita sintetico.</summary>
    Info,

    /// <summary>Diniego o fallback, sempre con un codice.</summary>
    Warning,

    /// <summary>Guasto di storage o di protocollo.</summary>
    Error,

    /// <summary>
    /// Nessuna riga. E' l'unico valore che toglie anche i guasti, e vale solo se l'operatore lo
    /// chiede: resta ultimo perche' la soglia si confronta con <c>level &lt; minimum</c>.
    /// </summary>
    None,
}

/// <summary>
/// Log locale strutturato (03-specs/OBSERVABILITY.md).
///
/// Una riga e' fatta di un codice e di metadati con nomi noti: non esiste un percorso per scriverci
/// testo libero. Il valore di ogni metadato viene comunque ripulito da CR, LF, controlli terminale,
/// sequenze ANSI e markup, perche' un valore lecito puo' arrivare da un adapter e una riga di log
/// non deve poter pilotare il terminale di chi la legge.
///
/// Le chiavi ammesse sono quelle della telemetria piu' gli identificatori di correlazione locale.
/// Authorization, token di sessione, chiavi di idempotenza, corpi HTTP, parametri SQL e stderr
/// grezzo non hanno una chiave: non vengono filtrati, semplicemente non esistono qui.
///
/// Rotazione: 5 file da 5 MiB, e nulla piu' vecchio di 7 giorni.
/// </summary>
public sealed class RouterLog : IDisposable
{
    /// <summary>Dimensione massima di un file prima della rotazione.</summary>
    public const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>Numero massimo di file conservati, corrente compreso.</summary>
    public const int MaxFiles = 5;

    /// <summary>Eta' massima di un file di log.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>Lunghezza massima di un valore di metadato.</summary>
    public const int MaxValueLength = 200;

    /// <summary>
    /// Chiavi di correlazione ammesse nel log e mai nelle metriche: servono a ricucire una riga con
    /// un audit sulla stessa macchina, e restano fuori da qualunque exporter.
    /// </summary>
    public static IReadOnlySet<string> CorrelationKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "execution_id",
        "attempt_id",
        "trace_id",
    };

    private readonly string directory;
    private readonly IClock clock;
    private readonly Lock gate = new();
    private readonly RouterLogLevel minimum;
    private bool disposed;

    public RouterLog(string logsDirectory, IClock clock, RouterLogLevel minimum = RouterLogLevel.Info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        ArgumentNullException.ThrowIfNull(clock);

        directory = Path.GetFullPath(logsDirectory);
        this.clock = clock;
        this.minimum = minimum;

        Directory.CreateDirectory(directory);
    }

    /// <summary>File corrente.</summary>
    public string CurrentFile => Path.Combine(directory, "pa-router.log");

    /// <summary>Vero se la chiave puo' comparire in una riga di log.</summary>
    public static bool IsAllowedKey(string key) =>
        RouterTelemetry.AllowedAttributes.Contains(key) || CorrelationKeys.Contains(key);

    /// <summary>
    /// Soglia corrispondente a un livello dichiarato dall'operatore.
    ///
    /// I nomi ammessi sono i sette di .NET, perche' sono quelli che un operatore si aspetta di
    /// poter scrivere; i livelli di questo log sono quattro piu' il silenzio, quindi due coppie
    /// collassano: <c>trace</c> vale <c>debug</c> — qui non esiste un grado piu' fine, e prometterlo
    /// sarebbe falso — e <c>critical</c> vale <c>error</c>, perche' un guasto non ha gradi.
    ///
    /// Un valore sconosciuto non fa fallire nulla: vale il predefinito. Il rifiuto avviene prima,
    /// alla lettura della configurazione (<c>INPUT_INVALID</c>), dove l'operatore lo puo' correggere;
    /// qui siamo dentro un turno, e spegnere un router per il livello del log sarebbe sproporzionato.
    /// </summary>
    /// <param name="level">Livello dichiarato, gia' normalizzato o meno; null significa predefinito.</param>
    public static RouterLogLevel LevelFor(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "trace" or "debug" => RouterLogLevel.Debug,
        "information" => RouterLogLevel.Info,
        "warning" => RouterLogLevel.Warning,
        "error" or "critical" => RouterLogLevel.Error,
        "none" => RouterLogLevel.None,
        _ => RouterLogLevel.Info,
    };

    /// <summary>Scrive una riga. Un fallimento del log non interrompe cio' che lo ha prodotto.</summary>
    public void Write(RouterLogLevel level, string code, params (string Key, string? Value)[] metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(metadata);

        if (level < minimum || disposed)
        {
            return;
        }

        StringBuilder line = new();

        line.Append(clock.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(level.ToString().ToUpperInvariant())
            .Append(' ')
            .Append(Sanitize(code));

        foreach ((string key, string? value) in metadata)
        {
            // Una chiave sconosciuta viene scartata insieme al suo valore: e' l'unico modo perche'
            // un campo nuovo non entri nel log per distrazione.
            if (!IsAllowedKey(key))
            {
                continue;
            }

            line.Append(' ').Append(key).Append('=').Append(Sanitize(value ?? string.Empty));
        }

        lock (gate)
        {
            try
            {
                Rotate();
                File.AppendAllText(CurrentFile, line.Append('\n').ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Ciclo di vita.</summary>
    public void Info(string code, params (string Key, string? Value)[] metadata) =>
        Write(RouterLogLevel.Info, code, metadata);

    /// <summary>Diniego o fallback.</summary>
    public void Warning(string code, params (string Key, string? Value)[] metadata) =>
        Write(RouterLogLevel.Warning, code, metadata);

    /// <summary>Guasto di storage o protocollo.</summary>
    public void Error(string code, params (string Key, string? Value)[] metadata) =>
        Write(RouterLogLevel.Error, code, metadata);

    /// <summary>
    /// Ripulisce un valore: via CR, LF, tabulazioni, controlli, sequenze ANSI e parentesi di markup;
    /// poi taglio alla lunghezza massima. Un valore vuoto resta vuoto, non diventa un segnaposto.
    /// </summary>
    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        StringBuilder builder = new(Math.Min(value.Length, MaxValueLength));

        foreach (char character in value)
        {
            if (builder.Length >= MaxValueLength)
            {
                break;
            }

            if (char.IsControl(character))
            {
                // Un solo spazio al posto del controllo: cosi' due token separati da un a capo non
                // si fondono in una parola che non esisteva.
                builder.Append(' ');
                continue;
            }

            builder.Append(character switch
            {
                '[' => '(',
                ']' => ')',
                _ => character,
            });
        }

        return builder.ToString().Trim();
    }

    private void Rotate()
    {
        RemoveExpired();

        if (!File.Exists(CurrentFile) || new FileInfo(CurrentFile).Length < MaxFileBytes)
        {
            return;
        }

        string oldest = CurrentFile + "." + (MaxFiles - 1).ToString(CultureInfo.InvariantCulture);

        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int index = MaxFiles - 2; index >= 1; index--)
        {
            string source = CurrentFile + "." + index.ToString(CultureInfo.InvariantCulture);

            if (File.Exists(source))
            {
                File.Move(source, CurrentFile + "." + (index + 1).ToString(CultureInfo.InvariantCulture), overwrite: true);
            }
        }

        File.Move(CurrentFile, CurrentFile + ".1", overwrite: true);
    }

    private void RemoveExpired()
    {
        DateTimeOffset cutoff = clock.UtcNow.Subtract(Retention);

        foreach (string file in Directory.EnumerateFiles(directory, "pa-router.log*"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose() => disposed = true;
}
