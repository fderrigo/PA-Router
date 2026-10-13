using System.Text;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Privacy;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Contenuti che l'operatore allega a una richiesta (S63): <c>pa run --attach</c> e la menzione
/// <c>@percorso</c> del terminale.
///
/// Un allegato e' **contenuto**, non istruzione: entra nel payload come <see cref="ContextItem"/>
/// con la sua provenienza, riattraversa privacy e policy come ogni altro testo, e la sua classe la
/// decide la scansione. Un file che chiedesse di cambiare regola resta un dato.
///
/// Sta qui e non in un front end perche' i due modi di allegare devono leggere lo stesso file allo
/// stesso modo: se il terminale accettasse un file che <c>pa run</c> rifiuta, la stessa richiesta
/// darebbe due esiti (INV-22).
/// </summary>
public static class TurnAttachments
{
    /// <summary>Prefisso degli identificatori, per riconoscerli nel payload e nell'audit.</summary>
    public const string IdPrefix = "attach-";

    /// <summary>
    /// Legge un file e lo trasforma in un elemento di contesto.
    ///
    /// Tre rifiuti espliciti invece di un allegato che sembra riuscito: il file che non c'e', quello
    /// che non e' testo e quello che non sta nel budget del profilo. Nessuno dei tre viene troncato
    /// o ignorato — mandare al modello meta' di un file, o non mandarlo affatto senza dirlo, e' un
    /// modo silenzioso di eseguire una richiesta diversa da quella scritta.
    /// </summary>
    /// <param name="path">Percorso indicato dall'operatore.</param>
    /// <param name="maxBytes">Budget di contesto del profilo, in byte.</param>
    public static ContextItem Read(string path, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = Path.GetFullPath(path);

        if (!File.Exists(full))
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "That file does not exist.")
                .WithMetadata("reason", "attachment_missing");
        }

        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(full);
        }
        catch (IOException exception)
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "That file cannot be read.", exception)
                .WithMetadata("reason", "attachment_unreadable");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "That file cannot be read.", exception)
                .WithMetadata("reason", "attachment_unreadable");
        }

        // Un byte nullo dice che non e' testo. Un binario allegato a una richiesta arriverebbe al
        // modello come una sequenza priva di senso, e la scansione di privacy non puo' dire niente
        // di utile su di esso: meglio rifiutarlo dove l'operatore lo puo' correggere.
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "That file is not text.")
                .WithMetadata("reason", "attachment_not_text");
        }

        string text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes).TrimStart('﻿');

        // Il limite e' quello del profilo, non un numero di questo modulo: superarlo e' un errore
        // tipizzato con la stessa forma delle altre superfici.
        PrivacyScanner.EnsureWithinLimit(text, maxBytes, "attachment");

        return new ContextItem(
            IdPrefix + Path.GetFileName(full),
            text,
            // Il contenuto viene dal disco dell'operatore, non dalla sua tastiera: resta materiale
            // di lavoro, con le regole dei contenuti non fidati.
            Provenance.Workspace,
            // Nessuna classe dichiarata: la decide la scansione. Dichiararla qui significherebbe
            // decidere dove quel testo puo' andare prima di averlo guardato.
            DeclaredClass: null);
    }

    /// <summary>
    /// Percorsi menzionati con <c>@</c> in una riga del terminale.
    ///
    /// Solo a inizio parola, cosi' un indirizzo di posta dentro una frase non diventa un allegato.
    /// La menzione resta nel testo della richiesta: toglierla cambierebbe la domanda che
    /// l'operatore ha scritto, e il modello deve vedere a che cosa si riferisce.
    /// </summary>
    public static IReadOnlyList<string> Mentions(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return [.. ScanMentions(line).Where(item => item.Complete && item.Path.Length > 0).Select(item => item.Path)];
    }

    /// <summary>Shared mention syntax for the input editor; no filesystem access.</summary>
    public static bool TryGetEditingMention(string line, out int start, out string path)
    {
        ArgumentNullException.ThrowIfNull(line);
        foreach (Mention item in ScanMentions(line))
        {
            if (item.End == line.Length && !(item.Quoted && item.Complete))
            {
                start = item.Start;
                path = item.Path;
                return true;
            }
        }
        start = -1;
        path = string.Empty;
        return false;
    }

    /// <summary>Quote spaces and quotes; backslashes remain literal on both Windows and Unix.</summary>
    public static string FormatMention(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Any(c => char.IsWhiteSpace(c) || c is '\"' or '\'')
            ? "@\"" + path.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : "@" + path;
    }

    private readonly record struct Mention(int Start, int End, string Path, bool Quoted, bool Complete);

    private static IEnumerable<Mention> ScanMentions(string line)
    {
        for (int index = 0; index < line.Length; index++)
        {
            if (line[index] != '@' || (index > 0 && !char.IsWhiteSpace(line[index - 1]))) { continue; }
            int start = index++;
            StringBuilder path = new();
            char quote = index < line.Length && line[index] is '\"' or '\'' ? line[index++] : '\0';
            bool complete = quote == '\0';
            while (index < line.Length)
            {
                char current = line[index];
                if (quote == '\0' && char.IsWhiteSpace(current)) { break; }
                index++;
                if (quote != '\0' && current == quote)
                {
                    if (index < line.Length && line[index] == quote) { path.Append(quote); index++; continue; }
                    complete = true;
                    break;
                }
                path.Append(current);
            }
            yield return new Mention(start, index, path.ToString(), quote != '\0', complete);
            index--;
        }
    }
}
