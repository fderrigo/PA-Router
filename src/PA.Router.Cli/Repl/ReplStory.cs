using System.Text;

namespace PA.Router.Cli.Repl;

/// <summary>Una sezione del racconto di un turno: un'intestazione, il suo valore, e i punti che lo spiegano.</summary>
/// <param name="Label">Di che cosa parla: «Dati», «Difficoltà».</param>
/// <param name="Value">Il valore che conta, evidenziato accanto all'intestazione; null se non ce n'e' uno.</param>
/// <param name="Lines">I punti, una frase ciascuno. I dati sono segnati con <see cref="ReplStory.Mark"/>.</param>
internal sealed record StorySection(string Label, string? Value, IReadOnlyList<string> Lines);

/// <summary>
/// Il segno dei dati dentro le frasi del racconto (S117).
///
/// Il racconto e' testo localizzato con dentro dei valori — un nome di modello, un numero di token, una classe —
/// e l'operatore li vuole in evidenza. Il testo non puo' portare sequenze di terminale: ogni riga passa da una
/// pulizia che le toglie, ed e' giusto cosi'. Allora i valori si segnano con due caratteri di controllo che non
/// compaiono in nessun testo del router, e ogni resa decide che cosa farne: quella a colori li mette in grassetto,
/// quella semplice li toglie.
/// </summary>
internal static class ReplStory
{
    private const char Open = '\u0002';
    private const char Close = '\u0003';

    /// <summary>Segna un valore da mettere in evidenza.</summary>
    internal static string Mark(string value) => Open + value + Close;

    /// <summary>Il testo senza segni: cio' che legge chi non ha colori.</summary>
    internal static string Plain(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Replace(Open.ToString(), string.Empty, StringComparison.Ordinal)
            .Replace(Close.ToString(), string.Empty, StringComparison.Ordinal);
    }

    /// <summary>I pezzi di una frase, ciascuno con il suo ruolo: testo normale o dato.</summary>
    internal static IEnumerable<(string Text, bool Marked)> Parts(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder current = new();
        bool marked = false;

        foreach (char character in text)
        {
            if (character is Open or Close)
            {
                if (current.Length > 0) { yield return (current.ToString(), marked); current.Clear(); }
                marked = character == Open;
                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0) { yield return (current.ToString(), marked); }
    }
}
