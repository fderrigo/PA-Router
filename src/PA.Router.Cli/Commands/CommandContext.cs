using System.CommandLine;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Commands;

/// <summary>
/// Servizi condivisi dai comandi: risoluzione della directory dati e resa degli errori.
/// L'output di errore riporta codice, percorso e riga; mai il valore che ha causato il problema.
/// </summary>
internal static class CommandContext
{
    /// <summary>Opzione comune che indica la directory dati.</summary>
    internal static Option<DirectoryInfo?> DataOption() => new("--data")
    {
        Description = Ui.Text("Data directory to use. Defaults to the per-user location."),
    };

    /// <summary>Directory dati effettiva del comando.</summary>
    internal static string ResolveDataDirectory(ParseResult parseResult, Option<DirectoryInfo?>? dataOption = null)
    {
        if ((dataOption is null ? parseResult.GetValue<DirectoryInfo?>("--data") : parseResult.GetValue(dataOption))?.FullName is string chosen)
        {
            return chosen;
        }

        // L'allowlist dell'ambiente e' chiusa e documentata (03-specs/CONFIG.md), ma fino a S52 nessuno la leggeva:
        // `PA_ROUTER_DATA_DIR` era una promessa scritta e mai mantenuta. Una variabile presente e non valida e' un
        // errore, non un silenzio: ignorarla farebbe credere all'operatore di aver spostato lo store.
        List<ValidationIssue> issues = [];

        EnvironmentOverrides overrides = EnvironmentOverrides.Read(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [EnvironmentOverrides.DataDirectoryVariable] =
                    Environment.GetEnvironmentVariable(EnvironmentOverrides.DataDirectoryVariable),
                [EnvironmentOverrides.LogLevelVariable] =
                    Environment.GetEnvironmentVariable(EnvironmentOverrides.LogLevelVariable),
            },
            issues);

        if (issues.Count > 0)
        {
            throw new PaRouterException(ErrorCode.InputInvalid, issues[0].Message)
                .WithMetadata("reason", "environment_override_invalid");
        }

        return overrides.DataDirectory ?? DefaultPaths.DataDirectory;
    }

    /// <summary>Stampa i problemi di validazione e restituisce l'exit code della CLI.</summary>
    internal static int ReportIssues(ParseResult parseResult, IReadOnlyList<ValidationIssue> issues)
    {
        TextWriter error = parseResult.InvocationConfiguration.Error;

        error.WriteLine(Ui.Text("The configuration is not valid:"));

        foreach (ValidationIssue issue in issues)
        {
            // Il messaggio della validazione e' una frase fissa e senza valori: si traduce (23 settembre 2026).
            error.WriteLine("  " + (issue with { Message = Ui.Text(issue.Message) }));
        }

        return ExitCodes.InvalidInput;
    }

    /// <summary>Stampa un errore tipizzato e restituisce l'exit code della CLI.</summary>
    internal static int ReportError(ParseResult parseResult, PaRouterException exception)
    {
        parseResult.InvocationConfiguration.Error.WriteLine(
            exception.Code.ToWire() + ": " + exception.Message);

        return ExitCodes.ForStorage(exception.Code);
    }
}
