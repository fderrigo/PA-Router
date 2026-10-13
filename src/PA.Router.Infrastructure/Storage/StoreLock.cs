using System.Globalization;
using System.Text;
using PA.Router.Abstractions;

namespace PA.Router.Infrastructure.Storage;

/// <summary>
/// Lock esclusivo del sistema operativo sulla directory dati. Un solo worker possiede lo store;
/// gli altri client parlano con quel worker via IPC e non aprono il database.
/// Il rilascio segue la semantica del sistema operativo: alla morte del processo l'handle cade
/// e il lock diventa acquisibile. L'eta' del file non viene mai usata come criterio.
/// </summary>
public sealed class StoreLock : IDisposable
{
    /// <summary>
    /// Offset della regione che segna la proprieta'. Sta lontano dai dati scritti nel file, che
    /// occupano poche decine di byte: bloccare una regione vuota basta a escludere un secondo
    /// proprietario e lascia leggibile cio' che serve alle diagnosi.
    /// </summary>
    private const long OwnershipRegionOffset = 1024 * 1024;

    /// <summary>
    /// Directory dati gia' possedute da questo processo.
    ///
    /// Il blocco del sistema operativo esclude un **altro** processo, e su Unix non fa di piu': i
    /// blocchi POSIX appartengono al processo, quindi due acquisizioni dentro lo stesso programma
    /// non si escludono a vicenda. Su Windows si escludevano, ma per una proprieta' della
    /// piattaforma e non per una scelta: l'invariante «un solo proprietario» valeva su un sistema e
    /// non sull'altro, e nessuno poteva accorgersene finche' la suite girava su uno solo.
    ///
    /// Questo insieme rende la regola esplicita e uguale ovunque.
    /// </summary>
    private static readonly HashSet<string> OwnedDirectories =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly FileStream stream;

    private StoreLock(FileStream stream, string path)
    {
        this.stream = stream;
        Path = path;
    }

    /// <summary>Path del file di lock.</summary>
    public string Path { get; }

    /// <summary>
    /// Acquisisce il lock. Se un altro processo lo possiede, l'errore e' STORE_IN_USE: e' la
    /// condizione normale del secondo worker, non un guasto.
    /// </summary>
    public static StoreLock Acquire(StorePaths paths, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);

        string key = System.IO.Path.GetFullPath(paths.LockFile);

        lock (OwnedDirectories)
        {
            if (!OwnedDirectories.Add(key))
            {
                throw new PaRouterException(
                    ErrorCode.StoreInUse,
                    "The data directory is already owned inside this process.");
            }
        }

        try
        {
            return AcquireCore(paths, clock, key);
        }
        catch
        {
            // Qualunque cosa vada storta dopo la rivendicazione, questo processo non possiede piu'
            // nulla: lasciare il nome nell'insieme renderebbe la directory inaccessibile fino al
            // riavvio, per un guasto che magari non si ripeterebbe.
            Release(key);
            throw;
        }
    }

    private static StoreLock AcquireCore(StorePaths paths, IClock clock, string key)
    {
        FileStream stream;

        try
        {
            // FileShare.Read: nessun altro puo' aprire il file in scrittura, quindi il secondo
            // worker fallisce, ma i dati del proprietario restano leggibili per le diagnosi.
            stream = new FileStream(
                paths.LockFile,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read);
        }
        catch (IOException ex)
        {
            throw new PaRouterException(
                ErrorCode.StoreInUse,
                "The data directory is already owned by another worker process.",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new PaRouterException(
                ErrorCode.StoreInUse,
                "The data directory lock cannot be opened by this user.",
                ex);
        }

        // Il blocco esplicito, che e' l'unico che vale su entrambe le piattaforme dichiarate.
        //
        // FileShare e' un concetto di Windows: li' l'apertura del secondo proprietario fallisce da
        // sola e questa chiamata non viene nemmeno raggiunta. Su Unix quella modalita' non viene
        // applicata fra processi diversi, quindi senza blocco esplicito **due router aprirebbero lo
        // stesso store come proprietari** — e la garanzia di un solo proprietario, su cui si regge
        // tutto il resto, mancherebbe proprio dove nessuno la stava provando.
        //
        // La regione bloccata sta **oltre** i dati del proprietario, non sul primo byte: su Windows
        // il blocco e' vincolante e impedirebbe anche la lettura, mentre i dati del proprietario
        // devono restare leggibili per le diagnosi. E' la stessa ragione per cui l'apertura usa
        // FileShare.Read invece di FileShare.None.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            try
            {
                stream.Lock(OwnershipRegionOffset, 1);
            }
            catch (IOException ex)
            {
                stream.Dispose();

                throw new PaRouterException(
                    ErrorCode.StoreInUse,
                    "The data directory is already owned by another worker process.",
                    ex);
            }
        }
        else
        {
            // Dove il blocco non esiste, la proprieta' esclusiva non e' garantita. Il router si
            // rifiuta invece di aprire lo store senza quella garanzia: due proprietari sullo stesso
            // database sono peggio di un comando che non parte.
            stream.Dispose();

            throw new PaRouterException(
                ErrorCode.ControlUnavailable,
                "This platform has no supported file lock, so exclusive ownership of the data directory cannot be guaranteed.");
        }

        try
        {
            WriteOwner(stream, clock);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        return new StoreLock(stream, paths.LockFile);
    }

    /// <summary>Restituisce la directory all'insieme di quelle libere per questo processo.</summary>
    private static void Release(string key)
    {
        lock (OwnedDirectories)
        {
            OwnedDirectories.Remove(key);
        }
    }

    /// <summary>
    /// Dati del proprietario, utili alle diagnosi. PID e istante di avvio insieme: il solo PID
    /// puo' essere riusato da un processo diverso.
    /// </summary>
    private static void WriteOwner(FileStream stream, IClock clock)
    {
        using System.Diagnostics.Process current = System.Diagnostics.Process.GetCurrentProcess();

        string owner = string.Join(
            '\n',
            "processId=" + current.Id.ToString(CultureInfo.InvariantCulture),
            "startedAtMs=" + new DateTimeOffset(current.StartTime.ToUniversalTime(), TimeSpan.Zero)
                .ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            "acquiredAtMs=" + clock.UnixTimeMilliseconds.ToString(CultureInfo.InvariantCulture));

        byte[] bytes = Encoding.UTF8.GetBytes(owner);

        stream.SetLength(0);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        stream.Dispose();
        Release(System.IO.Path.GetFullPath(Path));
    }
}
