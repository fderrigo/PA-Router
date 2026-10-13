using PA.Router.Abstractions;
using PA.Router.Infrastructure.Storage;

namespace PA.Router.Infrastructure.Content;

/// <summary>Esito di una passata di sweep, da registrare come evento aggregato di retention.</summary>
/// <param name="RemovedOrphans">File orfani rimossi.</param>
/// <param name="SkippedRecent">File orfani lasciati perche' piu' recenti della soglia.</param>
/// <param name="MissingReferenced">Blob referenziati dal database ma non presenti su disco.</param>
public readonly record struct SweepResult(int RemovedOrphans, int SkippedRecent, int MissingReferenced);

/// <summary>
/// Pulizia dei file di contenuto. Rimuove soltanto i file non referenziati dal database e piu'
/// vecchi della soglia: un blob appena scritto ma non ancora committato non deve sparire sotto
/// la transazione che lo sta per referenziare.
///
/// La direzione opposta non viene mai risolta cancellando righe: un blob referenziato ma assente
/// viene segnalato come CONTENT_UNAVAILABLE all'avvio, non nascosto.
/// </summary>
public sealed class ContentSweeper
{
    /// <summary>Eta' minima di un orfano prima della rimozione.</summary>
    public static readonly TimeSpan DefaultOrphanAge = TimeSpan.FromHours(24);

    private readonly StorePaths paths;
    private readonly IClock clock;
    private readonly TimeSpan orphanAge;

    public ContentSweeper(StorePaths paths, IClock clock)
        : this(paths, clock, DefaultOrphanAge)
    {
    }

    public ContentSweeper(StorePaths paths, IClock clock, TimeSpan orphanAge)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);

        this.paths = paths;
        this.clock = clock;
        this.orphanAge = orphanAge;
    }

    /// <param name="referencedRelativePaths">Path relativi presenti in <c>content_blobs</c>.</param>
    public SweepResult Sweep(IReadOnlySet<string> referencedRelativePaths)
    {
        ArgumentNullException.ThrowIfNull(referencedRelativePaths);

        if (!Directory.Exists(paths.ContentDirectory))
        {
            return new SweepResult(0, 0, referencedRelativePaths.Count);
        }

        int removed = 0;
        int skipped = 0;
        DateTimeOffset threshold = clock.UtcNow - orphanAge;
        HashSet<string> present = new(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(paths.ContentDirectory))
        {
            string relativePath = StorePaths.ContentPrefix + Path.GetFileName(file);
            bool referenced = referencedRelativePaths.Contains(relativePath);

            if (referenced)
            {
                present.Add(relativePath);
                continue;
            }

            if (File.GetLastWriteTimeUtc(file) > threshold.UtcDateTime)
            {
                skipped++;
                continue;
            }

            File.Delete(file);
            removed++;
        }

        int missing = referencedRelativePaths.Count(path => !present.Contains(path));
        return new SweepResult(removed, skipped, missing);
    }
}
