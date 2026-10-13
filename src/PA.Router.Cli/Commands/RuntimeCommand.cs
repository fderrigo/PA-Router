using System.CommandLine;
using PA.Router.Abstractions;
using PA.Router.Runtime.Ipc;
using PA.Router.Runtime.Worker;

namespace PA.Router.Cli.Commands;

/// <summary>
/// Comandi del runtime locale. <c>worker</c> e' il sottocomando interno avviato dal client, non
/// un servizio da installare; <c>status</c> interroga il worker esistente senza avviarne uno.
/// I comandi operativi (run/explain/doctor/registry/policy/audit) arrivano con S08.
/// </summary>
internal static class RuntimeCommand
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    internal static Command Create()
    {
        Option<DirectoryInfo?> dataOption = new("--data")
        {
            Description = Ui.Text("Data directory owned by the worker. Defaults to the per-user location."),
        };

        Command worker = new("worker", Ui.Text("Run the local worker that owns the data directory."))
        {
            dataOption,
        };

        worker.SetAction((parseResult, cancellationToken) =>
            RunWorkerAsync(parseResult, dataOption, cancellationToken));

        Command status = new("status", Ui.Text("Show the status of the worker that owns the data directory."))
        {
            dataOption,
        };

        status.SetAction((parseResult, cancellationToken) =>
            ShowStatusAsync(parseResult, dataOption, cancellationToken));

        Command start = new("start", Ui.Text("Connect to the worker of the data directory, starting it if none answers."))
        {
            dataOption,
        };

        start.SetAction((parseResult, cancellationToken) =>
            StartAsync(parseResult, dataOption, cancellationToken));

        Command runtime = new("runtime", Ui.Text("Local runtime worker."))
        {
            worker,
            start,
            status,
        };

        runtime.SetAction(parseResult =>
        {
            parseResult.InvocationConfiguration.Output.WriteLine(
                Ui.Text("Specify a runtime subcommand. Run 'pa runtime --help' for the list."));
            return ExitCodes.UsageError;
        });

        return runtime;
    }

    private static async Task<int> RunWorkerAsync(
        ParseResult parseResult,
        Option<DirectoryInfo?> dataOption,
        CancellationToken cancellationToken)
    {
        string dataDirectory = ResolveDataDirectory(parseResult, dataOption);
        TextWriter output = parseResult.InvocationConfiguration.Output;

        try
        {
            await using WorkerHost host = WorkerHost.Start(new WorkerOptions(dataDirectory));

            output.WriteLine(Ui.Text("Worker started."));
            output.WriteLine(Ui.Text("  schema version: ") + host.SchemaVersion.ToString(Formats.Invariant));
            output.WriteLine(Ui.Text("  content mode:   ") + host.ContentMode.ToWire());

            await host.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C: arresto richiesto dall'operatore, non un errore.
            return ExitCodes.Cancelled;
        }
        catch (PaRouterException ex)
        {
            return Report(parseResult, ex);
        }
    }

    /// <summary>
    /// Collega il client al worker della directory dati e, se nessuno risponde, lo avvia.
    ///
    /// L'avvio e' esplicito: <c>status</c> continua a riportare l'assenza invece di creare un
    /// processo che l'operatore non ha chiesto. Un worker gia' vivo non viene mai terminato ne'
    /// sostituito: il client si collega e basta.
    /// </summary>
    private static async Task<int> StartAsync(
        ParseResult parseResult,
        Option<DirectoryInfo?> dataOption,
        CancellationToken cancellationToken)
    {
        string dataDirectory = ResolveDataDirectory(parseResult, dataOption);
        TextWriter output = parseResult.InvocationConfiguration.Output;

        try
        {
            await using IpcClient client = await WorkerLauncher.ConnectOrStartAsync(
                dataDirectory,
                Environment.ProcessPath!,
                ConnectTimeout,
                cancellationToken).ConfigureAwait(false);

            StoreStatusPayload status = await client.GetStoreStatusAsync(cancellationToken).ConfigureAwait(false);

            output.WriteLine(Ui.Text("Worker version:    ") + client.WorkerVersion);
            output.WriteLine(Ui.Text("Schema version:    ") + status.SchemaVersion.ToString(Formats.Invariant));
            output.WriteLine(Ui.Text("Content mode:      ") + status.ContentMode);
            output.WriteLine(Ui.Text("Connected clients: ") + status.ConnectedClients.ToString(Formats.Invariant));
            output.WriteLine(Ui.Text("Il worker si arresta da solo dopo l'inattivita' configurata."));

            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Cancelled;
        }
        catch (PaRouterException ex)
        {
            return Report(parseResult, ex);
        }
    }

    private static async Task<int> ShowStatusAsync(
        ParseResult parseResult,
        Option<DirectoryInfo?> dataOption,
        CancellationToken cancellationToken)
    {
        string dataDirectory = ResolveDataDirectory(parseResult, dataOption);
        TextWriter output = parseResult.InvocationConfiguration.Output;

        try
        {
            await using IpcClient client = await IpcClient.ConnectAsync(dataDirectory, ConnectTimeout, cancellationToken)
                .ConfigureAwait(false);

            StoreStatusPayload status = await client.GetStoreStatusAsync(cancellationToken).ConfigureAwait(false);

            output.WriteLine(Ui.Text("Worker version:    ") + client.WorkerVersion);
            output.WriteLine(Ui.Text("Schema version:    ") + status.SchemaVersion.ToString(Formats.Invariant));
            output.WriteLine(Ui.Text("Content mode:      ") + status.ContentMode);
            output.WriteLine(Ui.Text("Key protector:     ") + (status.KeyProtector ?? Ui.Text("nessuno")));
            output.WriteLine(Ui.Text("Connected clients: ") + status.ConnectedClients.ToString(Formats.Invariant));

            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Cancelled;
        }
        catch (PaRouterException ex)
        {
            return Report(parseResult, ex);
        }
    }

    private static int Report(ParseResult parseResult, PaRouterException exception)
    {
        // Solo codice e messaggio safe: nessuna eccezione provider o path completo sullo stream.
        parseResult.InvocationConfiguration.Error.WriteLine(
            exception.Code.ToWire() + ": " + (Ui.Contains(exception.Message) ? Ui.Text(exception.Message) : exception.Message));

        return ExitCodes.ForStorage(exception.Code);
    }

    /// <summary>
    /// Directory dati del comando, con la stessa regola di tutti gli altri (difetto trovato
    /// dall'audit, S77).
    ///
    /// Questo comando aveva un risolutore proprio che saltava `PA_ROUTER_DATA_DIR`: con la variabile
    /// impostata, `pa runtime start` avviava un worker sulla directory **predefinita** mentre
    /// `pa run` lavorava su quella dichiarata, quindi il turno non trovava il worker e apriva lo
    /// store da solo. Una seconda copia di un valore risolto e' la forma di INV-22 applicata a un
    /// dato invece che a una catena: non fallisce, decide diversamente.
    /// </summary>
    private static string ResolveDataDirectory(ParseResult parseResult, Option<DirectoryInfo?> dataOption) =>
        CommandContext.ResolveDataDirectory(parseResult, dataOption);
}
