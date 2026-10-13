using System.Text;
using System.Text.RegularExpressions;
using PA.Router.Cli.Localization;
using PA.Router.Hosting.Runtime;
using Spectre.Console;

namespace PA.Router.Cli.Repl;

/// <summary>
/// Lo schermo del REPL: la conversazione in alto, il riquadro dell'invito e la barra di stato fissi in
/// basso (ADR-023 per che cosa si vede, ADR-024 per come).
///
/// **Una penna sola.** Il REPL lavora nello schermo alternativo e questa classe possiede tutto cio' che vi
/// compare. La conversazione non sta sullo schermo ma in <see cref="ReplScrollback"/>: chi scrive contenuto
/// scrive su <see cref="Writer"/>, che la registra, e lo schermo e' un **fotogramma** composto da qui — le
/// righe della conversazione che si vedono, poi l'area fissa. Si scrivono solo le righe che cambiano.
///
/// Non ci sono regioni di scorrimento ne' salvataggi del cursore. ADR-023 li usava e la rotella del mouse
/// muoveva comunque riquadro e barra, perche' scorreva lo scrollback del terminale, di cui erano righe;
/// il ridimensionamento lasciava residui; il configuratore lasciava uno schermo che nessuno sapeva
/// ricostruire. Qui il cursore lo mette sempre questa classe, lo scorrimento e' un suo stato, e ridisegnare
/// da capo e' sempre possibile, perche' niente di cio' che si vede esiste solo sullo schermo.
///
/// **Senza ANSI non esiste.** Su un terminale che non lo dichiara, con l'uscita rediretta o dentro un
/// copione, niente di tutto questo si attiva e la resa resta quella a righe di sempre.
/// </summary>
internal sealed class ReplBottomRegion : IDisposable
{
    /// <summary>Righe dell'area fissa senza il testo: bordo, riga dell'invito, bordo, stato.</summary>
    private const int Fixed = 4;

    /// <summary>Righe minime che devono restare alla conversazione: sotto, lo schermo non si apre.</summary>
    private const int MinimumScrolling = 3;

    private readonly IAnsiConsole? console;
    private readonly TextWriter output;
    private readonly IKeySource? keys;
    private readonly bool enabled;
    private readonly object gate = new();
    private readonly ReplScrollback scrollback = new();

    /// <summary>Vero dopo l'ingresso nello schermo alternativo.</summary>
    private bool active;

    /// <summary>Momenti modali aperti: finche' ce n'e' uno, lo schermo e' di qualcun altro.</summary>
    private int modal;

    /// <summary>Il fotogramma gia' sullo schermo; null quando va ridisegnato da capo.</summary>
    private string[]? shown;

    private int rows;
    private int columns;

    /// <summary>Righe visive dal fondo: zero segue la conversazione, di piu' guarda indietro.</summary>
    private int offset;

    /// <summary>Righe arrivate mentre si guardava indietro.</summary>
    private int unseen;

    /// <summary>Righe della conversazione nell'ultimo fotogramma: e' la pagina di PgSu e PgGiu'.</summary>
    private int contentRows = 1;

    private string prompt = "> ";
    private string text = string.Empty;
    private int cursor;

    internal ReplBottomRegion(IAnsiConsole? console, TextWriter output, IKeySource? keys = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        this.console = console;
        this.output = output;
        this.keys = keys;
        enabled = console is { Profile.Capabilities.Ansi: true } && console.Profile.Height > Fixed + MinimumScrolling;
        Writer = enabled ? new ConversationWriter(this, output) : output;

        // Un processo che muore senza passare da Dispose lascerebbe il terminale nello schermo alternativo,
        // con il mouse catturato: chi lo usa dopo non capirebbe perche'.
        if (enabled) { AppDomain.CurrentDomain.ProcessExit += OnProcessExit; }
    }

    /// <summary>Vero se lo schermo del REPL e' in uso: senza, il REPL disegna come ha sempre fatto.</summary>
    internal bool Enabled => enabled;

    /// <summary>
    /// Il flusso su cui scrivere la conversazione. Con lo schermo del REPL va nel buffer e ridisegna; senza,
    /// e' il flusso originale e non fa nulla di speciale.
    /// </summary>
    internal TextWriter Writer { get; }

    /// <summary>Stato mostrato nella barra; null la lascia vuota.</summary>
    internal StatusView? Status { get; set; }

