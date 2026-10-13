using System.CommandLine;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Hosting.Runtime;
using PA.Router.Hosting.Security;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Storage;
using PA.Router.Registry.Bootstrap;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Commands;

/// <summary>
/// <c>pa init</c>: prepara la directory dati con i modelli del pacchetto contratti.
///
/// I target nascono disabilitati e i segnaposto restano tali: l'inizializzazione non abilita
/// nulla e non contatta nessuno. Il token di servizio viene generato e custodito cifrato, mai
/// stampato: la consegna al backend e' un'operazione separata dell'operatore.
/// </summary>
internal static class InitCommand
{
    internal static Command Create(Option<DirectoryInfo?> dataOption)
    {
        Option<bool> persistOption = new("--persist-content")
        {
            Description = Ui.Text("Start in encrypted content mode so memory survives a restart."),
        };

        Command command = new("init", Ui.Text("Create the data directory from the contract templates."))
        {
            dataOption,
            persistOption,
        };

        command.SetAction(parseResult => Run(parseResult, dataOption, persistOption));

        return command;
    }

    private static int Run(ParseResult parseResult, Option<DirectoryInfo?> dataOption, Option<bool> persistOption)
    {
        string dataDirectory = CommandContext.ResolveDataDirectory(parseResult, dataOption);
        bool persistContent = parseResult.GetValue(persistOption);
        TextWriter output = parseResult.InvocationConfiguration.Output;

        try
        {
            WorkspaceCreated created = WorkspaceCreation.Create(dataDirectory, persistContent, SystemClock.Instance);

            output.WriteLine(Ui.Text("Data directory: ") + created.Paths.DataDirectory);
            output.WriteLine(Ui.Text("Store schema:   ") + created.SchemaVersion.ToString(Formats.Invariant));

            foreach (BootstrapFile file in created.Files)
            {
                output.WriteLine("  " + (file.Created ? Ui.Text("ui.created") : Ui.Text("kept   ")) + " " + file.RelativePath);
            }

            if (created.Report.Snapshot is not ConfigurationSnapshot snapshot)
            {
                return CommandContext.ReportIssues(parseResult, created.Report.Issues);
            }

            ReportBootstrap(output, snapshot, created.Paths);

            return ExitCodes.Success;
        }
        catch (PaRouterException ex)
        {
            return CommandContext.ReportError(parseResult, ex);
        }
    }

    /// <summary>
    /// Il riepilogo della creazione, per una persona (24 settembre 2026): nomi dei servizi e non identificatori, le frasi di
    /// `pa config show`, e come ultima riga il passo che serve davvero — scegliere i modelli con `pa setup`. Prima diceva
    /// che `pa doctor` doveva «certificare ogni target», e mandava l'operatore nel posto sbagliato.
    /// </summary>
    private static void ReportBootstrap(TextWriter output, ConfigurationSnapshot snapshot, StorePaths paths)
    {
        output.WriteLine(Ui.Text("show.defaultLabel") + ": " + Repl.ProfilePresentation.Name(snapshot, snapshot.Config.DefaultProfileId));
        output.WriteLine(Ui.Text("show.contents") + ": "
            + (snapshot.Config.ContentMode is ContentMode.Encrypted ? Ui.Text("show.kept") : Ui.Text("show.notKept")));

        bool tokenReady = ServiceTokenVault.TryCreate(
            paths,
            Infrastructure.Crypto.PlatformKeyProtector.Create(),
            snapshot.Config.Api.ServiceTokenRef,
            out _);

        if (!tokenReady) { output.WriteLine(Ui.Text("init.noProtector")); }

        output.WriteLine(Ui.Format("init.services {0}", snapshot.Registry.Services.Count(service =>
            ServiceSetupCatalog.AnswersRequests(service.Capability))));
        output.WriteLine();
        output.WriteLine(Ui.Text("init.next"));
    }
}
