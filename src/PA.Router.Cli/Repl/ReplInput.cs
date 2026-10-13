using System.Text;
using PA.Router.Cli.Output;
using PA.Router.Hosting.Runtime;
using Spectre.Console;

namespace PA.Router.Cli.Repl;

/// <summary>
/// Da dove arriva una richiesta del REPL.
///
/// Esiste perche' il ciclo del REPL non deve sapere se dall'altra parte c'e' un terminale o una
/// pipe. Con un terminale la riga si compone a tasti — cronologia, completamento, multilinea — e
/// l'invito lo ridisegna chi la compone; senza terminale resta la lettura a righe di sempre, che e'
/// quella che usano le prove e l'automazione (03-specs/CLI-TUI.md).
/// </summary>
internal interface IReplInput
{
    /// <summary>
    /// Vero se e' questo strato a disegnare l'invito. Chi compone la riga lo ridisegna a ogni
    /// tasto, quindi il ciclo non deve stamparlo una seconda volta.
    /// </summary>
    bool OwnsPrompt { get; }
    ReplInterrupt? Interruptions => null;

    /// <summary>Nome del profilo della sessione, gia' risolto dalla configurazione; null: non mostrarlo.</summary>
    string? ProfileName { get => null; set { } }

    /// <summary>
    /// Stato mostrato sopra l'invito. Lo calcola chi ha la configurazione (INV-22); qui si disegna
    /// soltanto. Null significa «non disegnarlo», che e' il caso di un ingresso non interattivo.
    /// </summary>
    StatusView? Status { get => null; set { } }

    /// <summary>
    /// Che cosa fare quando l'operatore chiede piu' o meno dettaglio (Ctrl+O). Lo decide la
    /// sessione, che sa che cosa c'e' da rispiegare; il compositore di riga sa solo che e' stato
    /// premuto. Non e' Ctrl+V: su Windows e' «incolla», e catturarlo romperebbe l'incolla.
    /// </summary>
    Action? ToggleVerbose { get => null; set { } }

    /// <summary>
    /// Richiesta successiva, oppure null quando l'ingresso e' finito. Puo' contenere piu' righe:
    /// un blocco incollato e' **una** richiesta, non una per riga.
    /// </summary>
    /// <param name="remember">
    /// Falso per cio' che non e' una richiesta. Una risposta di conferma — «si» — non e' una
    /// richiesta, e ritrovarsela premendo la freccia su sarebbe una cronologia piena di monosillabi.
    /// </param>
    string? Read(bool remember = true);

    /// <summary>
    /// Vero se qualcuno ha gia' premuto un tasto e la lettura non attenderebbe (S65).
    ///
    /// Serve a scrivere **mentre il turno gira**: il ciclo sorveglia questa proprieta' e legge la
    /// riga solo quando l'operatore ha davvero cominciato, invece di bloccarsi in attesa e togliere
    /// il controllo al turno in corso.
    ///
    /// Senza un terminale e' sempre falsa, e la coda non esiste: in una pipe non c'e' nessuno che
    /// stia digitando mentre si aspetta.
    /// </summary>
    bool Typing => false;

    /// <summary>
    /// Modalita' vim del compositore di riga (S67). Nasce **spenta**.
    ///
    /// Senza un terminale non c'e' una riga da comporre: qui resta falsa e accenderla non fa nulla,
    /// cosi' il REPL puo' offrire il comando senza sapere quale ingresso ha davanti.
    /// </summary>
    bool VimEnabled
    {
        get => false;
        set { }
    }

    /// <summary>Modo corrente, per l'invito: vuoto quando la modalita' e' spenta.</summary>
    string VimIndicator => string.Empty;
}

/// <summary>Lettura a righe: il comportamento di sempre, per pipe, file e prove.</summary>
internal sealed class TextReaderReplInput(TextReader reader) : IReplInput
{
    /// <inheritdoc />
    public bool OwnsPrompt => false;

    /// <inheritdoc />
    public string? Read(bool remember = true) => reader.ReadLine();
}

/// <summary>
/// Tasti che arrivano al compositore di riga.
///
/// E' un'astrazione sottile sopra la console perche' una prova non puo' avere un terminale vero:
/// senza di essa l'unico modo di verificare la cronologia o il completamento sarebbe guardare uno
/// schermo, cioe' non verificarli.
/// </summary>
internal interface IKeySource
{
    /// <summary>Vero se un altro tasto e' gia' disponibile senza attendere.</summary>
    bool KeyAvailable { get; }
    bool Pasting => false;
    bool WaitedForKey => false;

