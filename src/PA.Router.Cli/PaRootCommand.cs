using System.CommandLine;
using System.CommandLine.Help;
using PA.Router.Abstractions;
using PA.Router.Cli.Commands;
using PA.Router.Cli.Repl;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Storage;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli;

/// <summary>
/// Costruzione del comando radice. Un solo parser per l'intera CLI: nessun secondo parser e
/// nessuna interfaccia browser.
///
/// Senza argomenti e con un terminale interattivo si apre il REPL; in automazione lo stesso
/// invito sarebbe una trappola, quindi la CLI risponde con un errore d'uso.
/// </summary>
internal static class PaRootCommand
{
    internal static string Description =>
        Ui.Text("PA Router - local orchestration of official agent CLIs and local models.");

    internal static RootCommand Create()
    {
        RootCommand root = new(Description)
        {
            InitCommand.Create(CommandContext.DataOption()),
            ConfigCommand.Create(CommandContext.DataOption()),
            RunCommand.CreateRun(CommandContext.DataOption()),
            RunCommand.CreateExplain(CommandContext.DataOption()),
            ToolsCommand.Create(),
            DiagnosticCommands.CreateDoctor(CommandContext.DataOption()),
            DiagnosticCommands.CreateRegistry(CommandContext.DataOption()),
            DiagnosticCommands.CreatePolicy(CommandContext.DataOption()),
            DiagnosticCommands.CreateAudit(CommandContext.DataOption()),
            MemoryCommand.Create(CommandContext.DataOption()),
            ExtensionCommands.CreatePlugin(CommandContext.DataOption()),
            ExtensionCommands.CreateSkill(CommandContext.DataOption()),
            SessionCommand.Create(CommandContext.DataOption()),
            MaintenanceCommands.Create(CommandContext.DataOption()),
            ServeCommand.Create(CommandContext.DataOption()),
            RuntimeCommand.Create(),
            CompletionCommand.Create(),
            AttachCommand.Create(CommandContext.DataOption()),
            ScriptCommand.Create(CommandContext.DataOption()),
            SetupCommand.Create(CommandContext.DataOption()),
        };

        // La libreria non traduce la descrizione di --help in italiano: restava «Show help and usage information» in
        // mezzo a un aiuto tutto italiano (24 settembre 2026). L'opzione e' ricorsiva, quindi basta sulla radice.
        foreach (HelpOption help in root.Options.OfType<HelpOption>())
        {
            help.Description = Ui.Text("help.option");
        }

        Option<DirectoryInfo?> dataOption = CommandContext.DataOption();
        root.Options.Add(dataOption);

        // «Continua l'ultima» e' la ripresa piu' frequente e la piu' scomoda da scrivere: senza di
        // essa si copia un GUID da un elenco per tornare alla conversazione di dieci minuti fa.
        Option<bool> continueOption = new("--continue")
        {
            Description = Ui.Text("Resume the most recent local session instead of starting a new one."),
        };

        root.Options.Add(continueOption);

        // Chi ha piu' etichette sceglie quale usare per questa conversazione, senza cambiare la predefinita dal setup
        // (23 settembre 2026): prima il REPL partiva sempre con quella, e `pa run` era l'unico a poterne indicare un'altra.
        Option<string?> profileOption = new("--profile")
        {
            Description = Ui.Text("repl.profileOption"),
        };

        root.Options.Add(profileOption);

        root.SetAction((parseResult, cancellationToken) =>
            StartReplAsync(parseResult, dataOption, continueOption, profileOption, cancellationToken));

        return root;
    }

    private static async Task<int> StartReplAsync(
        ParseResult parseResult,
        Option<DirectoryInfo?> dataOption,
        Option<bool> continueOption,
        Option<string?> profileOption,
        CancellationToken cancellationToken)
    {
        if (!Interactive)
        {
            ConfigurationReport report = ConfigurationHealth.Inspect(CommandContext.ResolveDataDirectory(parseResult, dataOption), SystemClock.Instance);
            Output.CliJson.Write(parseResult.InvocationConfiguration.Output,
                Output.CliJson.Error("setup", ErrorCode.InputInvalid,
                    Ui.Text("No command specified. Setup requires an interactive terminal. Configuration: ") + report.Readiness
                    + Ui.Text(". Run 'pa config show --json' for details, 'pa config migrate' for a migration preview, or 'pa' in a terminal. Run 'pa --help' for commands.")));

            return ExitCodes.UsageError;
        }

        using ReplTerminalScope terminal = new(parseResult.InvocationConfiguration.Output);

        // Il controllo della configurazione precede tutto: senza, l'avvio si fermava su un elenco di
        // problemi e usciva, lasciando all'operatore il compito di indovinare quale file scrivere.
        ConfigurationReport configuration = ReplSession.EnsureConfigured(
            CommandContext.ResolveDataDirectory(parseResult, dataOption),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error);

        if (terminal.Interruptions.ExitRequested) { return ExitCodes.Success; }

        if (configuration.Snapshot is not ConfigurationSnapshot snapshot)
        {
            // Il configuratore non ha portato la configurazione a uno stato utilizzabile — l'ha
            // chiuso l'operatore, oppure resta un problema che non si corregge rispondendo a una
            // domanda. L'uscita e' quella di sempre, con i problemi elencati.
            return CommandContext.ReportIssues(parseResult, configuration.Issues);
        }
        string? resumeSessionId = null;

        if (parseResult.GetValue(continueOption))
        {
            try
            {
                // Lettura, quindi senza possedere lo store: con un worker vivo la directory e' sua, e
                // chiedere la proprieta' per sapere qual e' l'ultima conversazione la renderebbe
                // disponibile solo a worker spento.
                await using RouterStack readOnly = RouterStack.Open(snapshot, ownsStore: false);

                if (SessionCatalog.Latest(readOnly) is SessionSummary latest)
                {
                    resumeSessionId = latest.SessionId;

                    parseResult.InvocationConfiguration.Output.WriteLine(
                        Ui.Text("Ripresa: ") + (latest.DisplayName ?? latest.SessionId));
                }
                else
                {
                    // Nessuna conversazione non e' un errore: se ne comincia una, dicendolo.
                    parseResult.InvocationConfiguration.Output.WriteLine(
                        Ui.Text("Nessuna conversazione da riprendere: se ne comincia una nuova."));
                }
            }
            catch (PaRouterException exception)
            {
                parseResult.InvocationConfiguration.Error.WriteLine(
                    exception.Code.ToWire() + ": " + exception.Message);

                return ExitCodes.ForStorage(exception.Code);
            }
        }

        return await ReplSession
            .RunAsync(parseResult, snapshot, resumeSessionId, cancellationToken, parseResult.GetValue(profileOption))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Vero quando esiste un terminale interattivo. <c>NO_COLOR</c> non disattiva il REPL, ma
    /// insieme all'assenza di TTY disattiva pannelli e animazioni.
    /// </summary>
    internal static bool Interactive => !Console.IsInputRedirected && !Console.IsOutputRedirected;
}