    /// <summary>Livello di dettaglio, mostrato accanto allo stato (ADR-023).</summary>
    internal bool Verbose { get; set; }

    /// <summary>Vero quando la vista segue la conversazione invece di guardare indietro.</summary>
    internal bool Following => offset == 0;

    /// <summary>Dove e' cominciata la selezione in corso; null quando non si sta selezionando.</summary>
    private (int Row, int Column)? anchor;

    /// <summary>Dove arriva la selezione adesso.</summary>
    private (int Row, int Column) reach;

    /// <summary>Il fotogramma senza colori: e' da qui che si copia, perche' e' cio' che l'operatore legge.</summary>
    private string[] plain = [];

    /// <summary>Un avviso breve nella barra, come «copiato»: sparisce al tasto successivo.</summary>
    private string? notice;

    /// <summary>
    /// Dove va il testo selezionato. Di norma gli appunti del sistema; le prove ne passano uno proprio, perche'
    /// una prova non deve riscrivere gli appunti di chi la esegue.
    /// </summary>
    internal Func<string, bool>? Copy { get; init; }

    /// <summary>Sequenze di colore e di cursore, da togliere per avere il testo che si vede.</summary>
    private static readonly Regex Sequences = new("\u001b\\[[0-9;?]*[A-Za-z]", RegexOptions.CultureInvariant);

    /// <summary>
    /// Un evento di selezione del mouse (ADR-024). Nello schermo del REPL il terminale non seleziona piu': i clic
    /// li riceve il router insieme alla rotella, quindi la selezione la fa lui. Si trascina con il tasto sinistro,
    /// il testo si evidenzia, e al rilascio va negli appunti.
    /// </summary>
    internal void Select(ConsoleKeyInfo key, (int Row, int Column) at)
    {
        if (!enabled) { return; }

        lock (gate)
        {
            if (key.Key == ReplKeys.Press.Key)
            {
                anchor = at;
                reach = at;
                notice = null;
            }
            else if (key.Key == ReplKeys.Drag.Key && anchor is not null)
            {
                reach = at;
            }
            else if (key.Key == ReplKeys.Release.Key && anchor is { } start)
            {
                reach = at;
                string? text = start == at ? null : Selected(start, at);
                anchor = null;

                // Un clic senza trascinare non e' una selezione: non si tocca cio' che c'e' negli appunti.
                if (!string.IsNullOrEmpty(text))
                {
                    bool copied = Copy is { } copy ? copy(text) : ReplClipboard.Copy(text, output);
                    notice = copied ? Ui.Text("clip.copied") : Ui.Text("clip.failed");
                }
            }

            Paint();
        }
    }

    /// <summary>Un avviso breve nella barra di stato, fino al tasto successivo.</summary>
    internal void Notice(string message)
    {
        if (!enabled) { return; }

        lock (gate)
        {
            notice = message;
            Paint();
        }
    }

    /// <summary>
    /// Il testo fra due punti dello schermo, come lo selezionerebbe un terminale: dal primo punto a fine riga, le
    /// righe intere in mezzo, l'ultima fino al secondo punto. Gli spazi in coda a ogni riga si tolgono.
    /// </summary>
    private string Selected((int Row, int Column) from, (int Row, int Column) to)
    {
        ((int Row, int Column) first, (int Row, int Column) last) = Order(from, to);
        List<string> lines = [];

        for (int row = first.Row; row <= last.Row && row < plain.Length; row++)
        {
            string line = plain[row];
            int start = row == first.Row ? Math.Min(first.Column, line.Length) : 0;
            int end = row == last.Row ? Math.Min(last.Column + 1, line.Length) : line.Length;
            lines.Add(end > start ? line[start..end].TrimEnd() : string.Empty);
        }

        return string.Join(Environment.NewLine, lines).Trim('\r', '\n');
    }

    private static ((int Row, int Column), (int Row, int Column)) Order((int Row, int Column) a, (int Row, int Column) b) =>
        a.Row < b.Row || (a.Row == b.Row && a.Column <= b.Column) ? (a, b) : (b, a);

    /// <summary>Periodo del lampeggio del pallino «in corso».</summary>
    internal static readonly TimeSpan BlinkPeriod = TimeSpan.FromMilliseconds(500);

    private bool busy;
    private bool blinkOn = true;
    private Timer? blink;

