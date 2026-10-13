using PA.Router.Cli.Output;
using Spectre.Console;

namespace PA.Router.Cli.Repl;

internal enum SetupVisualState
{
    RequiredMissing,
    RequiredSatisfied,
    Optional,
    Warning,
}

/// <summary>
/// Superficie di resa del REPL.
///
/// Esiste per una ragione di sicurezza prima che di estetica: il testo che arriva da un provider
/// non e' dato fidato. Se finisse dentro il markup di Spectre, una risposta potrebbe colorare
/// l'output, fingere una riga del router o emettere sequenze di terminale. Qui quel testo passa da
/// un solo metodo, <see cref="Answer"/>, che non lo interpreta mai.
///
/// Tabelle e dettagli riusano <see cref="IOutputSurface"/>. La conversazione usa ReplChatView
/// per distinguere ruoli e coordinare output rilasciato, attivita e input senza sovrapporli.
/// </summary>
internal interface IReplRenderer
{
    void Welcome(string profile, string location = "") => Header([(Ui.Text("profile.session"), profile)]);
    void Command(string command) { }
    void BeginTurn() { }
    void EndTurn() { }
    void BeginInput() { }
    void EndInput() { }
    void Event(string message) => Phase(message);
    void Outcome(string state) { }
    void Error(string message) => Warn(message);
    void User(string text) => Info(Ui.Text("chat.you") + " > " + ReplText.Sanitize(text));
    void Confirmation(string question, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        Warn(question);
        Table([Ui.Text("cosa"), Ui.Text("dettaglio")], rows);
        Warn(Ui.Text("Confermi? (si / una volta / no)"));
    }

    /// <summary>Intestazione della sessione: profilo, sessione, contenuti, target, servizi.</summary>
    void Header(IReadOnlyList<(string Label, string Value)> rows);

    /// <summary>Riga di stato durante un turno. Non contiene mai testo del provider.</summary>
    void Phase(string message);

    /// <summary>Riga informativa del router.</summary>
    void Info(string message);

    /// <summary>Avviso o errore del router.</summary>
    void Warn(string message);

    /// <summary>Stato del configuratore: il testo resta leggibile anche senza colori.</summary>
    void SetupStatus(SetupVisualState state, string label, string description);

    /// <summary>
    /// Dichiara che l'invito vive in un riquadro fisso in fondo (ADR-023): le rese che nominavano le
    /// parti della conversazione smettono di farlo, perche' la forma lo dice gia'.
    /// </summary>
    void Framed() { }

    /// <summary>Inizia e ridisegna la regione temporanea del setup, senza cancellare lo scrollback.</summary>
    void BeginSetupRegion();
    void RedrawSetupRegion();
    void EndSetupRegion();

    /// <summary>Risposta del provider: **dato non fidato**, reso senza interpretarlo.</summary>
    void Answer(string text);

    /// <summary>Tabella con intestazioni e righe gia' note al router.</summary>
    void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows);

    /// <summary>Invito di riga.</summary>
    void Prompt();

    /// <summary>
    /// Il racconto di un turno (S117): un titolo, poi sezioni con un'intestazione e dei punti. Senza colori e'
    /// testo semplice, con i dati senza segni.
    /// </summary>
    void Story(string title, IReadOnlyList<StorySection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        Info(string.Empty);
        Info(title);

        foreach (StorySection section in sections)
        {
            Info(section.Label + (section.Value is null ? string.Empty : ": " + ReplStory.Plain(section.Value)));

            foreach (string line in section.Lines) { Info("  · " + ReplStory.Plain(line)); }
        }
    }
}

