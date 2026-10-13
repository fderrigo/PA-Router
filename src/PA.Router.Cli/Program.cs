using System.CommandLine;
using PA.Router.Abstractions;
using PA.Router.Infrastructure;

namespace PA.Router.Cli;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        Ui.SetLanguage("en");
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try
        {
            // La lingua si legge anche quando la riga ha un errore (23 settembre 2026): e' proprio allora che si mostra
            // l'aiuto, e usciva in inglese mentre `--help` usciva nella lingua dell'operatore.
            ParseResult initial = PaRootCommand.Create().Parse(args);
            try
            {
                string directory = Commands.CommandContext.ResolveDataDirectory(initial);
                Ui.SetLanguage(new PA.Router.Registry.Loading.ConfigurationLoader(SystemClock.Instance).Load(directory).Snapshot?.Config.Language
                    ?? PA.Router.Registry.Loading.ConfigurationLoader.DeclaredLanguage(directory));
            }
            catch (Exception exception) when (initial.Errors.Count > 0 && exception is not OutOfMemoryException)
            {
                // Una riga sbagliata non deve diventare un secondo errore mentre si cerca la lingua: resta l'inglese.
            }
            return await PaRootCommand.Create().Parse(args).InvokeAsync().ConfigureAwait(false);
        }
        catch (PaRouterException exception)
        {
            // Rete di sicurezza per gli errori tipizzati che nascono fuori dai percorsi che li gestiscono gia' - per
            // esempio un `PA_ROUTER_DATA_DIR` presente e non valido, letto prima che un comando cominci. Un operatore
            // deve leggere il codice e il motivo, non una traccia di stack.
            Console.Error.WriteLine(exception.Code.ToWire() + ": " + exception.Message);

            return ExitCodes.ForStorage(exception.Code);
        }
    }
}
