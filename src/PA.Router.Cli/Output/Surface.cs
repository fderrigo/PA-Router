using System.Text;
using Spectre.Console;

namespace PA.Router.Cli.Output;

/// <summary>
/// Testo non fidato reso sicuro da mostrare.
///
/// Fino alla S56 questa funzione viveva nel REPL e aveva **tre** punti di chiamata: la risposta del
/// provider e il titolo di un ricordo. Tutto il resto — contenuto dei ricordi, testo delle skill,
/// nomi e indirizzi degli strumenti, ragioni di guasto che arrivano da un server di terzi, codici
/// delle probe, percorsi, messaggi di eccezione — raggiungeva il terminale come arrivava. Fuori dal
/// REPL non c'era ne' sanificazione ne' escape.
///
/// Non e' una questione di estetica: le sequenze di escape arrivano fino allo schermo anche quando
/// nessuno interpreta un markup, e possono spostare il cursore o riscrivere righe gia' stampate dal
/// router. Un server MCP che dichiara un nome con dentro una sequenza ANSI non deve poter disegnare
/// sul terminale di chi esegue <c>pa doctor</c>.
/// </summary>
internal static class SafeText
{
    /// <summary>
    /// Toglie le sequenze di controllo, lasciando tabulazione e a capo. Un valore nullo diventa
    /// stringa vuota: una cella assente non e' un guasto della resa.
    /// </summary>
    internal static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder safe = new(text.Length);

        foreach (char character in text)
        {
            if (character is '\n' or '\t' || !char.IsControl(character))
            {
                safe.Append(character);
            }
        }

        return safe.ToString();
    }

    /// <summary>Come <see cref="Sanitize"/>, ma su una riga sola: anche gli a capo spariscono.</summary>
    internal static string Inline(string? text) =>
        Sanitize(text).Replace('\n', ' ').Replace('\t', ' ');
}

/// <summary>
/// Larghezza utile del terminale.
///
/// Nessun punto del prodotto la leggeva: le colonne erano numeri scritti a mano nei letterali, e un
/// identificatore lungo le sfondava in silenzio. Senza terminale — pipe, file, prove — vale ottanta,
/// che e' la larghezza che la specifica pretende resti leggibile (03-specs/CLI-TUI.md).
/// </summary>
internal static class Terminal
{
    /// <summary>Larghezza usata quando non c'e' un terminale da cui leggerla.</summary>
    internal const int DefaultWidth = 80;

    /// <summary>Larghezza minima sotto la quale non si scende: sotto, la resa diventa illeggibile.</summary>
    internal const int MinimumWidth = 40;

    /// <summary>Larghezza massima considerata: oltre, le righe lunghe stancano piu' che aiutare.</summary>
    internal const int MaximumWidth = 160;

    /// <summary>Larghezza corrente, gia' limitata.</summary>
    internal static int Width => Measure(() => Console.WindowWidth, Console.IsOutputRedirected);

    /// <summary>Forma verificabile: la prova fornisce la misura e la condizione, senza un terminale.</summary>
    internal static int Measure(Func<int> width, bool redirected)
    {
        ArgumentNullException.ThrowIfNull(width);

        if (redirected)
        {
            return DefaultWidth;
        }

        try
        {
            int measured = width();

            return measured < MinimumWidth
                ? MinimumWidth
                : Math.Min(measured, MaximumWidth);
        }
        catch (IOException)
        {
            // Una console che non sa dire la propria larghezza non e' un guasto del comando.
            return DefaultWidth;
        }
        catch (PlatformNotSupportedException)
        {
            return DefaultWidth;
        }
    }
}

/// <summary>
/// Superficie di resa, unica per il REPL e per i comandi.
///
/// Prima erano due mondi: il REPL passava da <c>IReplRenderer</c>, i comandi scrivevano diretti sul
/// <c>TextWriter</c>. Ne seguivano otto larghezze di colonna diverse, due tabelle scritte a mano con
/// convenzioni incompatibili, e un sanificatore che valeva solo da una parte. Una resa sola vuol
/// dire un allineamento solo e **un sanificatore solo**.
///
/// Ogni valore che attraversa questa superficie viene sanificato: non esiste un percorso in cui un
/// testo di terze parti arrivi allo schermo senza passare di qui.
/// </summary>
internal interface IOutputSurface
{
    /// <summary>Larghezza utile in colonne.</summary>
    int Width { get; }

    /// <summary>Riga scritta dal router. Viene comunque sanificata: puo' contenere valori esterni.</summary>
    void Line(string text);

    /// <summary>Riga vuota.</summary>
    void Blank();

    /// <summary>Avviso o errore. Va sull'errore standard, come prima.</summary>
    void Warn(string text);

    /// <summary>
    /// Blocco etichetta/valore, con la colonna calcolata **dai dati** invece che scritta a mano.
    /// I valori lunghi vanno a capo sotto la colonna del valore, non sotto l'etichetta.
    /// </summary>
    void Labels(IReadOnlyList<(string Label, string Value)> rows, int indent = 0);

