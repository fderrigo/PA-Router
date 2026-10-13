using System.Globalization;
using System.Text;

namespace PA.Router.Cli.Repl;

/// <summary>
/// La conversazione del REPL, tenuta dal router (ADR-024).
///
/// Nello schermo alternativo il terminale non ha scrollback: cio' che e' stato detto esiste solo qui.
/// Chi scrive contenuto — risposte, fasi, rifiuti, tabelle — scrive come su un terminale, con ritorni
/// a capo, cancellazioni di riga e colori; questa classe applica quelle scritture a un elenco di righe
/// logiche e le restituisce avvolte alla larghezza del momento. Le righe restano **logiche**: una riga
/// lunga si ri-avvolge da sola quando la finestra cambia larghezza.
///
/// Conosce le sole sequenze che il REPL scrive nella conversazione: colori e stili, cancellazione della
/// riga, ritorno a inizio riga, riga su. Qualunque altra la ignora invece di interpretarla a caso.
///
/// Conta **un carattere per colonna** (ADR-024): i caratteri larghi possono far andare a capo una riga
/// prima del previsto, ma non si perde testo.
/// </summary>
internal sealed class ReplScrollback
{
    /// <summary>Righe logiche conservate: oltre, le piu' vecchie escono, come nello scrollback di un terminale.</summary>
    internal const int Capacity = 5000;

    /// <summary>Uno stile piu' lungo di cosi' non e' uno stile: e' una sequenza finita per errore nel testo.</summary>
    private const int StyleLimit = 96;

    private readonly List<List<Cell>> lines = [[]];
    private int row;
    private int column;
    private string style = string.Empty;

    /// <summary>Una colonna: il testo (uno o due char, per le coppie surrogate) e lo stile attivo quando e' stato scritto.</summary>
    private readonly record struct Cell(string Text, string Style);

    /// <summary>Numero di righe logiche, compresa quella in corso.</summary>
    internal int Count => lines.Count;

