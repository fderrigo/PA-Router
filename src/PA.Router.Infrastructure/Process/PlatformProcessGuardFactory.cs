using PA.Router.Abstractions;

namespace PA.Router.Infrastructure.Process;

/// <summary>
/// Fabbrica dei contenitori dell'albero dei processi, scelta secondo la piattaforma: Job Object su
/// Windows, cgroup v2 su Linux.
///
/// Dichiara il meccanismo invece di dedurlo. Dove nessuno dei due e' disponibile —
/// una gerarchia cgroup v1, un cgroup non delegato, un sistema operativo diverso — la fabbrica si
/// dichiara non supportata e l'adapter che richiede confinamento non parte. Non esiste un
/// contenimento di ripiego: un gruppo di processi non regge il caso che conta, perche' un
/// discendente puo' sfilarsene con <c>setpgid</c>, e spacciarlo per contenimento sarebbe peggio
/// che non averlo.
/// </summary>
public sealed class PlatformProcessGuardFactory : IProcessGuardFactory
{
    /// <summary>Istanza condivisa: la fabbrica non ha stato.</summary>
    public static PlatformProcessGuardFactory Instance { get; } = new();

    /// <inheritdoc />
    public bool IsSupported => ProcessTreeGuard.IsSupported || LinuxCgroupGuard.IsSupported;

    /// <inheritdoc />
    public string Mechanism =>
        ProcessTreeGuard.IsSupported ? ProcessTreeGuard.Mechanism
        : LinuxCgroupGuard.IsSupported ? LinuxCgroupGuard.Mechanism
        : "unsupported";

    /// <summary>
    /// Raccoglie i contenitori lasciati da un'esecuzione precedente. Su Windows non serve — la
    /// chiusura dell'handle del job termina l'albero anche se il router muore — mentre un cgroup
    /// sopravvive al processo che lo ha creato. Restituisce quanti ne ha rimossi.
    /// </summary>
    public static int CleanupOrphans() => LinuxCgroupGuard.CleanupOrphans();

    /// <inheritdoc />
    public IProcessGuard Create() =>
        ProcessTreeGuard.IsSupported
            ? new WindowsGuard(ProcessTreeGuard.Create())
            : new LinuxGuard(LinuxCgroupGuard.Create());

    private sealed class WindowsGuard(ProcessTreeGuard inner) : IProcessGuard
    {
        public void Assign(System.Diagnostics.Process process) => inner.Assign(process);

        public void Terminate() => inner.Terminate();

        public void Dispose() => inner.Dispose();
    }

    private sealed class LinuxGuard(LinuxCgroupGuard inner) : IProcessGuard
    {
        public void Assign(System.Diagnostics.Process process) => inner.Assign(process);

        public void Terminate() => inner.Terminate();

        public void Dispose() => inner.Dispose();
    }
}
