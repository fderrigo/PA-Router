namespace PA.Router.Cli.Repl;

/// <summary>
/// Il comando piu' vicino a quello che e' stato scritto (S63).
///
/// Serve a chi sbaglia una lettera: rileggere l'intero elenco per un `/doctr` e' un costo che il
/// terminale puo' togliere. La regola vive qui perche' e' una **regola**, non una resa, e perche'
/// cosi' si puo' provare senza pilotare un REPL intero.
///
/// Il suggerimento non esegue mai nulla: propone un nome e basta. Un comando eseguito al posto di
/// quello scritto sarebbe il modo piu' rapido di far accadere una cosa che nessuno ha chiesto.
/// </summary>
internal static class ReplSuggestion
{
    /// <summary>
    /// Distanza massima ammessa. Due: copre una lettera sbagliata, una mancante e una di troppo,
    /// senza arrivare a proporre `/cost` per chi ha scritto `/context`.
    /// </summary>
    private const int MaxDistance = 2;

    /// <summary>
    /// Comando piu' vicino, oppure null quando nessuno lo e' abbastanza.
    ///
    /// A parita' di distanza vince l'ordine alfabetico: il suggerimento deve essere lo stesso ogni
    /// volta che si sbaglia allo stesso modo.
    /// </summary>
    internal static string? Closest(string? typed, IReadOnlyList<string> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        string text = (typed ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return null;
        }

        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (string command in commands.OrderBy(command => command, StringComparer.Ordinal))
        {
            int distance = Distance(text, command);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = command;
            }
        }

        return bestDistance <= MaxDistance ? best : null;
    }

    /// <summary>
    /// Distanza di edit fra due parole, senza distinzione fra maiuscole e minuscole.
    ///
    /// Implementata con due righe invece della matrice intera: le parole sono nomi di comandi, ma
    /// non c'e' ragione di allocare una matrice per confrontare dieci caratteri.
    /// </summary>
    private static int Distance(string left, string right)
    {
        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];

        for (int index = 0; index <= right.Length; index++)
        {
            previous[index] = index;
        }

        for (int row = 1; row <= left.Length; row++)
        {
            current[0] = row;

            for (int column = 1; column <= right.Length; column++)
            {
                int substitution = previous[column - 1]
                    + (char.ToLowerInvariant(left[row - 1]) == char.ToLowerInvariant(right[column - 1]) ? 0 : 1);

                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
