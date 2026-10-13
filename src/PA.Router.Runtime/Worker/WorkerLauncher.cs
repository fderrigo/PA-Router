using System.Diagnostics;
using PA.Router.Abstractions;
using PA.Router.Runtime.Ipc;

namespace PA.Router.Runtime.Worker;

/// <summary>
/// Avvio automatico del worker dal lato client. Al primo comando la CLI cerca il worker della
/// stessa versione; se non risponde lo avvia in background senza finestra aggiuntiva.
///
/// Non viene mai terminato un processo esistente: se un worker possiede gia' la directory dati,
/// il client si limita a collegarsi.
/// </summary>
public static class WorkerLauncher
{
    /// <summary>Sottocomando interno usato per avviare il worker.</summary>
    public static readonly string[] WorkerArguments = ["runtime", "worker"];

    /// <summary>
    /// Attesa del solo sondaggio iniziale. Se nessun worker e' in ascolto la pipe non risponde
    /// subito: attendere qui il timeout completo renderebbe lento ogni primo comando.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Restituisce un client collegato al worker della directory dati, avviandolo se serve.
    /// </summary>
    /// <param name="dataDirectory">Directory dati da servire.</param>
    /// <param name="executablePath">Eseguibile <c>pa</c> da avviare, della stessa versione.</param>
    /// <param name="connectTimeout">Attesa massima per ogni tentativo di connessione.</param>
    public static async Task<IpcClient> ConnectOrStartAsync(
        string dataDirectory,
        string executablePath,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        try
        {
            return await IpcClient.ConnectAsync(dataDirectory, ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PaRouterException ex) when (ex.Code == ErrorCode.StoreUnavailable)
        {
            // Nessun worker in ascolto: e' il caso normale del primo comando.
        }

        Start(dataDirectory, executablePath);

        return await IpcClient.ConnectAsync(dataDirectory, connectTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void Start(string dataDirectory, string executablePath)
    {
        ProcessStartInfo startInfo = new(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
        };

        foreach (string argument in WorkerArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--data");
        startInfo.ArgumentList.Add(dataDirectory);

        try
        {
            using Process? process = Process.Start(startInfo);

            if (process is null)
            {
                throw new PaRouterException(
                    ErrorCode.StoreUnavailable,
                    "The local runtime worker could not be started.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new PaRouterException(
                ErrorCode.StoreUnavailable,
                "The local runtime worker executable could not be started.",
                ex);
        }
    }
}