    /// <summary>
    /// Vero mentre un turno gira: sotto l'ultima riga della conversazione, dove arrivera' la risposta, compare un
    /// pallino che lampeggia con «in corso» (S117). Prima lo dicevano le fasi, righe blu che si riscrivevano sopra
    /// il riquadro; ora il turno si racconta alla fine, e mentre si aspetta basta sapere che sta lavorando. La riga
    /// non entra nella conversazione: sparisce quando il turno finisce, e non resta nel testo ristampato uscendo.
    /// </summary>
    internal bool Busy
    {
        get => busy;
        set
        {
            if (!enabled) { return; }

            lock (gate)
            {
                if (busy == value) { return; }

                busy = value;
                blinkOn = true;
                blink?.Dispose();
                blink = value ? new Timer(_ => Blink(), null, BlinkPeriod, BlinkPeriod) : null;
                if (active && modal == 0) { Paint(); }
            }
        }
    }

    /// <summary>Un colpo del lampeggio: cambia il pallino e ridisegna, cioe' riscrive la sola riga di stato.</summary>
    internal void Blink()
    {
        lock (gate)
        {
            if (!busy) { return; }

            blinkOn = !blinkOn;
            if (active && modal == 0) { Paint(); }
        }
    }

    /// <summary>Una pagina: le righe della conversazione che si vedono, meno una per non perdere il filo.</summary>
    internal int Page => Math.Max(1, contentRows - 1);

    /// <summary>
    /// Righe da mostrare **sopra** il riquadro e dentro l'area fissa: il menu dei comandi, l'elenco dei
    /// file. Stanno qui e non nella conversazione perche' sono una scelta in corso, non qualcosa che e'
    /// successo.
    /// </summary>
    internal IReadOnlyList<OverlayLine> Overlay { get; set; } = [];

    /// <summary>Una riga dell'elenco sopra il riquadro.</summary>
    /// <param name="Text">Testo gia' impaginato.</param>
    /// <param name="Selected">Vero per la voce sotto il cursore.</param>
    /// <param name="Dim">Vero per cio' che accompagna e non si sceglie: intestazioni, conteggi, tasti.</param>
    internal readonly record struct OverlayLine(string Text, bool Selected = false, bool Dim = false);

    /// <summary>Disegna lo schermo con l'invito indicato e lascia il cursore dentro il riquadro.</summary>
    internal void Draw(string promptText, string typed, int position)
    {
        ArgumentNullException.ThrowIfNull(promptText);
        ArgumentNullException.ThrowIfNull(typed);

        if (!enabled) { return; }

        lock (gate)
        {
            // L'avviso dura fino al tasto successivo: se il testo cambia, l'operatore e' gia' passato oltre.
            if (typed != text) { notice = null; }
            prompt = promptText;
            text = typed;
            cursor = Math.Clamp(position, 0, typed.Length);
            Paint();
        }
    }

    /// <summary>Ridisegna con l'ultimo invito noto: dopo un cambio di stato, di livello o di dimensione.</summary>
    internal void Refresh()
    {
        if (!enabled) { return; }

        lock (gate) { Paint(); }
    }

    /// <summary>Dichiara perduto cio' che il terminale mostrava: il fotogramma successivo si scrive da capo.</summary>
    internal void Invalidate()
    {
        if (!enabled) { return; }

        lock (gate) { shown = null; }
    }

    /// <summary>
    /// Scorre la vista. Positivo va indietro nella conversazione, negativo verso il fondo; tornati in fondo
    /// si torna a seguire.
    /// </summary>
    internal void ScrollBy(int rowsBack)
    {
        if (!enabled) { return; }

        lock (gate)
        {
            int width = Math.Max(8, console!.Profile.Width);
            int limit = Math.Max(0, scrollback.TotalRows(width) - contentRows);
            offset = Math.Clamp(offset + rowsBack, 0, limit);
            if (offset == 0) { unseen = 0; }
            Paint();
        }
    }

    /// <summary>Torna in fondo e segue la conversazione.</summary>
    internal void ScrollToEnd()
    {
        if (!enabled) { return; }

        lock (gate)
        {
            if (offset == 0) { return; }

            offset = 0;
            unseen = 0;
            Paint();
        }
    }