    /// <summary>Tabella con colonne calcolate dai dati e adattate alla larghezza del terminale.</summary>
    void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows);

    /// <summary>Separatore con titolo. Nella resa piana e' una riga di trattini.</summary>
    void Rule(string title);

    /// <summary>
    /// Testo di un fornitore o di un terzo: **dato non fidato**, reso senza interpretarlo mai.
    /// </summary>
    void Untrusted(string text);
}

/// <summary>Calcoli di allineamento condivisi dalle due rese.</summary>
internal static class Layout
{
    /// <summary>Spezza un testo su piu' righe rispettando la larghezza, senza tagliare le parole.</summary>
    internal static IReadOnlyList<string> Wrap(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (width < 1)
        {
            return [text];
        }

        List<string> lines = [];

        foreach (string paragraph in text.Split('\n'))
        {
            if (paragraph.Length <= width)
            {
                lines.Add(paragraph);
                continue;
            }

            StringBuilder current = new();

            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.Length > 0 && current.Length + 1 + word.Length > width)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }

                if (word.Length > width)
                {
                    // Una parola piu' lunga della riga — un percorso, un digest — si spezza, perche'
                    // l'alternativa e' sfondare la larghezza in silenzio.
                    if (current.Length > 0)
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                    }

                    for (int index = 0; index < word.Length; index += width)
                    {
                        lines.Add(word.Substring(index, Math.Min(width, word.Length - index)));
                    }

                    continue;
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(word);
            }

            if (current.Length > 0)
            {
                lines.Add(current.ToString());
            }
        }

        return lines.Count == 0 ? [string.Empty] : lines;
    }

    /// <summary>Accorcia con un carattere di continuazione quando non c'e' spazio.</summary>
    internal static string Truncate(string text, int width) =>
        width < 1 ? string.Empty
        : text.Length <= width ? text
        : width == 1 ? "…"
        : text[..(width - 1)] + "…";

    /// <summary>
    /// Larghezze delle colonne: quella che serve a ciascuna, ridotta proporzionalmente finche' la
    /// somma sta nella larghezza disponibile. Si restringe sempre la colonna piu' larga: e' quella
    /// che ha piu' da perdere e meno da soffrire.
    /// </summary>
    internal static int[] Columns(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows,
        int available,
        int separator = 2)
    {
        int[] widths = [.. headers.Select(header => header.Length)];

        foreach (IReadOnlyList<string> row in rows)
        {
            for (int column = 0; column < widths.Length && column < row.Count; column++)
            {
                widths[column] = Math.Max(widths[column], row[column].Length);
            }
        }

        int total() => widths.Sum() + (separator * (widths.Length - 1));

        while (total() > available && widths.Length > 0)
        {
            int widest = Array.IndexOf(widths, widths.Max());

            if (widths[widest] <= 3)
            {
                break;
            }

            widths[widest]--;
        }

        return widths;
    }
}

/// <summary>
/// Resa in testo semplice: nessun colore, nessun bordo, nessuna animazione.
///
/// E' quella delle prove, delle pipe, di <c>NO_COLOR</c> e dei lettori di schermo. La specifica la
/// pretende leggibile a ottanta colonne, e adesso lo e' per costruzione invece che per fortuna.
/// </summary>
internal sealed class PlainSurface(TextWriter output, TextWriter error, int? width = null) : IOutputSurface
{
    /// <inheritdoc />
    public int Width => width ?? Terminal.Width;

    /// <inheritdoc />
    public void Line(string text)
    {
        foreach (string line in Layout.Wrap(SafeText.Sanitize(text), Width))
        {
            output.WriteLine(line);
        }
    }

    /// <inheritdoc />
    public void Blank() => output.WriteLine();

    /// <inheritdoc />
    public void Warn(string text) => error.WriteLine(SafeText.Inline(text));

    /// <inheritdoc />
    public void Labels(IReadOnlyList<(string Label, string Value)> rows, int indent = 0)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return;
        }

        int column = rows.Max(row => SafeText.Inline(row.Label).Length) + 2;
        string margin = new(' ', indent);

        foreach ((string label, string value) in rows)
        {
            string head = margin + SafeText.Inline(label).PadRight(column);
            IReadOnlyList<string> lines = Layout.Wrap(SafeText.Sanitize(value), Math.Max(Width - head.Length, 8));

            output.WriteLine(head + lines[0]);

            foreach (string line in lines.Skip(1))
            {
                output.WriteLine(new string(' ', head.Length) + line);
            }
        }
    }

    /// <inheritdoc />
    public void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        string[] safeHeaders = [.. headers.Select(SafeText.Inline)];
        List<IReadOnlyList<string>> safeRows = [.. rows.Select(row => (IReadOnlyList<string>)[.. row.Select(SafeText.Inline)])];

        int[] widths = Layout.Columns(safeHeaders, safeRows, Width);

        output.WriteLine(Row(safeHeaders, widths));

        foreach (IReadOnlyList<string> row in safeRows)
        {
            output.WriteLine(Row(row, widths));
        }
    }

    /// <inheritdoc />
    public void Rule(string title) =>
        output.WriteLine("-- " + SafeText.Inline(title) + " " + new string('-', Math.Max(Width - title.Length - 4, 0)));

    /// <inheritdoc />
    public void Untrusted(string text) => output.WriteLine(SafeText.Sanitize(text));

    private static string Row(IReadOnlyList<string> cells, int[] widths)
    {
        StringBuilder line = new();

        for (int column = 0; column < widths.Length; column++)
        {
            if (column > 0)
            {
                line.Append("  ");
            }

            string cell = column < cells.Count ? Layout.Truncate(cells[column], widths[column]) : string.Empty;

            line.Append(column == widths.Length - 1 ? cell : cell.PadRight(widths[column]));
        }

        return line.ToString().TrimEnd();
    }
}