    /// <summary>
    /// Vero mentre la rotella deve arrivare come tasto (ADR-024). Lo accende lo schermo alternativo; una
    /// sorgente che non sa leggere il mouse lo ignora.
    /// </summary>
    bool Mouse { get => false; set { } }

    /// <summary>
    /// Riga e colonna, contate da zero, dell'ultimo evento del mouse letto. Un tasto non ha posto per due
    /// coordinate: chi riceve una selezione o un clic le chiede qui.
    /// </summary>
    (int Row, int Column) Pointer => (0, 0);

    /// <summary>Tasto successivo. Non viene mai mostrato dalla console: lo disegna il compositore.</summary>
    ConsoleKeyInfo Read();
}

/// <summary>Tasti della console reale.</summary>
internal sealed class ConsoleKeySource : IKeySource
{
    /// <inheritdoc />
    public bool KeyAvailable => Console.KeyAvailable;

    /// <inheritdoc />
    public ConsoleKeyInfo Read() => Console.ReadKey(intercept: true);
}

/// <summary>
/// Cosa si puo' completare con Tab.
///
/// Il completamento vede **solo** nomi di comandi del REPL e percorsi del disco. Non vede la
/// cronologia dei contenuti, i ricordi o i nomi dei servizi: un elenco che si apre da solo e'
/// l'ultimo posto dove far comparire un dato che l'operatore non ha chiesto di mostrare.
/// </summary>
internal sealed class ReplCompletion(IReadOnlyList<string> commands)
{
    /// <summary>Completamenti possibili per il testo gia' scritto, in ordine stabile.</summary>
    internal IReadOnlyList<string> For(string buffer, int cursor)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        string head = buffer[..Math.Clamp(cursor, 0, buffer.Length)];

        // Un comando si completa solo se e' la prima parola della riga: `/exit` e' un comando,
        // «dimmi come si usa /exit» e' una richiesta.
        if (head.StartsWith('/') && !head.Contains(' ', StringComparison.Ordinal))
        {
            return [.. commands
                .Where(command => command.StartsWith(head, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)];
        }

        return Paths(Token(head));
    }

    /// <summary>Ultima parola della riga: e' quella che Tab sostituisce.</summary>
    internal static string Token(string head)
    {
        ArgumentNullException.ThrowIfNull(head);

        int space = head.LastIndexOf(' ');

        return space < 0 ? head : head[(space + 1)..];
    }

    /// <summary>
    /// Percorsi che cominciano per il testo scritto. Un errore del filesystem non e' un errore del
    /// REPL: chi preme Tab su una directory che non esiste non vede completamenti, non un guasto.
    /// </summary>
    private static IReadOnlyList<string> Paths(string token)
    {
        if (token.Length == 0)
        {
            return [];
        }

        try
        {
            string directory = Path.GetDirectoryName(token) is { Length: > 0 } parent ? parent : ".";
            string prefix = Path.GetFileName(token);

            if (!Directory.Exists(directory))
            {
                return [];
            }

            return
            [
                .. Directory
                    .EnumerateFileSystemEntries(directory)
                    .Select(entry => Path.GetFileName(entry) ?? string.Empty)
                    .Where(name => name.Length > 0 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)
                    .Select(name => Path.GetDirectoryName(token) is { Length: > 0 } parent
                        ? Path.Combine(parent, name)
                        : name),
            ];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>
/// Cronologia delle richieste della sessione.
///
/// Vive **in memoria** e muore con il processo. Contiene le richieste dell'operatore, cioe'
/// contenuti, e i contenuti seguono <c>contentMode</c>; inoltre, con un worker vivo, questo
/// processo non possiede la chiave dati, quindi una cronologia su disco esisterebbe solo quando
/// nessun worker e' in ascolto. Una funzionalita' che cambia con la presenza di un altro processo
/// e' peggio di una dichiaratamente assente (ADR-021).
/// </summary>
internal sealed class ReplHistory
{
    private readonly List<string> entries = [];
    private int cursor;

    /// <summary>Richieste conservate, dalla piu' vecchia.</summary>
    internal IReadOnlyList<string> Entries => entries;

    /// <summary>Aggiunge una richiesta. Una ripetizione consecutiva non si duplica.</summary>
    internal void Add(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Length > 0 && (entries.Count == 0 || !string.Equals(entries[^1], entry, StringComparison.Ordinal)))
        {
            entries.Add(entry);
        }

        cursor = entries.Count;
    }

    /// <summary>Voce precedente, oppure null se non ce n'e' piu'.</summary>
    internal string? Previous()
    {
        if (cursor == 0)
        {
            return null;
        }

        cursor--;
        return entries[cursor];
    }

    /// <summary>Voce successiva; oltre l'ultima torna la riga vuota.</summary>
    internal string? Next()
    {
        if (cursor >= entries.Count)
        {
            return null;
        }

        cursor++;
        return cursor == entries.Count ? string.Empty : entries[cursor];
    }

    /// <summary>Voce piu' recente che contiene il testo cercato.</summary>
    internal string? Search(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        for (int index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index].Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return entries[index];
            }
        }

        return null;
    }
}