    /// <summary>
    /// Pulisce lo schermo come farebbe un terminale: la conversazione sale fuori dalla vista e resta
    /// raggiungibile scorrendo, e nel terminale uscendo.
    /// </summary>
    internal void ClearScreen()
    {
        if (!enabled) { return; }

        lock (gate)
        {
            scrollback.Write(new string('\n', Math.Max(1, contentRows)));
            offset = 0;
            unseen = 0;
            shown = null;
            Paint();
        }
    }

    /// <summary>
    /// Cede lo schermo per la durata di un **momento modale** e lo riprende alla chiusura.
    ///
    /// Modale e' cio' che disegna da se' e scrive dentro i campi: il configuratore. Riceve lo schermo intero,
    /// senza mouse, e alla chiusura il fotogramma si ridisegna dal buffer — la conversazione non stava sullo
    /// schermo, quindi non c'e' niente da ricostruire. Durante il momento modale cio' che arriva su
    /// <see cref="Writer"/> va sullo schermo cosi' com'e': e' il configuratore che scrive, non la conversazione.
    ///
    /// I momenti modali si annidano: si contano, e lo schermo torna quando si chiude l'ultimo.
    /// </summary>
    /// <param name="fromTop">Tenuto per i chiamanti: il momento modale riceve comunque lo schermo intero.</param>
    internal IDisposable Modal(bool fromTop = false) => new ModalScope(this);

    private void Suspend()
    {
        if (!enabled) { return; }

        lock (gate)
        {
            if (modal++ > 0 || !active) { return; }

            Mouse(false);
            output.Write("\u001b[r\u001b[0m\u001b[2J\u001b[H\u001b[?25h");
            output.Flush();
            shown = null;
        }
    }

    private void Resume()
    {
        if (!enabled) { return; }

        lock (gate)
        {
            if (--modal > 0) { return; }

            modal = 0;
            if (!active) { return; }

            // Il configuratore puo' aver lasciato margini, stili o un cursore nascosto: si riparte puliti.
            output.Write("\u001b[r\u001b[0m\u001b[2J");
            Mouse(true);
            shown = null;
            Paint();
        }
    }

    private sealed class ModalScope : IDisposable
    {
        private readonly ReplBottomRegion region;
        private bool closed;

        internal ModalScope(ReplBottomRegion region)
        {
            this.region = region;
            region.Suspend();
        }

        public void Dispose()
        {
            if (closed) { return; }

            closed = true;
            region.Resume();
        }
    }

    /// <summary>Registra una scrittura della conversazione e, se non c'e' un momento modale, la mostra.</summary>
    private void Append(string? value)
    {
        if (string.IsNullOrEmpty(value)) { return; }

        lock (gate)
        {
            if (modal > 0)
            {
                output.Write(value);
                return;
            }

            if (offset > 0)
            {
                // Chi guarda indietro non viene riportato in fondo: la vista resta dov'e', e la barra conta
                // le righe arrivate sotto.
                int width = Math.Max(8, console!.Profile.Width);
                int before = scrollback.TotalRows(width);
                scrollback.Write(value);
                int added = Math.Max(0, scrollback.TotalRows(width) - before);
                offset += added;
                unseen += added;
            }
            else
            {
                scrollback.Write(value);
            }

            Paint();
        }
    }

