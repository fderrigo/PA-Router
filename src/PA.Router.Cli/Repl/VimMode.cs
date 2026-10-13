using System.Text;

namespace PA.Router.Cli.Repl;

/// <summary>Modo del compositore di riga quando la modalita' vim e' accesa.</summary>
internal enum VimState
{
    /// <summary>Si scrive: ogni tasto entra nella riga, come senza modalita' vim.</summary>
    Insert,

    /// <summary>I tasti sono comandi: movimenti e operatori, nessun carattere inserito.</summary>
    Normal,
}

/// <summary>Che cosa il compositore deve fare dopo aver consultato il motore modale.</summary>
internal enum VimOutcome
{
    /// <summary>Il tasto non riguarda la modalita': prosegue il percorso normale del compositore.</summary>
    Ignored,

    /// <summary>Il tasto e' stato consumato: la riga e' cambiata e va ridisegnata.</summary>
    Handled,
}

/// <summary>
/// Modi normale e inserimento del compositore di riga (S67).
///
/// **Sta qui perche' e' una regola, non una resa**: quali tasti muovono, quali cancellano e quando
/// si cambia modo si provano senza pilotare un terminale. E' la stessa scelta del selettore delle
/// sessioni e del suggerimento del comando vicino.
///
/// L'insieme e' **piccolo di proposito**: `Esc` per il modo normale, `i a I A o` per tornare a
/// scrivere, movimenti `h l w b 0 $`, operatori `x D dd dw`, e `k j` sulla cronologia — la stessa
/// cronologia delle frecce, non una seconda. Tutto il resto resta inserimento. Un vim finto che
/// accetta dieci comandi e ne ignora altri trenta e' peggio di nessun vim: chi lo conosce si aspetta
/// che funzionino, e scopre quali no premendo tasti dentro una richiesta vera.
///
/// La modalita' nasce **spenta** e il motore non viene nemmeno consultato: con la modalita' spenta
/// il percorso dei tasti e' quello di S56, byte per byte. E' il gate di questo step, ottenuto per
/// costruzione invece che per verifica a campione.
/// </summary>
internal sealed class VimMode
{
    /// <summary>Operatore in attesa del proprio movimento: <c>d</c> che aspetta <c>d</c> o <c>w</c>.</summary>
    private char pending;

    /// <summary>Modo corrente. Accendendo la modalita' si comincia scrivendo, non in normale.</summary>
    internal VimState State { get; private set; } = VimState.Insert;

    /// <summary>Indicatore per l'invito: due lettere, perche' l'invito resta corto.</summary>
    internal string Indicator => State is VimState.Normal ? "N" : "I";

    /// <summary>Torna al modo inserimento. Serve quando la riga e' stata consegnata.</summary>
    internal void Reset()
    {
        State = VimState.Insert;
        pending = '\0';
    }

    /// <summary>
    /// Consulta il motore per un tasto.
    ///
    /// In inserimento consuma **solo** <c>Esc</c>: tutto il resto prosegue nel percorso normale del
    /// compositore, quindi scrivere una richiesta si comporta come sempre. In normale consuma i
    /// tasti che riconosce e lascia passare gli altri — cosi' Invio, Tab, le frecce e Ctrl+C
    /// continuano a fare quello che facevano.
    /// </summary>
    /// <param name="key">Tasto appena letto.</param>
    /// <param name="buffer">Riga in composizione.</param>
    /// <param name="cursor">Posizione del cursore, aggiornata se il tasto la muove.</param>
    /// <param name="history">Cronologia della sessione, per i movimenti verticali.</param>
    internal VimOutcome Handle(ConsoleKeyInfo key, StringBuilder buffer, ref int cursor, ReplHistory history)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(history);

        if (key.Key is ConsoleKey.Escape)
        {
            State = VimState.Normal;
            pending = '\0';

            // Come in vim: uscendo dall'inserimento il cursore arretra di uno, perche' in normale
            // sta **sul** carattere e non dopo.
            cursor = Math.Max(0, Math.Min(cursor, buffer.Length) - (buffer.Length > 0 ? 1 : 0));

            return VimOutcome.Handled;
        }

        if (State is VimState.Insert)
        {
            return VimOutcome.Ignored;
        }

