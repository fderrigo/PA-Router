using System.Text;
using PA.Router.Cli.Output;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PA.Router.Cli.Repl;

/// <summary>Temporary command suggestions; never executes a command or lists file contents.</summary>
internal sealed class ReplCommandMenu(IAnsiConsole console)
{
    /// <summary>Voci mostrate insieme: oltre, l'elenco mangia la conversazione invece di aiutare.</summary>
    private const int Page = 6;

    internal sealed record Result(string Text, ConsoleKeyInfo? PendingKey = null);

    /// <summary>I tasti per intero se ci stanno, altrimenti la forma corta.</summary>
    internal static string Hints(int room) =>
        Ui.Text("completion.keys") is { } full && full.Length <= room ? full : Ui.Text("completion.keysShort");

    internal Result Read(string prompt, string prefix, ReplCompletion completion, IKeySource keys, ReplBottomRegion? region = null)
    {
        StringBuilder text = new(prefix);
        int focus = 0;
        IReadOnlyList<string> matches = completion.For(prefix, prefix.Length);

        // Il comando e cio' che fa stanno **sulla stessa riga**, in due colonne: prima ogni voce ne
        // occupava due, e sei comandi diventavano dodici righe di elenco per tre parole utili.
        IReadOnlyList<ReplBottomRegion.OverlayLine> Lines()
        {
            int width = Math.Max(20, console.Profile.Width - 1);
            int column = Math.Min(22, Math.Max(10, matches.Count == 0 ? 10 : matches.Max(item => item.Length) + 2));
            int start = focus / Page * Page;

            List<ReplBottomRegion.OverlayLine> rows = [];

            foreach (string command in matches.Skip(start).Take(Page))
            {
                bool selected = command == matches[focus];
                string note = ReplHelp.For(command)?.Note ?? string.Empty;
                string row = (selected ? "> " : "  ") + command.PadRight(column) + note;
                rows.Add(new(Output.Layout.Truncate(row, width), selected));
            }

            // La descrizione per intero sta **sotto**, per la sola voce scelta: nella colonna verrebbe
            // tagliata su un terminale stretto, ed e' proprio li' che serve leggerla tutta.
            if (matches.Count > 0 && ReplHelp.For(matches[focus])?.Note is { Length: > 0 } detail)
            {
                rows.Add(new(Output.Layout.Truncate("  " + detail, width), Dim: true));
            }

            // L'ultima riga: dove sei e come si sceglie. L'elenco dei tasti per intero e' piu' lungo di
            // mezzo terminale stretto, quindi ne esiste una forma corta e si usa quella quando la
            // lunga non entra: troncata non insegnerebbe niente a nessuno.
            string tail = matches.Count == 0
                ? Ui.Text("completion.empty")
                : Ui.Format("Comando {0} di {1}", focus + 1, matches.Count) + "   " + Hints(width - 24);

            rows.Add(new(Output.Layout.Truncate("  " + tail, width), Dim: true));

            return rows;
        }

        return ReplMenuCanvas.Run(console, region, prompt, text.ToString, Lines, refresh =>
        {
            while (true)
            {
                refresh();
                ConsoleKeyInfo key = keys.Read();
                // Un ridimensionamento ridisegna e basta; la rotella dentro un elenco sposta la scelta.
                if (ReplKeys.IsResize(key)) { continue; }
                if (ReplKeys.IsWheel(key, out bool back)) { key = new ConsoleKeyInfo('\0', back ? ConsoleKey.UpArrow : ConsoleKey.DownArrow, false, false, false); }
                else if (ReplKeys.IsTerminalEvent(key)) { continue; }
                bool pasting = keys.Pasting || keys.KeyAvailable;
                if (key.Key is ConsoleKey.Escape) { return new Result(text.ToString()); }
                if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow && matches.Count > 0)
                {
                    focus = (focus + (key.Key == ConsoleKey.UpArrow ? matches.Count - 1 : 1)) % matches.Count;
                }
                else if (key.Key is ConsoleKey.Tab or ConsoleKey.Enter && !pasting)
                {
                    if (matches.Count == 0 || (key.Key == ConsoleKey.Enter && text.ToString() == matches[focus]))
                    { return new Result(text.ToString(), key); }
                    return new Result(matches[focus]);
                }
                else if (key.Key == ConsoleKey.Backspace && !key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    text.Length--;
                    if (text.Length == 0) { return new Result(""); }
                    matches = completion.For(text.ToString(), text.Length);
                    focus = 0;
                }
                else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar) && !char.IsWhiteSpace(key.KeyChar))
                {
                    text.Append(key.KeyChar);
                    matches = completion.For(text.ToString(), text.Length);
                    focus = 0;
                }
                else { return new Result(text.ToString(), key); }
            }
        });
    }
}