    /// <summary>Compone il fotogramma e scrive le righe che sono cambiate. Si chiama sotto il lucchetto.</summary>
    private void Paint()
    {
        if (modal > 0) { return; }

        Enter();

        int width = Math.Max(8, console!.Profile.Width);
        int height = Math.Max(Fixed + MinimumScrolling + 1, console.Profile.Height);

        if (width != columns || height != rows)
        {
            // Una finestra nuova e' uno schermo nuovo: si ridisegna tutto, e la conversazione si ri-avvolge.
            columns = width;
            rows = height;
            shown = null;
        }

        List<string> area = FixedArea(width, out int boxRow, out int boxColumn);

        contentRows = Math.Max(1, rows - area.Count);
        int limit = Math.Max(0, scrollback.TotalRows(width) - contentRows);
        if (offset > limit) { offset = limit; }

        // Mentre il turno gira e si guarda in fondo, l'ultima riga utile e' dell'attesa: la conversazione ne cede
        // una e la riga «in corso» sta subito sotto il suo ultimo testo. Chi guarda indietro non la vede: sta
        // leggendo altro, e la barra gli dice gia' che sotto c'e' dell'altro.
        bool waiting = busy && offset == 0 && contentRows > 1;
        IReadOnlyList<string> conversation = scrollback.Rows(width, waiting ? contentRows - 1 : contentRows, offset);

        string[] frame = new string[rows];
        for (int index = 0; index < contentRows; index++)
        {
            frame[index] = index < conversation.Count ? conversation[index] : string.Empty;
        }

        if (waiting)
        {
            // Il pallino arancione si spegne e si riaccende, la scritta resta ferma e tenue.
            frame[conversation.Count] = (blinkOn ? Orange + "●" + StyleOff : " ") + " " + DimOn + Ui.Text("status.busy") + StyleOff;
        }
        for (int index = 0; index < area.Count && contentRows + index < rows; index++)
        {
            frame[contentRows + index] = area[index];
        }

        // Il testo che si vede, senza colori: da qui si copia. Poi la selezione in corso si disegna in negativo
        // sopra quel testo, dove l'operatore la sta trascinando.
        plain = [.. frame.Select(line => Sequences.Replace(line ?? string.Empty, string.Empty))];

        if (anchor is { } selecting)
        {
            ((int Row, int Column) first, (int Row, int Column) last) = Order(selecting, reach);

            for (int row = Math.Max(0, first.Row); row <= last.Row && row < rows; row++)
            {
                string line = plain[row];
                int start = row == first.Row ? Math.Min(first.Column, line.Length) : 0;
                int end = row == last.Row ? Math.Min(last.Column + 1, line.Length) : line.Length;
                if (end <= start) { frame[row] = line; continue; }
                frame[row] = line[..start] + "\u001b[7m" + line[start..end] + "\u001b[27m" + line[end..];
            }
        }

        StringBuilder paint = new();
        paint.Append("\u001b[?25l");

        for (int index = 0; index < rows; index++)
        {
            if (shown is not null && index < shown.Length && shown[index] == frame[index]) { continue; }
            paint.Append(Move(index + 1, 1)).Append("\u001b[2K").Append(frame[index]);
        }

        paint.Append(Move(contentRows + boxRow + 1, boxColumn)).Append("\u001b[?25h");

        shown = frame;
        output.Write(paint.ToString());
        output.Flush();
    }

    /// <summary>
    /// L'area fissa, riga per riga: elenco del menu, bordo, invito, bordo, stato. Restituisce anche dove va il
    /// cursore, contato dall'inizio dell'area.
    /// </summary>
    private List<string> FixedArea(int width, out int boxRow, out int boxColumn)
    {
        int inner = Math.Max(1, width - 4);
        string[] typed = Wrap(prompt + text, inner);

        // L'area fissa non si mangia la conversazione: oltre questo limite l'invito mostra le **ultime**
        // righe — dov'e' il cursore — e l'elenco del menu si accorcia.
        int room = Math.Max(Fixed, rows - MinimumScrolling);
        int allowed = Math.Max(1, room - 3);
        int skipped = Math.Max(0, typed.Length - allowed);
        typed = [.. typed.Skip(skipped)];

        List<string> area = [];

        foreach (OverlayLine line in Overlay.Take(Math.Max(0, room - 3 - typed.Length)))
        {
            // Dentro l'area fissa i colori si scrivono a mano: qui ANSI c'e' per definizione.
            string style = line.Selected ? BoldOn : line.Dim ? DimOn : string.Empty;
            area.Add(style + Output.Layout.Truncate(line.Text, width) + (style.Length > 0 ? StyleOff : string.Empty));
        }

        area.Add(Border('╭', '╮', width));
        int first = area.Count;
        foreach (string line in typed) { area.Add("│ " + line.PadRight(inner) + " │"); }
        area.Add(Border('╰', '╯', width));

        // L'ultima colonna dell'ultima riga non si scrive: in alcuni terminali farebbe scorrere lo schermo.
        area.Add(StatusLine(width - 1));

        int offsetInBox = prompt.Length + cursor;
        boxRow = first + Math.Clamp(offsetInBox / inner - skipped, 0, typed.Length - 1);
        boxColumn = 3 + (offsetInBox % inner);

        return area;
    }