/// <summary>
/// Resa in testo semplice, senza colori ne' pannelli.
///
/// E' quella dei test e quella che la specifica impone quando manca un terminale o
/// <c>NO_COLOR</c> e' impostata: leggibile a ottanta colonne e adatta a uno screen reader.
/// </summary>
internal sealed class PlainReplRenderer(TextWriter output, TextWriter error) : IReplRenderer
{
    private readonly PlainSurface surface = new(output, error);
    private readonly ReplChatView chat = new(output, error);
    public void Welcome(string profile, string location = "") => chat.Welcome(profile, location);
    public void Command(string command) => chat.Command(command);
    public void BeginTurn() => chat.BeginTurn();
    public void EndTurn() => chat.EndTurn();
    public void BeginInput() => chat.BeginInput();
    public void EndInput() => chat.EndInput();
    public void Event(string message) => chat.Event(message);
    public void Outcome(string state) => chat.Outcome(state);
    public void Error(string message) => chat.Warning(message, blocking: true);
    public void User(string text) => chat.Message(Ui.Text("chat.you") + " > " + ReplText.Sanitize(text));
    public void Confirmation(string question, IReadOnlyList<IReadOnlyList<string>> rows) =>
        chat.Confirmation(question, () => surface.Table([Ui.Text("cosa"), Ui.Text("dettaglio")], rows));


    public void Header(IReadOnlyList<(string Label, string Value)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        output.WriteLine(Ui.Text("PA Router"));
        surface.Labels(rows, indent: 2);
    }

    public void Phase(string message) => chat.Phase(message);

    public void Info(string message) => chat.Message(message);

    public void Warn(string message) => chat.Warning(message);

    public void Framed() => chat.Framed = true;

    public void SetupStatus(SetupVisualState state, string label, string description) =>
        output.WriteLine("  " + Ui.Text(SetupVisualText.Symbol(state)) + " " + SafeText.Inline(label) + " — " + SafeText.Inline(description));

    public void BeginSetupRegion() { }
    public void RedrawSetupRegion() { }
    public void EndSetupRegion() { }

    public void Answer(string text) => chat.Answer(text);

    public void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows) =>
        chat.Render(() => surface.Table(headers, rows));

    public void Prompt() => output.Write("> ");
}

/// <summary>
/// Resa con Spectre.Console: regole, colori e tabelle per cio' che il router genera.
///
/// La risposta del provider **non** passa dal markup. E' l'unico modo di dare per scontato che sia
/// ostile senza dover ricordare un escape in ogni punto di stampa: il percorso pericoloso e' uno
/// solo e non interpreta nulla.
/// </summary>
internal sealed class SpectreReplRenderer(IAnsiConsole console, TextWriter output, TextWriter error) : IReplRenderer
{
    private readonly RichSurface surface = new(console, output, error);
    private readonly ReplChatView chat = new(output, error, console);
    public void Welcome(string profile, string location = "") => chat.Welcome(profile, location);
    public void Command(string command) => chat.Command(command);
    public void BeginTurn() => chat.BeginTurn();
    public void EndTurn() => chat.EndTurn();
    public void BeginInput() => chat.BeginInput();
    public void EndInput() => chat.EndInput();
    public void Event(string message) => chat.Event(message);
    public void Outcome(string state) => chat.Outcome(state);
    public void Error(string message) => chat.Warning(message, blocking: true);
    public void User(string text) => chat.Message(Ui.Text("chat.you") + " > " + ReplText.Sanitize(text));
    public void Confirmation(string question, IReadOnlyList<IReadOnlyList<string>> rows) =>
        chat.Confirmation(question, () => surface.Table([Ui.Text("cosa"), Ui.Text("dettaglio")], rows));


    public void Header(IReadOnlyList<(string Label, string Value)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .Title(Ui.Text("[bold]PA Router[/]"))
            .AddColumn(new TableColumn("[dim]campo[/]"))
            .AddColumn(new TableColumn("[dim]valore[/]"));

        foreach ((string label, string value) in rows)
        {
            table.AddRow(new Text(SafeText.Inline(label)), new Text(SafeText.Inline(value)));
        }

        console.Write(table);
    }

    public void Phase(string message) => chat.Phase(message);

    public void Info(string message) => chat.Message(message);