        // Control e Alt in modo normale restano del compositore: Ctrl+L pulisce lo schermo, Ctrl+R
        // cerca nella cronologia, e non c'e' ragione di togliere quei gesti.
        //
        // **Shift no.** Le maiuscole del modo normale — `I`, `A`, `D` — arrivano dal terminale con
        // Shift premuto: scartarle qui le avrebbe mandate al percorso normale del compositore, che le
        // avrebbe **inserite nella riga**. Un `D` che scrive «D» invece di cancellare fino a fine riga
        // e' il modo peggiore di sbagliare, perche' sporca la richiesta.
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) || key.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            return VimOutcome.Ignored;
        }

        if (pending is 'd')
        {
            return Delete(key.KeyChar, buffer, ref cursor);
        }

        switch (key.KeyChar)
        {
            case 'i':
                State = VimState.Insert;
                return VimOutcome.Handled;

            case 'a':
                State = VimState.Insert;
                cursor = Math.Min(cursor + 1, buffer.Length);
                return VimOutcome.Handled;

            case 'I':
                State = VimState.Insert;
                cursor = 0;
                return VimOutcome.Handled;

            case 'A':
                State = VimState.Insert;
                cursor = buffer.Length;
                return VimOutcome.Handled;

            case 'o':
                // Non apre una riga nuova nel senso di vim: la richiesta e' una sola riga logica, e
                // la continuazione ha gia' un gesto suo (Ctrl+J). Porta in fondo e fa scrivere.
                State = VimState.Insert;
                cursor = buffer.Length;
                return VimOutcome.Handled;

            case 'h':
                cursor = Math.Max(0, cursor - 1);
                return VimOutcome.Handled;

            case 'l':
                cursor = Math.Min(Math.Max(0, buffer.Length - 1), cursor + 1);
                return VimOutcome.Handled;

            case '0':
                cursor = 0;
                return VimOutcome.Handled;

            case '$':
                cursor = Math.Max(0, buffer.Length - 1);
                return VimOutcome.Handled;

            case 'w':
                cursor = NextWord(buffer, cursor);
                return VimOutcome.Handled;

            case 'b':
                cursor = PreviousWord(buffer, cursor);
                return VimOutcome.Handled;

            case 'x' when cursor < buffer.Length:
                buffer.Remove(cursor, 1);
                cursor = Math.Min(cursor, Math.Max(0, buffer.Length - 1));
                return VimOutcome.Handled;

            case 'D':
                if (cursor < buffer.Length)
                {
                    buffer.Remove(cursor, buffer.Length - cursor);
                    cursor = Math.Max(0, buffer.Length - 1);
                }

                return VimOutcome.Handled;

            case 'd':
                // Operatore in attesa del movimento: da solo non cancella niente.
                pending = 'd';
                return VimOutcome.Handled;

            case 'k' when history.Previous() is string previous:
                buffer.Clear();
                buffer.Append(previous);
                cursor = Math.Max(0, buffer.Length - 1);
                return VimOutcome.Handled;

            case 'j' when history.Next() is string next:
                buffer.Clear();
                buffer.Append(next);
                cursor = Math.Max(0, buffer.Length - 1);
                return VimOutcome.Handled;

            default:
                // Un tasto che il modo normale non conosce **non si inserisce**: in vim non
                // scriverebbe, e scriverlo qui sarebbe la sorpresa peggiore.
                return key.KeyChar is not '\0' && !char.IsControl(key.KeyChar)
                    ? VimOutcome.Handled
                    : VimOutcome.Ignored;
        }
    }

    /// <summary>Completa un operatore di cancellazione con il suo movimento.</summary>
    private VimOutcome Delete(char movement, StringBuilder buffer, ref int cursor)
    {
        pending = '\0';

        switch (movement)
        {
            case 'd':
                buffer.Clear();
                cursor = 0;
                return VimOutcome.Handled;

            case 'w':
                int end = NextWord(buffer, cursor);

                if (end > cursor)
                {
                    buffer.Remove(cursor, end - cursor);
                    cursor = Math.Min(cursor, Math.Max(0, buffer.Length - 1));
                }

                return VimOutcome.Handled;

            default:
                // `d` seguito da un movimento che non si conosce non cancella niente: annullare e'
                // meglio che cancellare la cosa sbagliata.
                return VimOutcome.Handled;
        }
    }

    /// <summary>Inizio della parola successiva.</summary>
    private static int NextWord(StringBuilder buffer, int cursor)
    {
        int index = cursor;

        while (index < buffer.Length && buffer[index] != ' ')
        {
            index++;
        }

        while (index < buffer.Length && buffer[index] == ' ')
        {
            index++;
        }

        return index;
    }

    /// <summary>Inizio della parola precedente.</summary>
    private static int PreviousWord(StringBuilder buffer, int cursor)
    {
        int index = Math.Min(cursor, buffer.Length);

        while (index > 0 && buffer[index - 1] == ' ')
        {
            index--;
        }

        while (index > 0 && buffer[index - 1] != ' ')
        {
            index--;
        }

        return index;
    }
}