    /// <summary>
    /// Stato e livello di dettaglio; mentre si guarda indietro, prima di tutto dice che la vista non e' in
    /// fondo e come tornarci. Il livello **si riserva il suo spazio**: e' corto ed e' l'unica cosa che dice
    /// come sara' la prossima risposta.
    /// </summary>
    private string StatusLine(int width)
    {
        string level = Verbose ? "◉ verbose on" : "○ verbose off";
        string scrolled = offset == 0 ? string.Empty
            : (unseen == 0 ? Ui.Text("scroll.back") : unseen == 1 ? Ui.Text("scroll.backOne") : Ui.Format("scroll.backMany {0}", unseen)) + "  ";

        string lead = (notice is null ? string.Empty : notice + "  ") + scrolled;

        if (Status is not StatusView status) { return Output.Layout.Truncate(lead + level, width); }

        string fields = Output.Layout.Truncate(ReplStatusBar.Plain(status), Math.Max(0, width - level.Length - lead.Length - 2));

        return Output.Layout.Truncate(lead + fields + "  " + level, width);
    }

    /// <summary>Entra nello schermo alternativo, se non lo si e' ancora fatto.</summary>
    private void Enter()
    {
        if (active) { return; }

        active = true;
        shown = null;
        output.Write("\u001b[?1049h\u001b[r\u001b[2J");
        Mouse(true);
    }

    /// <summary>La rotella la ricevono il terminale, con le sequenze SGR, e la sorgente dei tasti.</summary>
    private void Mouse(bool on)
    {
        // 1002 aggiunge a 1000 il movimento con un tasto premuto: senza, una selezione non si potrebbe trascinare.
        output.Write(on ? "\u001b[?1000h\u001b[?1002h\u001b[?1006h" : "\u001b[?1006l\u001b[?1002l\u001b[?1000l");
        if (keys is not null) { keys.Mouse = on; }
    }

    /// <summary>
    /// Lascia lo schermo alternativo e ristampa la conversazione nel terminale, dove resta nello scrollback.
    /// Va fatto anche quando il REPL finisce male: un terminale lasciato nello schermo alternativo, con il
    /// mouse catturato, resta rotto per chi lo usa dopo.
    /// </summary>
    public void Dispose()
    {
        if (!enabled) { return; }

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

        lock (gate)
        {
            busy = false;
            blink?.Dispose();
            blink = null;

            if (!active) { return; }

            Leave();

            StringBuilder transcript = new();
            foreach (string line in scrollback.Transcript()) { transcript.Append(line).Append("\r\n"); }
            transcript.Append("\u001b[0m");
            output.Write(transcript.ToString());
            output.Flush();
        }
    }

    private void Leave()
    {
        active = false;
        Mouse(false);
        output.Write("\u001b[0m\u001b[?25h\u001b[?1049l");
        output.Flush();
    }

    private void OnProcessExit(object? sender, EventArgs arguments)
    {
        lock (gate)
        {
            if (active) { Leave(); }
        }
    }

    /// <summary>Grassetto per la voce scelta, tenue per cio' che accompagna.</summary>
    private const string BoldOn = "\u001b[1m";
    private const string DimOn = "\u001b[2m";
    private const string Orange = "\u001b[38;5;208m";
    private const string StyleOff = "\u001b[0m";

    private static string Border(char left, char right, int width) =>
        left + new string('─', Math.Max(0, width - 2)) + right;

    private static string Move(int row, int column) =>
        "\u001b[" + row.ToString(Formats.Invariant) + ";" + column.ToString(Formats.Invariant) + "H";

    /// <summary>Spezza il testo alla larghezza interna del riquadro, senza perdere niente.</summary>
    private static string[] Wrap(string value, int width)
    {
        if (value.Length <= width) { return [value]; }

        List<string> lines = [];

        for (int index = 0; index < value.Length; index += width)
        {
            lines.Add(value.Substring(index, Math.Min(width, value.Length - index)));
        }

        return [.. lines];
    }

    /// <summary>
    /// Il flusso della conversazione: registra nel buffer e ridisegna. Chi scrive contenuto non sa che lo
    /// schermo esista.
    /// </summary>
    private sealed class ConversationWriter(ReplBottomRegion region, TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value) => region.Append(value.ToString());

        public override void Write(string? value) => region.Append(value);

        public override void Write(char[] buffer, int index, int count) => region.Append(new string(buffer, index, count));

        public override void WriteLine(string? value) => region.Append(value + new string(CoreNewLine));

        public override void WriteLine() => region.Append(new string(CoreNewLine));

        public override void Flush() { }
    }
}
