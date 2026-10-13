using PA.Router.Cli.Output;
using PA.Router.Hosting.Runtime;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PA.Router.Cli.Repl;

/// <summary>One temporary region for explicit file mentions. Lists names, never reads content.</summary>
internal sealed class ReplFileMenu(IAnsiConsole console, string? baseDirectory = null)
{
    private readonly string root = Path.GetFullPath(baseDirectory ?? Environment.CurrentDirectory);
    internal sealed record Entry(string Name, string Path, bool Directory);
    internal sealed record Listing(string Directory, string Prefix, IReadOnlyList<Entry> Entries, string? Error, bool Limited);
    internal sealed record Result(string Text, ConsoleKeyInfo? PendingKey = null);

    internal Listing Discover(string query)
    {
        try
        {
            string leaf = Path.EndsInDirectorySeparator(query) ? "" : Path.GetFileName(query);
            string prefix = query[..(query.Length - leaf.Length)];
            string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(prefix.Length == 0 ? "." : prefix, root));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var ordering = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            Entry[] entries = [.. new DirectoryInfo(directory).EnumerateFileSystemInfos()
                .Where(item => !item.Name.Any(char.IsControl) && item.Name.Contains(leaf, comparison))
                .Select(item => new Entry(item.Name, Path.Combine(prefix, item.Name), (item.Attributes & FileAttributes.Directory) != 0))
                .OrderByDescending(item => item.Directory).ThenBy(item => item.Name, ordering).Take(501)];
            return new(directory, prefix, entries.Take(500).ToArray(), null, entries.Length > 500);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(root, "", [], Ui.Text("files.unavailable"), false);
        }
    }

    /// <summary>Voci mostrate insieme: oltre, l'elenco mangia la conversazione invece di aiutare.</summary>
    private const int Page = 6;

    internal Result Read(string prompt, string head, IKeySource keys, ReplBottomRegion? region = null)
    {
        if (!TurnAttachments.TryGetEditingMention(head, out int start, out string query)) { return new(head); }
        string before = head[..start];
        char quote = head.Length > start + 1 && head[start + 1] is '\"' or '\'' ? head[start + 1] : '\0';
        int focus = 0;
        Listing listing = Discover(query);
        string Draft() => before + TurnAttachments.FormatMention(query);
        // Nome e tipo sulla **stessa riga**, in due colonne: prima ogni file ne occupava due, e sei
        // file diventavano dodici righe. La cartella corrente sta in cima, perche' e' il contesto di
        // tutto cio' che segue.
        IReadOnlyList<ReplBottomRegion.OverlayLine> Lines()
        {
            int width = Math.Max(20, console.Profile.Width - 1);
            int column = Math.Min(30, Math.Max(12, listing.Entries.Count == 0
                ? 12
                : listing.Entries.Max(item => item.Name.Length) + 3));
            int start = focus / Page * Page;

            List<ReplBottomRegion.OverlayLine> rows =
            [
                new(Output.Layout.Truncate("  " + SafeText.Inline(listing.Directory), width), Dim: true),
            ];

            foreach (Entry entry in listing.Entries.Skip(start).Take(Page))
            {
                bool selected = entry == listing.Entries[focus];
                string name = SafeText.Inline(entry.Name) + (entry.Directory ? "/" : string.Empty);
                string row = (selected ? "> " : "  ") + name.PadRight(column)
                    + Ui.Text(entry.Directory ? "files.directory" : "files.file");
                rows.Add(new(Output.Layout.Truncate(row, width), selected));
            }

            if (listing.Entries.Count == 0)
            {
                rows.Add(new("  " + (listing.Error ?? Ui.Text("files.empty")), Dim: true));
            }

            string tail = listing.Limited ? Ui.Text("files.limited") + "   " : string.Empty;
            rows.Add(new(Output.Layout.Truncate("  " + tail + Ui.Text("files.keys"), width), Dim: true));

            return rows;
        }

        return ReplMenuCanvas.Run(console, region, prompt, Draft, Lines, refresh =>
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
                if (key.Key == ConsoleKey.Escape) { return new Result(Draft()); }
                if (key.Key == ConsoleKey.UpArrow && key.Modifiers.HasFlag(ConsoleModifiers.Alt))
                {
                    if (listing.Error is null && Path.GetDirectoryName(listing.Directory) is { } parent)
                    {
                        query = Path.IsPathRooted(query) ? parent : Path.GetRelativePath(root, parent);
                        query = query == "." ? "" : Path.TrimEndingDirectorySeparator(query) + Path.DirectorySeparatorChar;
                        listing = Discover(query);
                        focus = 0;
                    }
                }
                else if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow && listing.Entries.Count > 0)
                {
                    focus = (focus + (key.Key == ConsoleKey.UpArrow ? listing.Entries.Count - 1 : 1)) % listing.Entries.Count;
                }
                else if (key.Key is ConsoleKey.Enter or ConsoleKey.Tab && !pasting)
                {
                    if (listing.Entries.Count > 0)
                    {
                        Entry selected = listing.Entries[focus];
                        if (!selected.Directory) { return new Result(before + TurnAttachments.FormatMention(selected.Path) + " "); }
                        query = selected.Path + Path.DirectorySeparatorChar;
                        listing = Discover(query);
                        focus = 0;
                    }
                    // No match is never permission to submit a turn.
                }
                else if (key.Key == ConsoleKey.Backspace && !key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    if (query.Length == 0) { return new Result(before); }
                    query = query[..^1];
                    listing = Discover(query);
                    focus = 0;
                }
                else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    if (query.Length == 0 && quote == '\0' && key.KeyChar is '\"' or '\'')
                    { quote = key.KeyChar; continue; }
                    if (quote != '\0' && key.KeyChar == quote) { return new Result(Draft()); }
                    query += key.KeyChar;
                    listing = Discover(query);
                    focus = 0;
                }
                else { return new Result(Draft(), key); }

            }
        });
    }
}
