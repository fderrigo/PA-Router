namespace PA.Router.Abstractions;

/// <summary>
/// Dove vivono i modelli installati sulla macchina dell'operatore.
///
/// Il registro dichiara il **nome** del modello, non un percorso: e' lo stesso principio del
/// modello di chat, dove il registro dice <c>llama3.1</c> e la directory la decide chi lo ha
/// installato. Un percorso nel registro sarebbe un modo per far leggere al router un file
/// qualunque del disco, e la configurazione non e' il posto dove concedere quel permesso.
///
/// I modelli stanno sotto la directory dati, in <c>models/&lt;nome&gt;</c>. Il nome e' un solo
/// segmento di forma ristretta: nessun separatore, nessun <c>..</c>, nessuna radice alternativa.
/// </summary>
public static class LocalModelStore
{
    /// <summary>Sottodirectory della directory dati che contiene i modelli.</summary>
    public const string DirectoryName = "models";

    /// <summary>
    /// File opzionale che dichiara il digest dei pesi, scritto al momento dell'installazione.
    ///
    /// Il digest non viene ricalcolato a ogni osservazione: i pesi pesano oltre un gigabyte e
    /// rileggerli prima di ogni turno costerebbe secondi. Se il file non c'e', l'osservazione non
    /// dichiara alcun digest invece di inventarne uno.
    /// </summary>
    public const string DigestFileName = "model.onnx.sha256";

    /// <summary>Directory dei modelli sotto la directory dati indicata.</summary>
    public static string RootFor(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        return Path.Combine(Path.GetFullPath(dataDirectory), DirectoryName);
    }

    /// <summary>
    /// Vero se il nome e' un segmento semplice. Lettere, cifre, punto, trattino e trattino basso:
    /// tutto cio' che potrebbe uscire dalla directory dei modelli e' escluso.
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || name is "." or "..")
        {
            return false;
        }

        foreach (char character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Directory del modello indicato, o null se il nome non e' un segmento ammesso. Il percorso
    /// viene comunque verificato: deve restare sotto la radice dei modelli.
    /// </summary>
    public static string? Resolve(string modelsRoot, string? name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);

        if (!IsValidName(name))
        {
            return null;
        }

        string root = Path.GetFullPath(modelsRoot);
        string candidate = Path.GetFullPath(Path.Combine(root, name!));

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? candidate
            : null;
    }

    /// <summary>Digest dichiarato all'installazione, quando il file esiste ed e' leggibile.</summary>
    public static string? ReadDeclaredDigest(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

        string path = Path.Combine(modelDirectory, DigestFileName);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string content = File.ReadAllText(path).Trim();

            // Un file di digest malformato non viene «aggiustato»: o e' un SHA-256 esadecimale, o
            // non c'e' digest da dichiarare.
            return content.Length == 64 && content.All(char.IsAsciiHexDigit) ? content.ToLowerInvariant() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
