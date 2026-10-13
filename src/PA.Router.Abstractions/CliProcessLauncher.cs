using System.Diagnostics;
using System.Text;
namespace PA.Router.Abstractions;

/// <summary>Righe lette da un programma avviato, con lo stato di uscita.</summary>
/// <param name="ExitCode">Codice di uscita, quando il processo e' terminato.</param>
/// <param name="Truncated">Vero se lo stream ha superato i limiti dichiarati.</param>
/// <param name="ErrorLines">
/// Righe scritte su standard error. Solo il conteggio: il contenuto puo' portare percorsi, nomi
/// utente o frammenti del prompt, e conservarlo aprirebbe un varco in un confine che tutto il
/// resto difende. Il numero basta a dire che il programma si e' lamentato.
/// </param>
public sealed record CliOutcome(int ExitCode, bool Truncated, int ErrorLines);

/// <summary>
/// Avvio dei programmi ufficiali.
///
/// Sta nelle astrazioni perche' le regole sono comuni ai due adapter e scriverle due volte
/// significherebbe poterne sbagliare una sola: nessuna shell, argomenti come lista e mai come
/// stringa interpolata, prompt su standard input, ambiente ridotto a una allowlist, working
/// directory canonica. In dettaglio: nessuna shell, argomenti come lista e mai come stringa interpolata, prompt su
/// standard input, ambiente ridotto a una allowlist, working directory canonica.
///
/// Lo standard error non viene propagato: se ne conserva solo il conteggio delle righe. Un
/// programma puo' scriverci percorsi, nomi utente o frammenti del prompt, e un log integrale
/// sarebbe il modo piu' semplice per far uscire dati da un confine che tutto il resto difende
/// (03-specs/ADAPTERS.md).
/// </summary>
public sealed class CliProcessLauncher
{
    /// <summary>Righe massime lette da uno stream prima di dichiararlo troncato.</summary>
    public const int MaxLines = 20_000;

    /// <summary>Byte massimi di una singola riga.</summary>
    public const int MaxLineBytes = 1024 * 1024;

    /// <summary>
    /// Variabili necessarie a far partire un programma: percorsi di sistema, home dell'utente e
    /// localizzatori del runtime. Sono cio' che serve a trovare l'interprete — Node per le CLI
    /// ufficiali, .NET per un programma gestito — e nulla che riguardi credenziali. Tutto il resto
    /// dell'ambiente del router non viene ereditato.
    /// </summary>
    public static IReadOnlyList<string> BaseEnvironmentAllowlist { get; } =
    [
        "PATH", "PATHEXT", "SystemRoot", "windir", "COMSPEC",
        "TEMP", "TMP", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
        "ProgramFiles", "ProgramData",
        "DOTNET_ROOT", "DOTNET_ROOT(x86)",
        "LANG", "LC_ALL",
    ];

    /// <summary>
    /// Avvia il programma e consegna le righe di standard output a <paramref name="onLine"/>.
    /// </summary>
    /// <param name="guards">Contenimento dell'albero dei processi offerto dalla piattaforma.</param>
    /// <param name="executable">Percorso assoluto del programma ufficiale.</param>
    /// <param name="arguments">Argomenti gia' certificati; nessuno arriva dal modello.</param>
    /// <param name="workingDirectory">Directory di lavoro canonica.</param>
    /// <param name="prompt">Payload autorizzato, scritto su standard input.</param>
    /// <param name="environment">Variabili aggiuntive consentite, oltre alla allowlist.</param>
    /// <param name="onLine">Riceve ogni riga di standard output.</param>
    /// <param name="cancellationToken">Annullamento: termina l'albero dei processi.</param>
    public static async Task<CliOutcome> RunAsync(
        IProcessGuardFactory guards,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string prompt,
        IReadOnlyDictionary<string, string?> environment,
        Func<string, Task> onLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guards);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(onLine);

        if (!Path.IsPathRooted(executable))
        {
            // Un nome risolto dal PATH sarebbe un programma scelto dall'ambiente, non dal registro.
            throw new PaRouterException(ErrorCode.ControlUnavailable, "The official program must be an absolute path.")
                .WithMetadata("reason", "executable_not_absolute");
        }

        ProcessStartInfo start = new()
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // L'ambiente parte vuoto e viene riempito dalla allowlist: cosi' una variabile di API di un
        // altro provider non entra per inerzia.
        start.Environment.Clear();

        foreach (string name in BaseEnvironmentAllowlist)
        {
            if (Environment.GetEnvironmentVariable(name) is string value)
            {
                start.Environment[name] = value;
            }
        }

        foreach ((string name, string? value) in environment)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
                continue;
            }

            start.Environment[name] = value;
        }

        using Process process = new() { StartInfo = start };

        if (!process.Start())
        {
            throw new PaRouterException(ErrorCode.TargetUnavailable, "The official program did not start.");
        }

        using IProcessGuard guard = guards.Create();

        if (guards.IsSupported)
        {
            // Contenimento dell'albero: un programma che ne avvia altri non deve poter lasciare
            // nipoti orfani dopo una cancellazione.
            guard.Assign(process);
        }

        await using (cancellationToken.Register(() => Stop(process, guard)))
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();

            // Standard error viene drenato insieme a standard output: non leggerlo riempirebbe il
            // buffer del sistema operativo e bloccherebbe il programma.
            Task<int> errorLines = DrainAsync(process.StandardError, cancellationToken);

            bool truncated = false;
            int lines = 0;

            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (line is null)
                {
                    break;
                }

                if (++lines > MaxLines || Encoding.UTF8.GetByteCount(line) > MaxLineBytes)
                {
                    truncated = true;
                    break;
                }

                await onLine(line).ConfigureAwait(false);
            }

            if (truncated)
            {
                // Prima si ferma il programma, poi si aspetta il conteggio delle righe di errore: un programma che
                // inonda l'uscita e poi resta fermo non chiude mai lo standard error, e l'attesa non finirebbe.
                Stop(process, guard);
            }

            int errors = await errorLines.ConfigureAwait(false);

            if (truncated)
            {
                return new CliOutcome(-1, Truncated: true, errors);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return new CliOutcome(process.ExitCode, Truncated: false, errors);
        }
    }

    /// <summary>
    /// Ferma il programma: prima il contenitore, che copre anche i discendenti, poi il processo
    /// stesso quando il contenitore non esiste.
    /// </summary>
    private static void Stop(Process process, IProcessGuard guard)
    {
        guard.Terminate();

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    /// <summary>
    /// Conta le righe di standard error senza conservarle. Il numero e' un'informazione utile a
    /// una diagnosi; il contenuto no.
    /// </summary>
    private static async Task<int> DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        int lines = 0;

        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                lines++;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }

        return lines;
    }
}