    /// <summary>
    /// Il racconto a colori: il titolo nel colore del router, ogni intestazione in grassetto con il suo valore
    /// in evidenza, i punti con i dati in grassetto. I testi passano comunque dalla pulizia di ogni riga: i colori
    /// li mette la resa, non il testo.
    /// </summary>
    public void Story(string title, IReadOnlyList<StorySection> sections)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(sections);

        static string Rich(string text) => string.Concat(ReplStory.Parts(text).Select(part => part.Marked
            ? "[bold]" + Markup.Escape(SafeText.Inline(part.Text)) + "[/]"
            : Markup.Escape(SafeText.Inline(part.Text))));

        chat.Render(() =>
        {
            console.WriteLine();
            console.MarkupLine("[bold rgb(0,128,128)]" + Markup.Escape(SafeText.Inline(title)) + "[/]");

            foreach (StorySection section in sections)
            {
                console.WriteLine();
                console.MarkupLine("[bold]" + Markup.Escape(SafeText.Inline(section.Label)) + "[/]"
                    + (section.Value is null ? string.Empty : "  [bold rgb(215,135,0)]" + Markup.Escape(SafeText.Inline(ReplStory.Plain(section.Value))) + "[/]"));

                foreach (string line in section.Lines)
                {
                    console.MarkupLine("  [grey]·[/] " + Rich(line));
                }
            }
        });
    }

    /// <summary>
    /// Gli avvisi vanno sull'errore standard anche qui. Prima finivano sullo standard output
    /// insieme alle risposte: chi filtrava l'uscita di un turno si ritrovava dentro i dinieghi.
    /// </summary>
    public void Warn(string message) => chat.Warning(message);

    public void Framed() => chat.Framed = true;

    public void SetupStatus(SetupVisualState state, string label, string description)
    {
        string style = state switch
        {
            SetupVisualState.RequiredMissing => "red",
            SetupVisualState.RequiredSatisfied => "green",
            SetupVisualState.Warning => "yellow",
            _ => "grey",
        };
        console.MarkupLine("  [" + style + "]" + Markup.Escape(Ui.Text(SetupVisualText.Symbol(state)) + " " + SafeText.Inline(label))
            + Ui.Text("[/] [dim]— ") + Markup.Escape(SafeText.Inline(description)) + "[/]");
    }

    public void BeginSetupRegion() => output.Write("\u001b7");

    public void RedrawSetupRegion()
    {
        output.Write("\u001b8\u001b[0J\u001b7");
        output.Flush();
    }

    public void EndSetupRegion()
    {
        output.Write("\u001b8\n");
        output.Flush();
    }

    /// <summary>
    /// La risposta viene scritta direttamente sul flusso, mai attraverso il markup: una risposta
    /// che contiene <c>[red]</c> deve comparire cosi' com'e', non colorare il terminale.
    /// </summary>
    public void Answer(string text) => chat.Answer(text);

    public void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows) =>
        chat.Render(() => surface.Table(headers, rows));

    public void Prompt() => console.Markup(Ui.Text("[bold]> [/]"));
}

internal static class SetupVisualText
{
    internal static string Symbol(SetupVisualState state) => state switch
    {
        SetupVisualState.RequiredMissing => "[obbligatorio]",
        SetupVisualState.RequiredSatisfied => "[completo]",
        SetupVisualState.Warning => "[warning]",
        _ => "[opzionale]",
    };
}

/// <summary>
/// Testo non fidato reso sicuro da mostrare.
///
/// Dalla S57 la funzione vive in <see cref="SafeText"/>, insieme al resto della resa: qui resta il
/// nome storico perche' e' quello che il REPL usa nei propri punti di chiamata.
/// </summary>
internal static class ReplText
{
    /// <summary>Toglie le sequenze di controllo da un testo non fidato, lasciando tabulazione e a capo.</summary>
    internal static string Sanitize(string text) => SafeText.Sanitize(text);
}
