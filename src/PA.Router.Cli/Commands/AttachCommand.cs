using System.CommandLine;
using PA.Router.Abstractions;
using PA.Router.Cli.Output;
using PA.Router.Infrastructure;
using PA.Router.Registry.Loading;
using PA.Router.Runtime.Ipc;

namespace PA.Router.Cli.Commands;

/// <summary>
/// <c>pa attach &lt;sessione&gt;</c>: guarda una conversazione che sta lavorando nel worker (S65).
///
/// Serve al caso concreto di chi ha lanciato un turno lungo da un terminale e vuole seguirlo da un
/// altro — o vuole vedere che cosa sta facendo una conversazione che un altro terminale ha avviato.
/// Prima gli eventi nascevano e morivano dentro il worker, e da fuori si vedeva solo l'esito finale.
///
/// **Guarda e basta.** Non invia richieste, non cancella il turno, non tocca la conversazione: chi
/// si attacca non diventa un secondo autore. La sottoscrizione e' per **sessione**, perche' e'
/// l'identificatore che esiste prima del turno.
///
/// Senza un worker vivo non c'e' niente da guardare: i turni girano nel processo che li ha chiesti,
/// e i loro eventi li disegna quel terminale.
/// </summary>
internal static class AttachCommand
{
    internal static Command Create(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> sessionArgument = new("session-id") { Description = Ui.Text("Session to watch.") };

        Command command = new("attach", Ui.Text("Watch the turns of a session running in the worker."))
        {
            dataOption,
            sessionArgument,
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

            string sessionId = parseResult.GetValue(sessionArgument)!;

            IpcClient client;

            try
            {
                client = await IpcClient
                    .ConnectAsync(configuration.Require().DataDirectory, TimeSpan.FromMilliseconds(300), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PaRouterException)
            {
                // Nessun worker: i turni girano nel processo che li chiede, e i loro eventi li
                // disegna quel terminale. Dirlo e' meglio che restare in attesa di niente.
                error.WriteLine(
                    Ui.Text("Nessun worker in ascolto: non c'e' una conversazione da guardare. ")
                    + Ui.Text("Avvialo con 'pa runtime start', oppure segui il turno dal terminale che lo esegue."));

                return ExitCodes.InvalidInput;
            }

            IOutputSurface surface = Surfaces.For(output, error, Surfaces.CanDecorate);

            surface.Line(Ui.Text("In ascolto su ") + sessionId + Ui.Text(". Ctrl+C per smettere di guardare."));
            surface.Line(Ui.Text("Guardare non invia nulla: il turno resta di chi lo ha chiesto."));
            surface.Blank();

            await using (client)
            {
                try
                {
                    await foreach (TurnEventPayload turnEvent in client
                        .SubscribeAsync(new SessionEventsRequest(sessionId), cancellationToken)
                        .ConfigureAwait(false))
                    {
                        Show(surface, turnEvent);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ctrl+C: si smette di guardare, e il turno prosegue per conto suo.
                    surface.Blank();
                    surface.Line(Ui.Text("Ascolto chiuso. Il turno continua."));
                }
            }

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Mostra un evento. Il testo di un blocco e' **gia' passato dallo screening** e resta materiale
    /// non fidato: si mostra senza interpretarlo, come ogni risposta.
    /// </summary>
    private static void Show(IOutputSurface surface, TurnEventPayload turnEvent)
    {
        switch (turnEvent.Type)
        {
            case "block_released" when turnEvent.Text is string text:
                surface.Untrusted(text);
                break;

            case "phase_ended":
                surface.Line(
                    "  " + turnEvent.Phase + ": " + turnEvent.Outcome
                    + (turnEvent.ElapsedMs is long elapsed
                        ? " (" + elapsed.ToString(Formats.Invariant) + " ms)"
                        : string.Empty));
                break;

            case "phase_started":
                surface.Line("  " + turnEvent.Phase);
                break;

            case "tool_called":
                surface.Line(
                    Ui.Text("  strumento ") + turnEvent.Tool + " (" + turnEvent.Source + Ui.Text(", giro ")
                    + (turnEvent.Round?.ToString(Formats.Invariant) ?? "?") + ")");
                break;

            case "usage_observed":
                surface.Line(
                    Ui.Text("  consumo: ") + (turnEvent.InputTokens?.ToString(Formats.Invariant) ?? Ui.Text("non dichiarato"))
                    + Ui.Text(" in ingresso, ") + (turnEvent.OutputTokens?.ToString(Formats.Invariant) ?? Ui.Text("non dichiarato"))
                    + Ui.Text(" in uscita")
                    + (turnEvent.PricedCost is decimal priced ? ", " + Ui.Format("usage.priced {0} {1}", priced.ToString("0.######", Formats.Invariant), turnEvent.Currency ?? string.Empty) : string.Empty));
                break;

            case "artifact_declared_changed":
                surface.Line(Ui.Text("  file dichiarato modificato: ") + turnEvent.Path);
                break;

            default:
                // Un evento nuovo non deve rompere un terminale vecchio.
                break;
        }
    }
}
