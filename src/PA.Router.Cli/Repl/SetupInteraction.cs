using System.Text;
using PA.Router.Cli.Output;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PA.Router.Cli.Repl;

internal interface ISetupInteraction
{
    SetupNavigation Navigation { get; }
    void Frame(string breadcrumb, bool dirty, string context) { }
    /// <summary>La gabbia della schermata. Una resa che non la disegna riceve la forma storica.</summary>
    void Page(SetupPage page) => Frame(page.Path, page.Dirty, page.Context);
    /// <summary>Un'intestazione di gruppo in un elenco: si legge, non si sceglie.</summary>
    static bool IsHeading(string id) => id.StartsWith('#');
    void Details(IReadOnlyDictionary<string, string> details) { }
    void ProfileSummary(string name, string description) { }
    /// <summary>
    /// L'etichetta attiva e che cosa comporta: resta in cima a ogni pagina finche' non cambia, perche' cio' che si vede
    /// sotto le regole dell'etichetta vale per lei (deciso con l'operatore il 23 settembre 2026).
    /// </summary>
    void ActiveLabel(string name, string summary) { }
    string? Choose(string title, IReadOnlyList<(string Id, string Label)> options);
    string? Edit(string title, string current, bool secret = false);
    IReadOnlyList<string>? Many(string title, IReadOnlyList<(string Id, string Label)> options, IReadOnlySet<string> selected, bool refresh = false)
    {
        HashSet<string> values = new(selected, StringComparer.Ordinal);
        while (true)
        {
            string? choice = Choose(title, [.. options.Select(option => (option.Id, IsHeading(option.Id) ? option.Label : (values.Contains(option.Id) ? "[x] " : "[ ] ") + option.Label)),
                ("confirm", Ui.Text("Conferma - applica la selezione alla sessione.")), ("back", Ui.Text("Indietro - annulla questa selezione."))]);
            if (choice is null or "back") { return null; }
            if (choice == "confirm") { return [.. values]; }
            if (IsHeading(choice)) { continue; }
            if (!values.Add(choice)) { values.Remove(choice); }
        }
    }
}

/// <summary>Richiesta di navigazione della sessione, mai un errore o un'eccezione.</summary>
internal sealed class SetupNavigation
{
    internal string? Destination { get; private set; }
    internal bool Pending => Destination is not null;
    internal void Request(string destination) => Destination = destination;
    internal string? Take()
    {
        string? destination = Destination;
        Destination = null;
        return destination;
    }
}

/// <summary>Una regione Spectre temporanea. Nessuno schermo alternativo o prompt annidato.</summary>
internal sealed class SetupInteraction(IAnsiConsole? output = null, Func<ConsoleKeyInfo>? input = null, ReplInterrupt? interruptions = null) : ISetupInteraction
{
    public SetupNavigation Navigation { get; } = new();
    private string breadcrumb = "Setup";
    private string context = "";
    private string pageNotice = "";
    private string? step;
    private int? missing;
    private bool dirty;
    private string? profileName;
    private string profileDescription = "";
    private string? labelName;
    private string labelSummary = "";
    private IReadOnlyDictionary<string, string> fieldDetails = new Dictionary<string, string>();
    private readonly IAnsiConsole console = output ?? AnsiConsole.Console;
    private readonly ReplInterrupt interrupt = interruptions ?? ReplTerminalScope.Current?.Interruptions ?? new();
    private readonly Func<ConsoleKeyInfo> readKey = input ?? (() => ReplTerminalScope.Current?.Keys.Read() ?? Console.ReadKey(intercept: true));
    private bool Pasting => input is null && ReplTerminalScope.Current?.Keys.Pasting == true;

    public void Frame(string path, bool changed, string description) => Page(new SetupPage(path, description) { Dirty = changed });

    public void Page(SetupPage page)
    {
        profileName = null;
        breadcrumb = SafeText.Inline(page.Path);
        dirty = page.Dirty;
        context = SafeText.Inline(page.Why);
        pageNotice = SafeText.Inline(page.Notice);
        step = page.Step is null ? null : SafeText.Inline(page.Step);
        missing = page.Missing;
        fieldDetails = new Dictionary<string, string>();
    }

