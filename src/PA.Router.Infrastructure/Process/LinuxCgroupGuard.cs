using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PA.Router.Abstractions;

namespace PA.Router.Infrastructure.Process;

/// <summary>
/// Contenimento dell'albero dei processi su Linux con cgroup v2.
///
/// E' l'analogo del Job Object di Windows: un processo spostato in un cgroup non puo' uscirne, e
/// ogni suo discendente ci nasce dentro. <c>cgroup.kill</c> termina l'intero sottoalbero in un
/// colpo solo, compresi i nipoti rimasti vivi dopo la morte del padre — il caso che i gruppi di
/// processi non reggono, perche' un discendente puo' chiamare <c>setpgid</c> e sfilarsi.
///
/// Due differenze rispetto a Windows vanno dette, non nascoste:
///
/// <list type="bullet">
///   <item>Il Job Object termina l'albero anche se il router muore di colpo, perche' la garanzia
///   e' legata alla chiusura dell'handle. Un cgroup no: sopravvive al router. Per questo
///   <see cref="CleanupOrphans"/> viene chiamata all'avvio e raccoglie i contenitori lasciati da
///   un'esecuzione precedente.</item>
///   <item>L'assegnazione avviene subito dopo l'avvio, non prima: un discendente creato nei
///   microsecondi fra <c>exec</c> e la scrittura di <c>cgroup.procs</c> resterebbe fuori. La
///   finestra e' reale e si dichiara.</item>
/// </list>
///
/// Non e' una sandbox: un cgroup limita la vita dei processi, non cio' che leggono o scrivono.
/// </summary>
public sealed class LinuxCgroupGuard : IDisposable
{
    private const string CgroupRoot = "/sys/fs/cgroup";

    private const int SIGKILL = 9;

    private const string ContainerPrefix = "pa-router-";

    private readonly string? directory;

    private bool disposed;

    private LinuxCgroupGuard(string? directory) => this.directory = directory;

    /// <summary>Vero se questo sistema offre un cgroup v2 in cui il router puo' creare figli.</summary>
    public static bool IsSupported => OperatingSystem.IsLinux() && ParentDirectory() is not null;

    /// <summary>Nome del meccanismo, per le evidenze.</summary>
    public static string Mechanism => IsSupported ? "linux-cgroup2" : "unsupported";

    /// <summary>
    /// Crea un contenitore. Dove il cgroup non e' delegato restituisce un contenitore inerte:
    /// <see cref="Assign"/> lancia, invece di lasciar credere che l'albero sia contenuto.
    /// </summary>
    public static LinuxCgroupGuard Create()
    {
        if (!OperatingSystem.IsLinux())
        {
            return new LinuxCgroupGuard(null);
        }

        string? parent = ParentDirectory();

        if (parent is null)
        {
            return new LinuxCgroupGuard(null);
        }

        string name = string.Create(
            CultureInfo.InvariantCulture,
            $"{ContainerPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}");

        string path = Path.Combine(parent, name);

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // La delega c'era alla lettura e non c'e' piu' alla scrittura: contenitore inerte, non
            // un'eccezione. Chi richiede il controllo lo scopre da Assign.
            return new LinuxCgroupGuard(null);
        }

