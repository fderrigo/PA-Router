using System.Diagnostics;

namespace PA.Router.Session;

/// <summary>
/// Esecuzione di Git per conto del router, con le difese che un repository di un agente richiede.
///
/// Un agente con la scrittura nel workspace puo' scrivere anche dentro <c>.git</c>. La configurazione
/// di un repository puo' far eseguire programmi a Git — hook, <c>core.fsmonitor</c>, filtri
/// <c>clean</c>, firma dei commit — e un comando lanciato dal router **fuori** dalla sandbox
/// dell'agente diventerebbe il modo di uscirne. Per questo ogni invocazione:
///
/// - ignora la configurazione di sistema e globale dell'utente;
/// - spegne hook, fsmonitor e firma sulla riga di comando, che vince sulla configurazione del
///   repository;
/// - non passa da una shell e non interpola percorsi in una stringa;
/// - non usa comandi che applicano filtri (<see cref="WorkspaceRepository"/> legge i file con
///   <c>hash-object --no-filters</c> invece di <c>add</c>).
///
/// Lo standard error non viene propagato: puo' contenere percorsi completi.
/// </summary>
internal static class GitCommand
{
    /// <summary>Tempo massimo di un comando.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Cartella vuota: radice degli hook e dei modelli di <c>init</c>.</summary>
    internal static string EmptyDirectory
    {
        get
        {
            string directory = Path.Combine(Path.GetTempPath(), "pa-router-git", "empty");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private static string EmptyConfig
    {
        get
        {
            string file = Path.Combine(Path.GetTempPath(), "pa-router-git", "empty.gitconfig");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            if (!File.Exists(file))
            {
                try
                {
                    using FileStream _ = new(file, FileMode.CreateNew);
                }
                catch (IOException)
                {
                    // Creato da un'altra invocazione nello stesso istante.
                }
            }

            return file;
        }
    }

    /// <summary>
    /// Esegue Git e restituisce lo standard output, oppure null se il comando non riesce o Git non
    /// e' installato.
    /// </summary>
    internal static async Task<string?> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? standardInput = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ProcessStartInfo start = new()
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            CreateNoWindow = true,
        };

        foreach (string argument in new[]
        {
            "-c", "core.hooksPath=" + EmptyDirectory,
            "-c", "core.fsmonitor=false",
            "-c", "commit.gpgSign=false",
            "-c", "user.name=PA Router",
            "-c", "user.email=pa-router@localhost",
        })
        {
            start.ArgumentList.Add(argument);
        }

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = EmptyConfig;
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        foreach ((string key, string value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        Process? started = null;

        try
        {
            using Process process = new() { StartInfo = start };

            if (!process.Start())
            {
                return null;
            }

            started = process;

            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Timeout);

            Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(deadline.Token);

            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            // Le pipe di un processo sono sincrone: il token passato alla lettura non la interrompe, e senza questa
            // attesa un git fermo terrebbe il turno oltre la sua scadenza.
            string text = await output.WaitAsync(deadline.Token).ConfigureAwait(false);
            _ = await error.WaitAsync(deadline.Token).ConfigureAwait(false);

            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);

            return process.ExitCode is 0 ? text : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(started);

            return null;
        }
        catch (OperationCanceledException)
        {
            Kill(started);

            throw;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Git non installato.
            return null;
        }
    }

    /// <summary>Termina un comando scaduto con i suoi discendenti: un git fermo non sopravvive al turno.</summary>
    private static void Kill(Process? process)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