    /// <summary>Applica una scrittura come la applicherebbe un terminale.</summary>
    internal void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) { return; }

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];

            if (character == '\u001b')
            {
                index = Escape(value, index);
                continue;
            }

            switch (character)
            {
                case '\r': column = 0; break;
                case '\n': NewLine(); break;
                case '\b': column = Math.Max(0, column - 1); break;
                case '\t':
                    int next = (column / 8 + 1) * 8;
                    while (column < next) { Put(" "); }
                    break;
                default:
                    if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                    {
                        Put(value.Substring(index, 2));
                        index++;
                    }
                    else if (!char.IsControl(character))
                    {
                        Put(character.ToString());
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Righe da mostrare, avvolte alla larghezza indicata: le ultime <paramref name="count"/> prima delle
    /// <paramref name="skip"/> piu' recenti. La riga in corso, se vuota, non si mostra: e' il punto dove
    /// arrivera' il testo successivo, non una riga della conversazione.
    /// </summary>
    internal IReadOnlyList<string> Rows(int width, int count, int skip = 0)
    {
        width = Math.Max(1, width);
        List<string> collected = [];
        int toSkip = Math.Max(0, skip);

        for (int index = Visible() - 1; index >= 0 && collected.Count < count; index--)
        {
            List<string> wrapped = Wrap(lines[index], width);

            for (int part = wrapped.Count - 1; part >= 0 && collected.Count < count; part--)
            {
                if (toSkip > 0) { toSkip--; continue; }
                collected.Add(wrapped[part]);
            }
        }

        collected.Reverse();
        return collected;
    }

    /// <summary>Righe visive dell'intera conversazione alla larghezza indicata: serve a sapere fin dove si puo' scorrere.</summary>
    internal int TotalRows(int width)
    {
        width = Math.Max(1, width);
        int total = 0;

        for (int index = 0; index < Visible(); index++)
        {
            total += Math.Max(1, (lines[index].Count + width - 1) / width);
        }

        return total;
    }

    /// <summary>
    /// La conversazione per intero, una riga logica per riga con i suoi colori: e' cio' che si ristampa nel
    /// terminale uscendo, dove il terminale la avvolgera' da se'.
    /// </summary>
    internal IEnumerable<string> Transcript()
    {
        for (int index = 0; index < Visible(); index++)
        {
            yield return Render(lines[index], 0, lines[index].Count);
        }
    }

    private int Visible() => lines[^1].Count == 0 && lines.Count > 1 ? lines.Count - 1 : lines.Count;

    private void Put(string text)
    {
        List<Cell> line = lines[row];

        while (line.Count < column) { line.Add(new Cell(" ", string.Empty)); }

        Cell cell = new(text, style);

        if (column < line.Count) { line[column] = cell; }
        else { line.Add(cell); }

        column++;
    }

    private void NewLine()
    {
        row++;
        column = 0;

        if (row == lines.Count) { lines.Add([]); }

        if (lines.Count > Capacity)
        {
            int excess = lines.Count - Capacity;
            lines.RemoveRange(0, excess);
            row = Math.Max(0, row - excess);
        }
    }

    /// <summary>Interpreta una sequenza di escape e restituisce l'indice del suo ultimo carattere.</summary>
    private int Escape(string value, int start)
    {
        if (start + 1 >= value.Length) { return start; }

        char kind = value[start + 1];

        // OSC — collegamenti, titolo della finestra: si salta fino al terminatore, senza interpretarlo.
        if (kind == ']')
        {
            for (int index = start + 2; index < value.Length; index++)
            {
                if (value[index] == '\a') { return index; }
                if (value[index] == '\u001b' && index + 1 < value.Length && value[index + 1] == '\\') { return index + 1; }
            }

            return value.Length - 1;
        }

        if (kind != '[') { return start + 1; }

        int end = start + 2;
        while (end < value.Length && value[end] is < '@' or > '~') { end++; }
        if (end >= value.Length) { return value.Length - 1; }

        string parameters = value[(start + 2)..end];
        int First(int fallback) =>
            int.TryParse(parameters.Split(';')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
                ? parsed : fallback;

        switch (value[end])
        {
            case 'm':
                if (parameters.Length == 0 || parameters == "0") { style = string.Empty; }
                else
                {
                    string sequence = "\u001b[" + parameters + "m";
                    style = style.Length + sequence.Length > StyleLimit ? sequence : style + sequence;
                }

                break;

            case 'K':
                List<Cell> line = lines[row];
                if (parameters == "2") { line.Clear(); }
                else if (parameters is "" or "0" && column < line.Count) { line.RemoveRange(column, line.Count - column); }
                break;

            case 'A': row = Math.Max(0, row - First(1)); break;
            case 'C': column += First(1); break;
            case 'D': column = Math.Max(0, column - First(1)); break;
            case 'G': column = First(1) - 1; break;
        }

        return end;
    }

    private static List<string> Wrap(List<Cell> line, int width)
    {
        List<string> parts = [];

        if (line.Count == 0) { parts.Add(string.Empty); return parts; }

        for (int start = 0; start < line.Count; start += width)
        {
            parts.Add(Render(line, start, Math.Min(width, line.Count - start)));
        }

        return parts;
    }

    /// <summary>Un tratto di riga con i suoi colori, che si chiude sempre senza stile attivo.</summary>
    private static string Render(List<Cell> line, int start, int length)
    {
        StringBuilder text = new();
        string current = string.Empty;

        for (int index = start; index < start + length; index++)
        {
            Cell cell = line[index];

            if (cell.Style != current)
            {
                text.Append("\u001b[0m").Append(cell.Style);
                current = cell.Style;
            }

            text.Append(cell.Text);
        }

        if (current.Length > 0) { text.Append("\u001b[0m"); }

        return text.ToString();
    }
}
