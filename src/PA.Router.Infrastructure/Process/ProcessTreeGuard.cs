using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PA.Router.Abstractions;

namespace PA.Router.Infrastructure.Process;

/// <summary>
/// Contenimento dell'albero dei processi di un programma avviato dal router.
///
/// Su Windows usa un Job Object con <c>KILL_ON_JOB_CLOSE</c>: ogni figlio e nipote nasce dentro il
/// job, e la chiusura dell'handle termina l'intero albero anche se il router muore. E' l'unico
/// meccanismo che regge il caso che conta davvero — un processo che ne avvia altri e poi esce,
/// lasciando i nipoti vivi (03-specs/ADAPTERS.md, «protezione albero processi certificata»).
///
/// Non e' una sandbox: un Job Object limita la vita dei processi, non cio' che possono leggere o
/// scrivere. Il confinamento del filesystem resta una proprieta' del programma ufficiale, da
/// certificare a parte.
///
/// Fuori da Windows la classe si dichiara non disponibile invece di fingere: <see cref="IsSupported"/>
/// e' falso e il chiamante sa che quel controllo non e' stato ottenuto.
/// </summary>
public sealed class ProcessTreeGuard : IDisposable
{
    private readonly SafeJobHandle? job;

    private ProcessTreeGuard(SafeJobHandle? job) => this.job = job;

    /// <summary>Vero se la piattaforma offre il contenimento certificabile.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Nome del meccanismo in uso, per le evidenze.</summary>
    public static string Mechanism => IsSupported ? "windows-job-object" : "unsupported";

    /// <summary>
    /// Crea un contenitore. Su una piattaforma senza supporto restituisce un contenitore inerte:
    /// <see cref="Assign"/> lancia, invece di lasciar credere che l'albero sia contenuto.
    /// </summary>
    public static ProcessTreeGuard Create()
    {
        // Il controllo di piattaforma e' esplicito: l'analizzatore deve poterlo vedere, e chi legge
        // deve sapere che fuori da Windows non esiste alcun contenimento.
        return OperatingSystem.IsWindows()
            ? new ProcessTreeGuard(CreateJob())
            : new ProcessTreeGuard(null);
    }

    /// <summary>Assegna un processo al contenitore, con tutti i suoi discendenti.</summary>
    public void Assign(System.Diagnostics.Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (job is null || !OperatingSystem.IsWindows())
        {
            throw new PaRouterException(
                ErrorCode.ControlUnavailable,
                "Process tree containment is not available on this platform.")
                .WithMetadata("mechanism", Mechanism);
        }

        AssignCore(job, process);
    }

    /// <summary>
    /// Termina l'albero. Idempotente: un albero gia' terminato non e' un errore, e un processo che
    /// esce da solo lascia il job vuoto.
    /// </summary>
    public void Terminate()
    {
        if (job is null || job.IsInvalid || !OperatingSystem.IsWindows())
        {
            return;
        }

        TerminateCore(job);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // La chiusura dell'handle termina cio' che resta: e' la garanzia che regge anche a un
        // router ucciso senza preavviso.
        job?.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private static SafeJobHandle CreateJob()
    {
        SafeJobHandle handle = CreateJobObjectW(IntPtr.Zero, null);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        JobObjectExtendedLimitInformation information = default;
        information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);

            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return handle;
    }

    [SupportedOSPlatform("windows")]
    private static void AssignCore(SafeJobHandle job, System.Diagnostics.Process process)
    {
        if (!AssignProcessToJobObject(job, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TerminateCore(SafeJobHandle job)
    {
        if (!TerminateJobObject(job, 1))
        {
            int error = Marshal.GetLastWin32Error();

            // 5 = accesso negato quando il job e' gia' chiuso: non e' una condizione da segnalare.
            if (error is not 5)
            {
                throw new Win32Exception(error);
            }
        }
    }

    private const int JobObjectExtendedLimitInformationClass = 9;

    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    // DllImport e non LibraryImport: il generatore del secondo richiede codice unsafe, che questo
    // repository non abilita. Le firme sono quattro e stabili, quindi il vantaggio non varrebbe
    // il costo di rendere unsafe l'intero modulo.
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        IntPtr information,
        uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    /// <summary>Handle del job, chiuso in modo deterministico.</summary>
    private sealed class SafeJobHandle : SafeHandle
    {
        public SafeJobHandle()
            : base(IntPtr.Zero, ownsHandle: true)
        {
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