/// <summary>
/// Resa con Spectre.Console: bordi e colori per cio' che genera il router.
///
/// Il testo non fidato **non** passa dal markup, nemmeno qui: <see cref="Untrusted"/> scrive
/// direttamente sul flusso. E' l'unico modo di dare per scontato che sia ostile senza dover
/// ricordare un escape in ogni punto di stampa.
/// </summary>
internal sealed class RichSurface(IAnsiConsole console, TextWriter output, TextWriter error) : IOutputSurface
{
    /// <inheritdoc />
    public int Width => Terminal.Width;

    /// <inheritdoc />
    public void Line(string text) => console.MarkupLine(Markup.Escape(SafeText.Sanitize(text)));

    /// <inheritdoc />
    public void Blank() => console.WriteLine();

    /// <inheritdoc />
    public void Warn(string text) => error.WriteLine(SafeText.Inline(text));

    /// <inheritdoc />
    public void Labels(IReadOnlyList<(string Label, string Value)> rows, int indent = 0)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return;
        }

        // Il rientro sta sulla colonna, non sulla griglia: `Grid` non e' paddable in questa versione
        // di Spectre, `GridColumn` si'.
        Grid grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadLeft(indent).PadRight(2))
            .AddColumn();

        foreach ((string label, string value) in rows)
        {
            grid.AddRow(
                new Markup("[dim]" + Markup.Escape(SafeText.Inline(label)) + "[/]"),
                new Text(SafeText.Sanitize(value)));
        }

        console.Write(grid);
    }

    /// <inheritdoc />
    public void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        Table table = new Table().Border(TableBorder.Minimal).Expand();

        foreach (string header in headers)
        {
            table.AddColumn(new TableColumn("[dim]" + Markup.Escape(SafeText.Inline(header)) + "[/]"));
        }

        foreach (IReadOnlyList<string> row in rows)
        {
            table.AddRow([.. row.Select(cell => new Text(SafeText.Inline(cell)))]);
        }

        console.Write(table);
    }

    /// <inheritdoc />
    public void Rule(string title) =>
        console.Write(new Rule("[dim]" + Markup.Escape(SafeText.Inline(title)) + "[/]") { Justification = Justify.Left });

    /// <inheritdoc />
    public void Untrusted(string text) => output.WriteLine(SafeText.Sanitize(text));
}

/// <summary>Scelta della resa: la stessa regola per il REPL e per i comandi.</summary>
internal static class Surfaces
{
    /// <summary>
    /// Resa da usare. Senza terminale o con <c>NO_COLOR</c> niente bordi ne' colori: la specifica lo
    /// impone, un bordo dentro una pipe e' illeggibile e un lettore di schermo legge i trattini.
    /// </summary>
    internal static IOutputSurface For(TextWriter output, TextWriter error, bool rich) =>
        rich
            ? new RichSurface(AnsiConsole.Console, output, error)
            : new PlainSurface(output, error);

    /// <summary>
    /// Scelta dell'operatore sul colore (S64). Null finche' nessuno la dichiara: in quel caso
    /// decidono terminale e <c>NO_COLOR</c>, come prima.
    ///
    /// La scelta vive **in un punto solo**. Prima il colore si decideva in due — qui e nella resa
    /// del REPL — e due punti che decidono la stessa cosa finiscono per deciderla diversamente.
    /// </summary>
    internal static bool? Forced { get; private set; }

    /// <summary>
    /// Fissa la scelta per questo processo: <c>always</c>, <c>never</c>, oppure <c>auto</c> che la
    /// rimette all'ambiente.
    /// </summary>
    /// <param name="choice">Valore dell'opzione, gia' validato dal parser.</param>
    internal static void UseColor(bool? choice) => Forced = choice;

    /// <summary>
    /// Vero se questo processo puo' permettersi bordi e colori.
    ///
    /// L'operatore ha l'ultima parola: <c>--color always</c> serve a chi cattura l'uscita e vuole
    /// comunque le sequenze, <c>--color never</c> a chi ha un terminale e non le vuole. Senza una
    /// scelta esplicita valgono il terminale e <c>NO_COLOR</c>, che restano il caso normale.
    /// </summary>
    internal static bool CanDecorate =>
        Forced ?? (!Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null);
}