        return new LinuxCgroupGuard(path);
    }

    /// <summary>
    /// Rimuove i contenitori lasciati da esecuzioni precedenti del router: un cgroup sopravvive al
    /// processo che lo ha creato, e senza questa raccolta i residui si accumulerebbero.
    /// Il nome porta il PID del router che lo ha creato; se quel processo non esiste piu' il
    /// contenitore viene terminato e cancellato.
    /// </summary>
    public static int CleanupOrphans()
    {
        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }

        string? parent = ParentDirectory();

        if (parent is null)
        {
            return 0;
        }

        int removed = 0;

        foreach (string candidate in SafeEnumerate(parent))
        {
            string name = Path.GetFileName(candidate);

            if (!name.StartsWith(ContainerPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryOwnerProcessId(name, out int owner) || owner == Environment.ProcessId)
            {
                continue;
            }

            if (IsAlive(owner))
            {
                // Un altro router e' vivo e possiede quel contenitore: non e' un residuo.
                continue;
            }

            KillMembers(candidate);

            if (TryRemoveDirectory(candidate))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Assegna un processo al contenitore, con tutti i suoi discendenti.</summary>
    public void Assign(System.Diagnostics.Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (directory is null || !OperatingSystem.IsLinux())
        {
            throw new PaRouterException(
                ErrorCode.ControlUnavailable,
                "Process tree containment is not available on this platform.")
                .WithMetadata("mechanism", Mechanism);
        }

        try
        {
            File.WriteAllText(
                Path.Combine(directory, "cgroup.procs"),
                process.Id.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PaRouterException(
                ErrorCode.ControlUnavailable,
                "The process could not be moved into the containment cgroup.",
                ex)
                .WithMetadata("mechanism", Mechanism);
        }
    }

    /// <summary>
    /// Termina l'albero. Idempotente: un contenitore gia' vuoto non e' un errore.
    /// </summary>
    public void Terminate()
    {
        if (directory is null || !OperatingSystem.IsLinux())
        {
            return;
        }

        KillMembers(directory);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (directory is null || !OperatingSystem.IsLinux())
        {
            return;
        }

        KillMembers(directory);

        // La cancellazione riesce solo a cgroup vuoto. Il kernel libera i membri in modo
        // asincrono, quindi si riprova per un tempo limitato invece di lasciare il residuo.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (TryRemoveDirectory(directory))
            {
                return;
            }

            Thread.Sleep(25);
        }
    }

    /// <summary>
    /// Directory in cui il router puo' creare contenitori: il proprio cgroup, quando la gerarchia
    /// e' v2 ed e' scrivibile. Su una gerarchia v1, dentro un namespace non delegato o con il
    /// filesystem in sola lettura restituisce <c>null</c>, e il controllo si dichiara assente.
    /// </summary>
    private static string? ParentDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        // Su cgroup v1 questo file non esiste: e' il modo documentato per distinguere le due
        // gerarchie senza leggere /proc/mounts.
        if (!File.Exists(Path.Combine(CgroupRoot, "cgroup.controllers")))
        {
            return null;
        }

        string? own = OwnCgroupPath();

        if (own is null)
        {
            return null;
        }

        string directory = own.Length is 0 or 1
            ? CgroupRoot
            : Path.Combine(CgroupRoot, own.TrimStart('/'));

        return Directory.Exists(directory) && CanCreateChild(directory) ? directory : null;
    }

    /// <summary>
    /// Percorso del cgroup del processo corrente, dalla riga <c>0::</c> di /proc/self/cgroup:
    /// nella gerarchia unificata e' l'unica riga, e il prefisso e' fisso.
    /// </summary>
    private static string? OwnCgroupPath()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/self/cgroup"))
            {
                if (line.StartsWith("0::", StringComparison.Ordinal))
                {
                    return line[3..];
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Verifica la delega creando e cancellando davvero una directory: i permessi del filesystem
    /// non bastano, perche' il kernel puo' rifiutare la creazione in un cgroup non delegato.
    /// </summary>
    private static bool CanCreateChild(string parent)
    {
        string probe = Path.Combine(
            parent,
            string.Create(CultureInfo.InvariantCulture, $"{ContainerPrefix}probe-{Guid.NewGuid():N}"));

        try
        {
            Directory.CreateDirectory(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        TryRemoveDirectory(probe);
        return true;
    }

    /// <summary>
    /// Termina i membri del contenitore. <c>cgroup.kill</c> (kernel 5.14 e successivi) e' atomico e
    /// non lascia scampo ai processi creati durante la terminazione; dove manca si ripiega sui PID
    /// letti da <c>cgroup.procs</c>, che invece una fork in corso puo' sfuggire.
    /// </summary>
    private static void KillMembers(string directory)
    {
        string kill = Path.Combine(directory, "cgroup.kill");

        if (File.Exists(kill))
        {
            try
            {
                File.WriteAllText(kill, "1");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ripiego sui PID: meglio una terminazione imperfetta di un albero lasciato vivo.
            }
        }

        foreach (int pid in Members(directory))
        {
            _ = NativeKill(pid, SIGKILL);
        }
    }

    private static IEnumerable<int> Members(string directory)
    {
        string path = Path.Combine(directory, "cgroup.procs");

        string[] lines;

        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string line in lines)
        {
            if (int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                yield return pid;
            }
        }
    }

    private static bool TryOwnerProcessId(string containerName, out int processId)
    {
        processId = 0;

        string rest = containerName[ContainerPrefix.Length..];
        int separator = rest.IndexOf('-', StringComparison.Ordinal);

        return separator > 0
            && int.TryParse(
                rest.AsSpan(0, separator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out processId);
    }

    /// <summary>Il segnale 0 non viene consegnato: verifica soltanto che il processo esista.</summary>
    private static bool IsAlive(int processId) => NativeKill(processId, 0) == 0;

    private static IEnumerable<string> SafeEnumerate(string parent)
    {
        try
        {
            return Directory.EnumerateDirectories(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryRemoveDirectory(string path)
    {
        try
        {
            // Un cgroup si cancella con rmdir e non e' mai ricorsivo: i file di controllo dentro
            // non sono file veri e Delete(recursive) fallirebbe.
            Directory.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int NativeKill(int processId, int signal)
    {
        if (!OperatingSystem.IsLinux())
        {
            return -1;
        }

        try
        {
            return Kill(processId, signal);
        }
        catch (DllNotFoundException)
        {
            return -1;
        }
        catch (EntryPointNotFoundException)
        {
            return -1;
        }
    }

    // DllImport e non LibraryImport, per la stessa ragione di ProcessTreeGuard: il generatore
    // richiede codice unsafe, che questo repository non abilita, e la firma e' una sola.
    [SupportedOSPlatform("linux")]
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int sig);
}