/// <summary>
/// Compositore di riga a tasti: cronologia, completamento, multilinea e incolla.
///
/// Ridisegna la riga corrente; i suggerimenti slash usano una regione Spectre temporanea.
/// Nessuno schermo alternativo: il menu viene cancellato alla chiusura e lo scrollback
/// conserva richieste ed eventi, non ogni passaggio del completamento (ADR-007/021).
///
/// Il testo composto non viene mai interpretato: e' la richiesta dell'operatore e attraversa la
/// stessa catena di <c>pa run</c>.
/// </summary>
internal sealed class ConsoleReplInput(
    IKeySource keys,
    TextWriter output,
    ReplCompletion completion,
    ReplHistory history,
    Action? clearScreen = null,
    ReplCommandMenu? commandMenu = null,
    IAnsiConsole? inputConsole = null,
    ReplFileMenu? fileMenu = null,
    ReplInterrupt? interruptions = null) : IReplInput
{
    /// <summary>Invito disegnato da questo strato. La larghezza serve a rimettere il cursore.</summary>
    public string? ProfileName { get; set; }

    /// <inheritdoc />
    public StatusView? Status { get; set; }

    /// <inheritdoc />
    public Action? ToggleVerbose { get; set; }

    /// <summary>
    /// L'area fissa in fondo, quando c'e'. Con questa attiva l'invito lo disegna lei (ADR-023);
    /// senza, resta la resa a righe di sempre.
    /// </summary>
    internal ReplBottomRegion? Region { get; set; }

    /// <summary>Da dove si incolla con il tasto destro: gli appunti del sistema, o quelli finti di una prova.</summary>
    internal Func<string?> Clipboard { get; init; } = ReplClipboard.Read;
    public ReplInterrupt Interruptions { get; } = interruptions ?? ReplTerminalScope.Current?.Interruptions ?? new();

    /// <summary>Invito delle righe successive di una richiesta su piu' righe.</summary>
    private const string ContinuationText = ". ";

    /// <inheritdoc />
    public bool OwnsPrompt => true;

    /// <summary>
    /// Un tasto gia' letto per sapere se qualcuno sta scrivendo, e non ancora consegnato: la lettura della
    /// riga comincia da lui.
    /// </summary>
    private ConsoleKeyInfo? held;

    /// <inheritdoc />
    /// <remarks>
    /// Rotella, ridimensionamento e scorrimento non sono scrittura (ADR-024): si eseguono qui e non fanno
    /// risultare nessuno al lavoro. Contarli come tasti apriva la lettura della riga a meta' turno, e il REPL
    /// restava li' ad aspettare un Invio anche a turno finito.
    /// </remarks>
    public bool Typing
    {
        get
        {
            while (held is null && keys.KeyAvailable)
            {
                ConsoleKeyInfo key = keys.Read();
                if (!Navigate(key)) { held = key; }
            }

            return held is not null;
        }
    }

    private ConsoleKeyInfo NextKey()
    {
        if (held is { } key) { held = null; return key; }
        return keys.Read();
    }

    /// <summary>
    /// Esegue un tasto che muove la vista invece di scrivere: rotella, PgSu e PgGiu', Ctrl+Fine, e il
    /// ridimensionamento. Vero se il tasto era uno di questi.
    /// </summary>
    private bool Navigate(ConsoleKeyInfo key)
    {
        if (keys.Pasting) { return false; }

        if (Region is { Enabled: true } screen)
        {
            if (ReplKeys.IsResize(key)) { screen.Refresh(); return true; }
            if (ReplKeys.IsSelection(key)) { screen.Select(key, keys.Pointer); return true; }
            if (ReplKeys.IsWheel(key, out bool back)) { screen.ScrollBy(back ? ReplKeys.WheelStep : -ReplKeys.WheelStep); return true; }
            if (key.Key == ConsoleKey.PageUp && key.Modifiers == 0) { screen.ScrollBy(screen.Page); return true; }
            if (key.Key == ConsoleKey.PageDown && key.Modifiers == 0) { screen.ScrollBy(-screen.Page); return true; }
            if (key.Key == ConsoleKey.End && key.Modifiers.HasFlag(ConsoleModifiers.Control)) { screen.ScrollToEnd(); return true; }
            return false;
        }

        return ReplKeys.IsResize(key) || ReplKeys.IsWheel(key, out _);
    }

    /// <summary>
    /// Motore dei modi, costruito solo quando la modalita' viene accesa (S67).
    ///
    /// Finche' resta null il percorso dei tasti e' **esattamente** quello di S56: non c'e' un ramo
    /// in piu' da attraversare, e il gate di questo step — «con la modalita' spenta nessun tasto
    /// cambia comportamento» — e' una proprieta' del codice, non una verifica a campione.
    /// </summary>
    private VimMode? vim;

    /// <inheritdoc />
    public bool VimEnabled
    {
        get => vim is not null;
        set => vim = value ? vim ?? new VimMode() : null;
    }

    /// <inheritdoc />
    public string VimIndicator => vim?.Indicator ?? string.Empty;

    /// <inheritdoc />
    public string? Read(bool remember = true)
    {
        StringBuilder buffer = new();
        List<string> lines = [];
        int cursor = 0;
        // Con l'area fissa lo stato sta in fondo ed e' sempre visibile: ristamparlo qui lo
        // ripeterebbe a ogni richiesta (ADR-023). Senza area fissa resta la riga di prima.
        if (remember && ProfileName is { } name && Region is not { Enabled: true })
        {
            output.WriteLine();

            if (Status is StatusView status && inputConsole is not null) { ReplStatusBar.Write(inputConsole, status); }
            else
            {
                string profile = Ui.Text("profile.session") + ": " + name;
                if (inputConsole is null) { output.WriteLine(profile); }
                else { inputConsole.Write(new Text(SafeText.Inline(profile) + Environment.NewLine, new Style(ReplChatStyle.Secondary))); }
            }
        }

        // Dentro il riquadro l'invito e' il solo «>»: il bordo dice gia' che quella riga e' la tua.
        string prompt = Region is { Enabled: true } || !remember ? "> " : Ui.Text("chat.you") + " > ";
        string? searching = null;
        string? dismissedCompletion = null;
        string? dismissedFileCompletion = null;
        cursorRow = 0;
        endRow = 0;

        Redraw(prompt, buffer, cursor);

        while (true)
        {
            bool queuedKey = held is not null || keys.KeyAvailable;
            ConsoleKeyInfo key;
            string current = buffer.ToString();
            if (commandMenu is not null && remember && lines.Count == 0 && searching is null
                && vim?.State != VimState.Normal && !keys.Pasting && held is null && !keys.KeyAvailable && cursor == buffer.Length
                && current.StartsWith('/') && !current.Any(char.IsWhiteSpace) && current != dismissedCompletion)
            {
                // Senza area fissa il menu prende la riga dell'invito e va cancellata prima; con
                // l'area fissa l'invito e' dentro il riquadro, e cancellare «la riga» significherebbe
                // scrivere spazi in mezzo alla conversazione.
                ClearLine(prompt.Length + current.Length + 8);
                ReplCommandMenu.Result choice = commandMenu.Read(prompt, current, completion, keys, Region);
                Replace(buffer, choice.Text, ref cursor);
                dismissedCompletion = choice.Text;
                Redraw(prompt, buffer, cursor);
                if (choice.PendingKey is not { } pending) { continue; }
                key = pending;
            }
            else if (fileMenu is not null && remember && searching is null && vim?.State != VimState.Normal
                && !keys.Pasting && held is null && !keys.KeyAvailable && (cursor == buffer.Length || char.IsWhiteSpace(buffer[cursor]))
                && current[..cursor] != dismissedFileCompletion
                && TurnAttachments.TryGetEditingMention(current[..cursor], out _, out _))
            {
                ClearLine(prompt.Length + current.Length + 8);
                ReplFileMenu.Result choice = fileMenu.Read(prompt, current[..cursor], keys, Region);
                string suffix = current[cursor..];
                buffer.Clear();
                buffer.Append(choice.Text).Append(suffix);
                cursor = choice.Text.Length;
                dismissedFileCompletion = choice.Text;
                Redraw(prompt, buffer, cursor);
                if (choice.PendingKey is not { } pending) { continue; }
                key = pending;
            }
            else { key = NextKey(); }

            // Scorrere, ridimensionare e selezionare non toccano la richiesta (ADR-024): si fanno e si torna ad
            // aspettare.
            if (Navigate(key)) { continue; }

            // Il tasto destro incolla dagli appunti, come nei terminali: il terminale non lo fa piu' da solo, perche'
            // i clic li riceve il router.
            if (ReplKeys.IsSecondaryClick(key))
            {
                if (Clipboard() is { Length: > 0 } clipboard) { Paste(clipboard, buffer, lines, ref cursor, ref prompt); }
                else { Region?.Notice(Ui.Text("clip.nothing")); }
                Redraw(prompt, buffer, cursor);
                continue;
            }

            // Un evento del terminale che nessuno ha preso non e' testo.
            if (ReplKeys.IsTerminalEvent(key)) { continue; }

            if (!keys.Pasting && ReplInterrupt.IsControlC(key))
            {
                bool exit = Interruptions.Press();
                ClearLine(prompt.Length + buffer.Length + 8);
                output.WriteLine(Ui.Text(exit ? "input.exiting" : "input.interrupt"));
                if (Region is { Enabled: true }) { Redraw(prompt, new StringBuilder(), 0); }
                vim?.Reset();
                return exit ? null : string.Empty;
            }
            if (!keys.Pasting) { Interruptions.Activity(); }
            if (keys.Pasting && key.KeyChar is not '\r' and not '\n')
            {
                if (key.KeyChar == '\t' || !char.IsControl(key.KeyChar)) { buffer.Insert(cursor++, key.KeyChar); }
                Redraw(prompt, buffer, cursor);
                continue;
            }

            if (keys.Pasting && key.KeyChar is '\r' or '\n') { key = new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false); }

            // Modalita' vim (S67): il motore si consulta **prima** dello smistamento, e solo se e'
            // accesa. Spenta, `vim` e' null e questo blocco non esiste sul percorso del tasto.
            if (!keys.Pasting && vim is not null && vim.Handle(key, buffer, ref cursor, history) is VimOutcome.Handled)
            {
                Redraw(prompt, buffer, cursor);
                continue;
            }

            // Un blocco incollato arriva tutto insieme: i tasti sono gia' disponibili senza
            // attendere. In quel caso un a capo **non** invia la richiesta, la continua — altrimenti
            // incollare dieci righe significherebbe dieci turni, che e' cio' che faceva prima.
            bool pasting = keys.Pasting || (queuedKey && !keys.WaitedForKey) || keys.KeyAvailable;

            switch (key.Key)
            {
                case ConsoleKey.Enter when pasting:
                    lines.Add(buffer.ToString());
                    buffer.Clear();
                    cursor = 0;
                    prompt = ContinuationText;
                    LeaveLine();
                    Redraw(prompt, buffer, cursor);
                    continue;

                case ConsoleKey.Enter when buffer.Length > 0 && buffer[^1] == '\\':
                    // Continuazione esplicita: la barra finale non fa parte della richiesta.
                    buffer.Length--;
                    lines.Add(buffer.ToString());
                    buffer.Clear();
                    cursor = 0;
                    prompt = ContinuationText;
                    LeaveLine();
                    Redraw(prompt, buffer, cursor);
                    continue;

                case ConsoleKey.Enter:
                    LeaveLine();
                    lines.Add(buffer.ToString());

                    string request = string.Join('\n', lines).Trim();

                    // Con l'area fissa la richiesta vive dentro il riquadro, che al tasto successivo si
                    // ridisegna vuoto: senza rimandarla sopra, di cio' che si e' chiesto non resta niente
                    // sullo schermo e la conversazione diventa un seguito di risposte senza domande.
                    if (Region is { Enabled: true } && request.Length > 0)
                    {
                        // Chi invia torna in fondo: la risposta arriva li'.
                        Region.ScrollToEnd();
                        Echo(request);
                    }

                    if (remember)
                    {
                        history.Add(request);
                    }

                    // Consegnata la riga si torna a scrivere: cominciare la successiva in modo
                    // normale farebbe sparire i primi caratteri di chi non se lo aspetta.
                    vim?.Reset();

                    return request;

                case ConsoleKey.J when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    // Ctrl+J: a capo dentro la richiesta, per chi scrive piu' righe a mano.
                    lines.Add(buffer.ToString());
                    buffer.Clear();
                    cursor = 0;
                    prompt = ContinuationText;
                    LeaveLine();
                    Redraw(prompt, buffer, cursor);
                    continue;

                case ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    if (buffer.Length == 0 && lines.Count == 0)
                    {
                        // Riga vuota: fine dell'ingresso, come farebbe /exit.
                        output.WriteLine();
                        return null;
                    }

                    continue;

                case ConsoleKey.L when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    // Pulire lo schermo cancella anche l'area fissa, e alcuni terminali ne rimettono i
                    // margini: la regione va rinegoziata, non ridisegnata dov'era.
                    if (Region is { Enabled: true }) { Region.ClearScreen(); }
                    else { clearScreen?.Invoke(); }
                    cursorRow = 0;
                    endRow = 0;
                    Redraw(prompt, buffer, cursor);
                    continue;

                case ConsoleKey.O when key.Modifiers.HasFlag(ConsoleModifiers.Control) && ToggleVerbose is not null:
                    // Cio' che l'operatore ha gia' scritto non si perde: si lascia la riga, si
                    // stampa la spiegazione, e l'invito si ridisegna sotto con il testo intatto.
                    LeaveLine();
                    ToggleVerbose();
                    cursorRow = 0;
                    endRow = 0;
                    Redraw(prompt, buffer, cursor);
                    continue;

                case ConsoleKey.R when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    searching = string.Empty;
                    if (Region is { Enabled: true }) { Region.Draw(Ui.Text("(cerca) "), string.Empty, 0); }
                    else { output.Write(Ui.Text("\r(cerca) ")); }
                    continue;

                case ConsoleKey.Tab:
                    Complete(buffer, ref cursor);
                    Redraw(prompt, buffer, cursor);
                    continue;

                case ConsoleKey.UpArrow when history.Previous() is string previous:
                    Replace(buffer, previous, ref cursor);
                    Redraw(prompt, buffer, cursor);
                    continue;

                case ConsoleKey.DownArrow when history.Next() is string next:
                    Replace(buffer, next, ref cursor);
                    Redraw(prompt, buffer, cursor);
                    continue;

                case ConsoleKey.LeftArrow when cursor > 0:
                    cursor--;
                    Redraw(prompt, buffer, cursor);
                    continue;

                case ConsoleKey.RightArrow when cursor < buffer.Length:
                    cursor++;
                    Redraw(prompt, buffer, cursor);
                    continue;

                case ConsoleKey.Home:
                    cursor = 0;
                    Redraw(prompt, buffer, cursor);
                    continue;

                case ConsoleKey.End:
                    cursor = buffer.Length;
                    Redraw(prompt, buffer, cursor);
                    continue;

                case ConsoleKey.Backspace when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    DeleteWord(buffer, ref cursor);
                    Redraw(prompt, buffer, cursor);
                    continue;

                case ConsoleKey.Backspace when cursor > 0:
                    buffer.Remove(cursor - 1, 1);
                    cursor--;
                    Redraw(prompt, buffer, cursor);
                    continue;

                case ConsoleKey.Delete when cursor < buffer.Length:
                    buffer.Remove(cursor, 1);
                    Redraw(prompt, buffer, cursor);
                    continue;

                default:
                    break;
            }

            if (key.KeyChar == '\0' || char.IsControl(key.KeyChar))
            {
                continue;
            }

            buffer.Insert(cursor, key.KeyChar);
            cursor++;

            if (searching is not null)
            {
                searching += key.KeyChar;

                if (history.Search(searching) is string found)
                {
                    Replace(buffer, found, ref cursor);
                    searching = null;
                }
            }

            Redraw(prompt, buffer, cursor);
        }
    }

    /// <summary>Sostituisce la riga con un'altra, portando il cursore in fondo.</summary>
    private static void Replace(StringBuilder buffer, string text, ref int cursor)
    {
        buffer.Clear();
        buffer.Append(text);
        cursor = buffer.Length;
    }

    /// <summary>Toglie la parola prima del cursore, spazi compresi.</summary>
    private static void DeleteWord(StringBuilder buffer, ref int cursor)
    {
        int end = cursor;

        while (cursor > 0 && buffer[cursor - 1] == ' ')
        {
            cursor--;
        }

        while (cursor > 0 && buffer[cursor - 1] != ' ')
        {
            cursor--;
        }

        buffer.Remove(cursor, end - cursor);
    }

    /// <summary>
    /// Completa la parola sotto il cursore. Con piu' candidati si scrive la parte comune: scegliere
    /// per l'operatore sarebbe piu' rapido e a volte sbagliato.
    /// </summary>
    private void Complete(StringBuilder buffer, ref int cursor)
    {
        string head = buffer.ToString(0, cursor);
        IReadOnlyList<string> candidates = completion.For(buffer.ToString(), cursor);

        if (candidates.Count == 0)
        {
            return;
        }

        string token = head.StartsWith('/') && !head.Contains(' ', StringComparison.Ordinal)
            ? head
            : ReplCompletion.Token(head);

        string chosen = candidates.Count == 1 ? candidates[0] : Common(candidates);

        if (chosen.Length <= token.Length)
        {
            return;
        }

        buffer.Insert(cursor, chosen[token.Length..]);
        cursor += chosen.Length - token.Length;
    }

    /// <summary>Prefisso comune a tutti i candidati.</summary>
    private static string Common(IReadOnlyList<string> candidates)
    {
        string prefix = candidates[0];

        foreach (string candidate in candidates)
        {
            int length = 0;

            while (length < prefix.Length && length < candidate.Length && prefix[length] == candidate[length])
            {
                length++;
            }

            prefix = prefix[..length];
        }

        return prefix;
    }

    /// <summary>Righe del terminale fra l'inizio della riga disegnata e quella dove sta il cursore.</summary>
    private int cursorRow;

    /// <summary>Righe del terminale fra l'inizio della riga disegnata e la sua fine.</summary>
    private int endRow;

    /// <summary>
    /// Larghezza utile per sapere dove il terminale manda a capo. Zero quando non c'e' un terminale che
    /// capisca le sequenze: li' resta il ridisegno semplice, che e' corretto per una riga sola.
    /// </summary>
    private int WrapWidth => inputConsole is { Profile.Capabilities.Ansi: true } console ? Math.Max(0, console.Profile.Width) : 0;

    /// <summary>
    /// Ridisegna la riga corrente.
    ///
    /// Un ritorno a inizio riga basta finche' il testo sta in una riga del terminale. Oltre, il ritorno
    /// porta all'inizio dell'**ultima** riga andata a capo, non della prima: ogni tasto ristampava
    /// l'intera richiesta una riga piu' sotto, e incollare un comando lungo lasciava sullo schermo
    /// centinaia di copie, ciascuna con un carattere in piu'. Dove il terminale capisce le sequenze si
    /// risale quindi alla prima riga, si cancella fino in fondo e si riscrive una volta.
    /// </summary>
    private void Redraw(string prompt, StringBuilder buffer, int cursor)
    {
        string text = buffer.ToString();

        if (Region is { Enabled: true } region)
        {
            // Dentro l'area fissa disegna la regione, che possiede quelle righe: il compositore le
            // toccherebbe con conteggi pensati per una riga che scorre, e le corromperebbe.
            region.Draw(vim is null ? prompt : prompt + vim.Indicator + " ", text, Math.Clamp(cursor, 0, text.Length));
            return;
        }

        // L'indicatore del modo si aggiunge **qui** e non nei chiamanti: la riga si ridisegna da
        // quindici punti diversi, e uno che se lo dimenticasse mostrerebbe un invito che si accorcia
        // e si allunga da solo. Con la modalita' spenta la stringa e' quella di S56.
        string shown = vim is null ? prompt : prompt + vim.Indicator + " ";
        int width = WrapWidth;

        if (width == 0)
        {
            output.Write('\r');
            WritePrompt(shown);
            output.Write(text);
            output.Write(new string(' ', 8));
            output.Write('\r');
            WritePrompt(shown);
            output.Write(text[..Math.Clamp(cursor, 0, text.Length)]);
            return;
        }

        if (cursorRow > 0) { output.Write("\u001b[" + cursorRow.ToString(Formats.Invariant) + "A"); }
        output.Write("\r\u001b[J");
        WritePrompt(shown);
        output.Write(text);

        int end = shown.Length + text.Length;
        int target = shown.Length + Math.Clamp(cursor, 0, text.Length);

        // Scritta l'ultima colonna, il terminale tiene il cursore li' e va a capo solo al carattere dopo:
        // uno spazio e un passo indietro lo fanno andare a capo adesso, cosi' la riga dove sta e' certa.
        if (end > 0 && end % width == 0) { output.Write(" \b"); }

        endRow = end / width;
        cursorRow = target / width;
        if (endRow > cursorRow) { output.Write("\u001b[" + (endRow - cursorRow).ToString(Formats.Invariant) + "A"); }
        output.Write('\r');
        if (target % width > 0) { output.Write("\u001b[" + (target % width).ToString(Formats.Invariant) + "C"); }
    }

    /// <summary>
    /// Lascia la riga disegnata: porta il cursore sotto la sua ultima riga, cosi' cio' che viene scritto dopo
    /// non ricopre la coda di una richiesta andata a capo.
    /// </summary>
    private void LeaveLine()
    {
        // Con l'area fissa la riga dell'invito non sta nella conversazione: non c'e' niente da
        // lasciare, e un a capo qui aggiungerebbe una riga vuota a ogni invio.
        if (Region is { Enabled: true }) { cursorRow = 0; endRow = 0; return; }

        if (endRow > cursorRow) { output.Write("\u001b[" + (endRow - cursorRow).ToString(Formats.Invariant) + "B"); }
        cursorRow = 0;
        endRow = 0;
        output.WriteLine();
    }

    /// <summary>
    /// Incolla del testo come farebbe il terminale: gli a capo continuano la richiesta su una riga nuova, non la
    /// inviano — come un blocco incollato, che e' una richiesta sola.
    /// </summary>
    private static void Paste(string text, StringBuilder buffer, List<string> lines, ref int cursor, ref string prompt)
    {
        foreach (char character in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            if (character == '\n')
            {
                lines.Add(buffer.ToString());
                buffer.Clear();
                cursor = 0;
                prompt = ContinuationText;
            }
            else if (character == '\t' || !char.IsControl(character))
            {
                buffer.Insert(cursor++, character);
            }
        }
    }

    /// <summary>
    /// Rimanda nella conversazione la richiesta appena inviata, con lo stesso invito del riquadro.
    /// Il testo non si interpreta: e' cio' che l'operatore ha scritto, e ne escono le sequenze di
    /// controllo come per qualunque altro contenuto.
    /// </summary>
    private void Echo(string request)
    {
        foreach (string line in request.Split('\n'))
        {
            output.WriteLine("> " + ReplText.Sanitize(line));
        }
    }

    /// <summary>
    /// Cancella la riga che il compositore ha disegnato.
    ///
    /// Con l'area fissa quella riga sta dentro il riquadro, che si ridisegna da solo: qui non si
    /// scrive niente, perche' ogni byte finirebbe nella conversazione sopra — ed e' cosi' che aprire un
    /// menu lasciava una riga di spazi in mezzo alle risposte.
    /// </summary>
    private void ClearLine(int room)
    {
        if (Region is { Enabled: true }) { return; }

        if (WrapWidth == 0) { output.Write("\r" + new string(' ', room) + "\r"); return; }

        // Una richiesta andata a capo occupa piu' righe: si cancella dalla prima, non dall'ultima.
        if (cursorRow > 0) { output.Write("\u001b[" + cursorRow.ToString(Formats.Invariant) + "A"); }
        output.Write("\r\u001b[J");
        cursorRow = 0;
        endRow = 0;
    }

    private void WritePrompt(string prompt)
    {
        if (inputConsole is null) { output.Write(prompt); }
        else { inputConsole.Write(new Text(prompt, new Style(ReplChatStyle.Accent, decoration: Decoration.Bold))); }
    }

}
