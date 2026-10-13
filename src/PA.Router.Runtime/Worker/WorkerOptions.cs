using PA.Router.Abstractions;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Crypto;

namespace PA.Router.Runtime.Worker;

/// <summary>
/// Parametri del worker locale. I default vengono da 03-specs/LOCAL-RUNTIME.md; i test li
/// abbassano per non attendere due minuti.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>Attesa di inattivita' prima dell'arresto automatico.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Attesa massima in coda del writer prima di STORE_UNAVAILABLE.</summary>
    public static readonly TimeSpan DefaultWriterEnqueueTimeout = TimeSpan.FromSeconds(5);

    /// <param name="dataDirectory">Directory dati posseduta dal worker.</param>
    public WorkerOptions(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = dataDirectory;
    }

    /// <summary>Directory dati posseduta dal worker.</summary>
    public string DataDirectory { get; }

    /// <summary>Inattivita' oltre la quale il worker si arresta.</summary>
    public TimeSpan IdleTimeout { get; init; } = DefaultIdleTimeout;

    /// <summary>Deadline di accodamento del writer.</summary>
    public TimeSpan WriterEnqueueTimeout { get; init; } = DefaultWriterEnqueueTimeout;

    /// <summary>Intervallo di controllo dell'inattivita'.</summary>
    public TimeSpan IdlePollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Protector delle chiavi. Sostituito nei test su piattaforme senza DPAPI.</summary>
    public IKeyProtector KeyProtector { get; init; } = PlatformKeyProtector.Create();

    /// <summary>
    /// Intervallo fra due passaggi di svuotamento della coda di indicizzazione. Il turno ne
    /// richiede comunque uno subito: questo copre i lavori rimasti e quelli riaccodati.
    /// </summary>
    public TimeSpan MemoryDrainInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Orologio iniettato.</summary>
    public IClock Clock { get; init; } = SystemClock.Instance;
}