    public void Details(IReadOnlyDictionary<string, string> details) => fieldDetails = details;

    public void ActiveLabel(string name, string summary)
    {
        labelName = SafeText.Inline(name);
        labelSummary = SafeText.Inline(summary);
    }

    public void ProfileSummary(string name, string description)
    {
        profileName = SafeText.Inline(name);
        profileDescription = SafeText.Inline(description);
    }

    public string? Choose(string title, IReadOnlyList<(string Id, string Label)> options) =>
        Select(title, options, null)?.FirstOrDefault();

    public IReadOnlyList<string>? Many(string title, IReadOnlyList<(string Id, string Label)> options, IReadOnlySet<string> selected, bool refresh = false) =>
        Select(title, options, new HashSet<string>(selected, StringComparer.Ordinal), refresh);

    private IReadOnlyList<string>? Select(string title, IReadOnlyList<(string Id, string Label)> options, HashSet<string>? selected, bool refresh = false)
    {
        if (Navigation.Pending || (input is null && !PaRootCommand.Interactive)) { return null; }
        int focus = Math.Max(0, options.ToList().FindIndex(option => option.Label.StartsWith("(x)", StringComparison.Ordinal)));
        int required = options.ToList().FindIndex(option => option.Label.StartsWith("[obbligatorio]", StringComparison.Ordinal));
        if (required >= 0 && profileName is null) { focus = required; }
        int Selectable(int from, int direction)
        {
            if (options.Count == 0 || options.All(option => ISetupInteraction.IsHeading(option.Id))) { return from; }
            int index = (from % options.Count + options.Count) % options.Count;
            while (ISetupInteraction.IsHeading(options[index].Id)) { index = (index + direction + options.Count) % options.Count; }
            return index;
        }
        focus = Selectable(focus, 1);
        bool singleChoice = options.Any(option => option.Label.StartsWith("(x)", StringComparison.Ordinal)
            || option.Label.StartsWith("( )", StringComparison.Ordinal));
        bool details = false;
        string? pendingNavigation = null;
        HashSet<string>? originalSelection = selected is null ? null : new(selected, StringComparer.Ordinal);
        IRenderable View(int? pageLimit = null)
        {
            if (pendingNavigation is not null)
            {
                return new Rows(new Text(Ui.Text("selection.pending"), new Style(decoration: Decoration.Bold)),
                    Text.Empty, new Text(breadcrumb), Separator(),
                    new Text(Ui.Text("selection.destination." + pendingNavigation)), Text.Empty,
                    new Text(Ui.Text("selection.confirm")), new Text(Ui.Text("selection.discard")),
                    new Text(Ui.Text("selection.stay")), Text.Empty, Separator(),
                    Keys(Ui.Text("selection.keys")), Text.Empty, SaveStatus(pendingChanges: true));
            }
            int pageSize = pageLimit ?? (details ? 1 : Math.Max(1, (console.Profile.Height - 12) / 3));
            int start = focus / pageSize * pageSize;
            List<IRenderable> lines = Header(title);
            List<(Markup Label, string Description, bool Wide)> entries = [];
            foreach ((string id, string label) in options.Skip(start).Take(pageSize))
            {
                if (ISetupInteraction.IsHeading(id))
                {
                    entries.Add((new Markup((id.StartsWith("#-", StringComparison.Ordinal) ? "[grey]" : "[bold]") + Markup.Escape(SafeText.Inline(label)) + "[/]"), "", true));
                    continue;
                }
                bool current = options[focus].Id == id;
                string marker = selected is null ? "" : (selected.Contains(id) ? "[x] " : "[ ] ");
                string[] parts = SafeText.Inline(label).Split(" - ", 2, StringSplitOptions.None);
                string name = parts[0];
                string status = "";
                foreach ((string token, string color) in new[] { ("[obbligatorio]", "red"), ("[completo]", "green"), ("[warning]", "yellow"), ("[opzionale]", "grey"), ("[non ammesso]", "grey") })
                {
                    if (!name.StartsWith(token, StringComparison.Ordinal)) { continue; }
                    status = "[" + color + "]" + Markup.Escape(Ui.Text(token)) + "[/] ";
                    name = name[token.Length..].TrimStart();
                    break;
                }
                // Focus e validita' restano indipendenti: il colore dello stato riguarda solo il badge.
                string focusStyle = current ? "bold cyan1" : "default";
                Markup entryLabel = new("[" + focusStyle + "]" + (current ? "> " : "  ")
                    + Markup.Escape(marker) + "[/]" + status
                    + "[" + focusStyle + "]" + Markup.Escape(name) + "[/]");
                // Una voce senza descrizione resta senza: la frase generica diceva «apri per vedere valore e
                // vincoli» anche accanto a «Si', confermo».
                string description = parts.Length > 1 ? parts[1] : SetupHelp.Known(id) ? SetupHelp.Description(id) : "";
                entries.Add((entryLabel, description, false));
            }
            if (entries.Count > 0)
            {
                int width = Math.Max(1, console.Profile.Width - 1);
                RenderOptions renderOptions = RenderOptions.Create(console, null);
                // Intestazioni e note occupano la riga intera: non entrano nella misura della colonna, altrimenti una
                // nota lunga toglierebbe alle voci lo spazio per la loro descrizione.
                var choices = entries.Where(entry => entry.Description.Length > 0 || !entry.Wide).ToArray();
                int labelWidth = choices.Length == 0 ? 0 : choices.Max(entry => ((IRenderable)entry.Label).Measure(renderOptions, int.MaxValue).Max);
                bool columns = width - labelWidth - 2 >= 40;
                Grid? menu = null;
                foreach (var entry in entries)
                {
                    if (entry.Wide || !columns)
                    {
                        if (menu is not null) { lines.Add(menu); menu = null; }
                        lines.Add(entry.Label);
                        if (!entry.Wide && entry.Description.Length > 0) { lines.Add(new Padder(new Text(entry.Description, new Style(Color.Grey)), new Padding(4, 0, 0, 0))); }
                        continue;
                    }
                    menu ??= new Grid { Width = width }
                        .AddColumn(new GridColumn { Width = labelWidth }.NoWrap().PadRight(2))
                        .AddColumn(new GridColumn().PadRight(0));
                    menu.AddRow(entry.Label, new Text(entry.Description, new Style(Color.Grey)));
                }
                if (menu is not null) { lines.Add(menu); }
            }
            if (options.Count == 0) { lines.Add(new Text(Ui.Text("Nessun elemento disponibile."))); }
            lines.Add(new Text(selected is null ? Ui.Interpolate($"Voci {Math.Min(start + 1, options.Count)}-{Math.Min(start + pageSize, options.Count)} di {options.Count}") : Ui.Interpolate($"{selected.Count} selezionati"), new Style(decoration: Decoration.Dim)));
            if (details && options.Count > 0)
            {
                string information = fieldDetails.TryGetValue(options[focus].Id, out string? detail) ? detail : SetupHelp.Details(options[focus].Id, options[focus].Label);
                lines.Add(new Text(Ui.Text("DETTAGLI"), new Style(decoration: Decoration.Bold)));
                lines.Add(new Text(SafeText.Inline(information), new Style(Color.Grey)));
            }
            lines.Add(Separator());
            if (selected is not null) { lines.Add(new Text(Ui.Text("Scelta multipla: [x] selezionato; C conferma nella sessione."), new Style(Color.Grey))); }
            else if (singleChoice) { lines.Add(new Text(Ui.Text("Scelta singola: (x) valore attuale; spostare il cursore non lo cambia."), new Style(Color.Grey))); }
            lines.Add(Keys(selected is null ? Ui.Text("Su/Giu sposta | Enter ") + (singleChoice ? Ui.Text("scegli") : Ui.Text("apri/seleziona")) + Ui.Text(" | I dettagli")
                : Ui.Text("Spazio seleziona | C conferma | I dettagli") + (refresh ? Ui.Text(" | R aggiorna") : "")));
            lines.Add(Keys(Ui.Text("B/Esc indietro | H home | S salva configurazione | Q esci")));
            lines.Add(Text.Empty);
            lines.Add(SaveStatus(selected is not null && !selected.SetEquals(originalSelection!)));
            Rows view = new(lines);
            // Lo spazio del footer non deve far sparire titolo o percorso nei terminali bassi.
            int height = Segment.SplitLines(((IRenderable)view).Render(RenderOptions.Create(console, null), Math.Max(1, console.Profile.Width - 1))).Count();
            return pageSize > 1 && height >= console.Profile.Height ? View(pageSize - 1) : view;
        }
        return console.Live(View()).AutoClear(true).Start<IReadOnlyList<string>?>(live =>
        {
            live.Refresh();
            while (true)
            {
                ConsoleKeyInfo key = readKey();
                if (Pasting) { continue; } // Pasted text never activates menu shortcuts.
                if (ReplInterrupt.IsControlC(key))
                {
                    if (!interrupt.Press()) { live.UpdateTarget(View()); live.Refresh(); continue; }
                    key = new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false);
                }
                else { interrupt.Activity(); }

                if (pendingNavigation is not null)
                {
                    if (key.Key == ConsoleKey.C)
                    {
                        interrupt.Stay();
                        if (pendingNavigation != "back") { Navigation.Request(pendingNavigation); }
                        return [.. selected!];
                    }
                    if (key.Key == ConsoleKey.D)
                    {
                        if (pendingNavigation == "back") { return null; }
                        Navigation.Request(pendingNavigation);
                        return null;
                    }
                    if (key.Key is ConsoleKey.B or ConsoleKey.Escape) { pendingNavigation = null; interrupt.Stay(); }
                    live.UpdateTarget(View()); live.Refresh(); continue;
                }
                if (selected is not null && !selected.SetEquals(originalSelection!)
                    && key.Key is ConsoleKey.Escape or ConsoleKey.B or ConsoleKey.H or ConsoleKey.Q or ConsoleKey.S or ConsoleKey.R)
                {
                    pendingNavigation = key.Key switch { ConsoleKey.H => "home", ConsoleKey.Q => "quit", ConsoleKey.S => "save", ConsoleKey.R => "refresh", _ => "back" };
                    live.UpdateTarget(View()); live.Refresh(); continue;
                }
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.B) { return null; }
                string? shortcut = key.Key switch { ConsoleKey.C => "confirm", ConsoleKey.A => "add", ConsoleKey.D => "discard", ConsoleKey.M => "migrate", ConsoleKey.Q when options.Any(option => option.Id == "quit") => "quit", ConsoleKey.S when options.Any(option => option.Id == "commit") => "commit", ConsoleKey.S when options.Any(option => option.Id == "save-disabled") => "save-disabled", ConsoleKey.S when options.Any(option => option.Id == "save") => "save", _ => null };
                if (selected is null && shortcut is not null && options.Any(option => option.Id == shortcut)) { return [shortcut]; }
                if (key.Key is ConsoleKey.H or ConsoleKey.Q or ConsoleKey.S)
                {
                    Navigation.Request(key.Key == ConsoleKey.H ? "home" : key.Key == ConsoleKey.Q ? "quit" : "save");
                    return null;
                }
                if (key.Key == ConsoleKey.I || key.KeyChar == '?') { details = !details; }
                if (key.Key == ConsoleKey.UpArrow && options.Count > 0) { focus = Selectable(focus + options.Count - 1, -1); }
                if (key.Key == ConsoleKey.DownArrow && options.Count > 0) { focus = Selectable(focus + 1, 1); }
                if (key.Key == ConsoleKey.Home) { focus = Selectable(0, 1); }
                if (key.Key == ConsoleKey.End) { focus = Selectable(Math.Max(0, options.Count - 1), -1); }
                if (key.Key == ConsoleKey.R && refresh) { Navigation.Request("refresh"); return null; }
                if (selected is not null && key.Key == ConsoleKey.C) { return [.. selected]; }
                if (options.Count > 0 && key.Key is ConsoleKey.Enter or ConsoleKey.Spacebar)
                {
                    if (selected is null && key.Key == ConsoleKey.Enter) { return [options[focus].Id]; }
                    if (selected is not null && !selected.Add(options[focus].Id)) { selected.Remove(options[focus].Id); }
                }
                live.UpdateTarget(View());
                live.Refresh();
            }
        });
    }

    public string? Edit(string title, string current, bool secret = false)
    {
        if (Navigation.Pending || (input is null && !PaRootCommand.Interactive)) { return null; }
        StringBuilder value = new(secret ? "" : current);
        bool replace = true;
        bool details = false;
        string? pendingNavigation = null;
        bool Changed() => !string.Equals(value.ToString(), secret ? "" : current, StringComparison.Ordinal);
        IRenderable View()
        {
            if (pendingNavigation is not null)
            {
                return new Rows(new Text(Ui.Text("edit.pending"), new Style(decoration: Decoration.Bold)),
                    Text.Empty, new Text(breadcrumb), Separator(),
                    new Text(Ui.Text("selection.destination." + pendingNavigation)), Text.Empty,
                    new Text(Ui.Text("edit.confirm")), new Text(Ui.Text("edit.discard")),
                    new Text(Ui.Text("edit.stay")), Text.Empty, Separator(),
                    Keys(Ui.Text("selection.keys")), Text.Empty, SaveStatus(pendingChanges: true));
            }
            List<IRenderable> lines = Header(title);
            string visible = secret ? new string('*', value.Length) : SafeText.Inline(value.ToString());
            lines.Add(new Text("> " + visible, new Style(decoration: Decoration.Bold)).Overflow(Overflow.Ellipsis));
            if (details) { lines.Add(new Text(Ui.Text("Il valore resta nella scheda fino a C Conferma; solo S Salva scrive la configurazione. Nessun segreto compare nei dettagli."), new Style(Color.Grey))); }
            lines.Add(Separator());
            lines.Add(Keys(Ui.Text("Enter conferma campo | Esc annulla | F1 dettagli")));
            lines.Add(Keys(Ui.Text("Ctrl+H home | Ctrl+S salva configurazione | Ctrl+Q esci")));
            lines.Add(Text.Empty);
            lines.Add(SaveStatus(Changed()));
            return new Rows(lines);
        }
        return console.Live(View()).AutoClear(true).Start<string?>(live =>
        {
            live.Refresh();
            while (true)
            {
                ConsoleKeyInfo key = readKey();
                if (Pasting)
                {
                    if (!char.IsControl(key.KeyChar))
                    {
                        if (replace) { value.Clear(); replace = false; }
                        if (value.Length < 8192) { value.Append(key.KeyChar); }
                    }
                    live.UpdateTarget(View()); live.Refresh(); continue;
                }
                if (ReplInterrupt.IsControlC(key))
                {
                    if (interrupt.Press()) { Navigation.Request("quit"); }
                    return null;
                }
                interrupt.Activity();

                if (pendingNavigation is not null)
                {
                    if (key.Key is ConsoleKey.C or ConsoleKey.D)
                    {
                        Navigation.Request(pendingNavigation);
                        return key.Key == ConsoleKey.C ? value.ToString() : null;
                    }
                    if (key.Key is ConsoleKey.B or ConsoleKey.Escape) { pendingNavigation = null; }
                    live.UpdateTarget(View()); live.Refresh(); continue;
                }
                if (key.Key == ConsoleKey.Escape) { return null; }
                if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key is ConsoleKey.H or ConsoleKey.S or ConsoleKey.Q)
                {
                    string destination = key.Key == ConsoleKey.H ? "home" : key.Key == ConsoleKey.S ? "save" : "quit";
                    if (Changed())
                    {
                        pendingNavigation = destination;
                        live.UpdateTarget(View()); live.Refresh(); continue;
                    }
                    Navigation.Request(destination);
                    return null;
                }
                if (key.Key == ConsoleKey.Enter) { return value.ToString(); }
                if (key.Key == ConsoleKey.F1) { details = !details; }
                if (key.Key == ConsoleKey.Backspace && value.Length > 0) { value.Length--; replace = false; }
                if (!char.IsControl(key.KeyChar))
                {
                    if (replace) { value.Clear(); replace = false; }
                    if (value.Length < 8192) { value.Append(key.KeyChar); }
                }
                live.UpdateTarget(View());
                live.Refresh();
            }
        });
    }

    /// <summary>
    /// La testa della gabbia, sempre nello stesso ordine: dove sei, che cosa fai qui, perche', che cosa e'
    /// appena successo. Nessuna schermata sceglie dove mettere queste righe.
    /// </summary>
    private List<IRenderable> Header(string title)
    {
        string[] parts = SafeText.Inline(title).Split(" - ", 2, StringSplitOptions.None);
        string[] path = breadcrumb.Split(" > ", StringSplitOptions.None);
        // Foreground del terminale: chiaro sul tema scuro, leggibile anche sul tema chiaro.
        List<IRenderable> lines = [Text.Empty];

        // L'etichetta attiva prima di tutto, quasi fuori dal setup: cio' che segue vale per lei.
        if (labelName is not null)
        {
            lines.Add(new Markup("[black on cyan1] " + Markup.Escape(Ui.Text("label.banner")) + " [/] [bold]" + Markup.Escape(labelName) + "[/]"));
            lines.Add(new Text(labelSummary, new Style(Color.Grey)));
            lines.Add(Text.Empty);
        }

        lines.Add(new Markup((step is null ? "" : "[cyan1]" + Markup.Escape(step) + "[/]  ")
            + "[grey]" + Markup.Escape(string.Join(" > ", path[..^1])) + (path.Length > 1 ? " > " : "") + "[/]"
            + "[bold]" + Markup.Escape(path[^1]) + "[/]"));
        lines.Add(Separator());
        lines.Add(new Text(parts[0], new Style(decoration: Decoration.Bold)));
        // Lo scopo precede valori e messaggi; le descrizioni non vengono troncate.
        if (parts.Length > 1) { lines.Add(new Text(parts[1], new Style(Color.Grey))); }
        if (!string.IsNullOrEmpty(context) && (parts.Length == 1 || !string.Equals(parts[1], context, StringComparison.Ordinal)))
        {
            lines.Add(new Text(context, new Style(parts.Length > 1 ? Color.Default : Color.Grey)));
        }
        if (missing is int count)
        {
            lines.Add(count == 0 ? new Text(Ui.Text("cage.nothingMissing"), new Style(Color.Green))
                : new Text(Ui.Format("cage.missing {0}", count), new Style(Color.Red, decoration: Decoration.Bold)));
        }
        if (!string.IsNullOrEmpty(pageNotice)) { lines.Add(new Text("» " + pageNotice, new Style(Color.Yellow))); }
        if (profileName is not null)
        {
            lines.Add(Text.Empty);
            lines.Add(new Text(Ui.Text("profile.default"), new Style(Color.Cyan1, decoration: Decoration.Bold)));
            lines.Add(new Text(profileName, new Style(decoration: Decoration.Bold)));
            lines.Add(new Text(profileDescription, new Style(Color.Grey)));
        }
        lines.Add(Text.Empty);
        if (interrupt.Armed) { lines.Add(new Text(Ui.Text("input.exitHint"), new Style(Color.Grey))); }
        return lines;
    }

    private Text SaveStatus(bool pendingChanges = false) => new(dirty || pendingChanges ? Ui.Text("* Modifiche non salvate") : Ui.Text("Nessuna modifica da salvare"),
        new Style(dirty || pendingChanges ? Color.Cyan1 : Color.Grey, decoration: dirty || pendingChanges ? Decoration.Bold : Decoration.None));

    private Text Separator() => new(new string('-', Math.Clamp(console.Profile.Width - 1, 1, 68)), new Style(decoration: Decoration.Dim));

    private static Markup Keys(string actions) => new(string.Join("  |  ", actions.Split(" | ").Select(action =>
    {
        int split = action.IndexOf(' ', StringComparison.Ordinal);
        return "[bold]" + Markup.Escape(action[..split]) + "[/] " + Markup.Escape(action[(split + 1)..]);
    })));
}
