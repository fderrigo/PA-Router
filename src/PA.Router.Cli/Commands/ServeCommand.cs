using System.CommandLine;
using PA.Router.Abstractions;
using PA.Router.Api;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Registry.Loading;
using PA.Router.Runtime.Worker;

namespace PA.Router.Cli.Commands;

/// <summary>
/// <c>pa serve</c>: host HTTP locale per un backend che gira sulla stessa macchina.
///
/// L'ascolto e' solo su loopback e ogni route richiede il token di servizio. Il token viene
/// generato a ogni avvio se non lo fornisce l'operatore, e stampato una volta sola su standard
/// output: e' una capability, non una configurazione da lasciare in giro.
///
/// Non esiste un'opzione per ascoltare su un indirizzo di rete. Un chatbot remoto richiede
/// un'istanza vicina al proprio backend, non un'esposizione di questa (03-specs/HEADLESS-API-SDK.md).
/// </summary>
internal static class ServeCommand
{
    internal static Command Create(Option<DirectoryInfo?> dataOption)
    {
        Option<int?> portOption = new("--port") { Description = Ui.Text("Loopback port. Defaults to the configured one.") };
        Option<string?> tokenOption = new("--service-token") { Description = Ui.Text("Service token to require. Generated when omitted.") };

        Command command = new("serve", Ui.Text("Serve the local HTTP API on loopback for a backend on this machine."))
        {
            dataOption,
            portOption,
            tokenOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            TextWriter output = parseResult.InvocationConfiguration.Output;
            TextWriter error = parseResult.InvocationConfiguration.Error;

            ConfigurationLoadResult configuration = new ConfigurationLoader(SystemClock.Instance)
                .Load(CommandContext.ResolveDataDirectory(parseResult, dataOption));

            if (!configuration.IsValid)
            {
                return CommandContext.ReportIssues(parseResult, configuration.Issues);
            }

            try
            {
                // `pa serve` possiede la directory dati come worker e serve l'API sopra la
                // stessa composizione: un solo proprietario, e gli altri client — `pa run`
                // compreso — passano dal canale del worker invece di trovarla occupata.
                await using WorkerHost worker = WorkerHost.Start(new WorkerOptions(
                    configuration.Require().DataDirectory));

                await using RouterHost host = await RouterHost.StartAsync(
                    configuration.Require(),
                    parseResult.GetValue(tokenOption),
                    parseResult.GetValue(portOption),
                    cancellationToken,
                    worker.Stack).ConfigureAwait(false);

                output.WriteLine(Ui.Text("Ascolto su ") + host.Address);
                output.WriteLine(Ui.Text("Token di servizio: ") + host.ServiceToken);
                output.WriteLine(Ui.Text("Il token vale per questo avvio: non viene conservato in chiaro."));

                // L'host resta vivo finche' l'operatore non interrompe: nessun avvio automatico,
                // nessun servizio installato.
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

                return ExitCodes.Success;
            }
            catch (OperationCanceledException)
            {
                output.WriteLine(Ui.Text("Host fermato."));

                return ExitCodes.Success;
            }
            catch (PaRouterException ex)
            {
                error.WriteLine(ex.Code.ToWire() + ": " + ex.Message);

                return ExitCodes.ForStorage(ex.Code);
            }
        });

        return command;
    }
}
