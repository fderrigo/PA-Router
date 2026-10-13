using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Routing;

/// <summary>
/// Classificazione di una richiesta: tipo di azione, bisogno di comandi di sistema e difficolta'
/// minima.
/// </summary>
/// <param name="Intent">Tipo di azione dedotto dal testo.</param>
/// <param name="NeedsShell">Vero se il testo chiede di eseguire comandi.</param>
/// <param name="BandFloor">Banda minima: la dimensione della richiesta puo' solo alzarla.</param>
/// <param name="Version">Versione delle regole, registrata come quella del ranking.</param>
/// <param name="Signals">Segnali trovati, per spiegare la classificazione; mai il testo.</param>
public sealed record RequestClassification(
    Intent Intent,
    bool NeedsShell,
    DifficultyBand BandFloor,
    string Version,
    IReadOnlyList<string> Signals);

/// <summary>
/// Classificatore a regole del testo **scritto dall'operatore** (ADR-019).
///
/// Non concede nulla: dice che cosa la richiesta sembra chiedere, e il router sceglie il minimo
/// necessario dentro il recinto che l'operatore ha fissato. Per questo le regole **scendono**
/// nell'incertezza: un verbo senza un oggetto di codice, o un oggetto senza un verbo, restano chat.
/// Classificare «chat» una richiesta che voleva modificare file costa una risposta a parole;
/// classificare «modifica» una domanda farebbe partire un agente che scrive.
///
/// Legge solo l'input: contesto allegato, ricordi, documenti recuperati, skill e risposte
/// precedenti non passano di qui. Un testo incollato che dice «esegui questo comando» e' pur sempre
/// un testo dell'operatore, ma resta dentro il recinto e i comandi di sistema chiedono conferma.
/// </summary>
public static class RequestClassifier
{
    /// <summary>Versione delle regole.</summary>
    public const string Version = "classify-rules-v1";

    private static readonly HashSet<string> ChangeVerbs = new(StringComparer.Ordinal)
    {
        "modifica", "modificare", "modificalo", "correggi", "correggere", "correggilo", "sistema", "sistemare",
        "aggiungi", "aggiungere", "rimuovi", "rimuovere", "elimina", "eliminare", "rinomina", "rinominare",
        "implementa", "implementare", "crea", "creare", "riscrivi", "riscrivere", "rifattorizza", "aggiorna",
        "aggiornare", "sostituisci", "sostituire", "sposta", "spostare", "applica",
        "fix", "change", "modify", "add", "remove", "delete", "rename", "implement", "create", "rewrite",
        "refactor", "update", "replace", "move", "apply",
    };

    private static readonly HashSet<string> ReadVerbs = new(StringComparer.Ordinal)
    {
        "leggi", "leggere", "analizza", "analizzare", "spiega", "spiegami", "spiegare", "rivedi", "revisiona",
        "controlla", "controllare", "cerca", "trova", "esamina", "descrivi", "riassumi",
        "read", "analyze", "analyse", "explain", "review", "inspect", "check", "find", "search", "describe",
        "summarize", "summarise", "audit",
    };

    private static readonly HashSet<string> CodeObjects = new(StringComparer.Ordinal)
    {
        "codice", "file", "funzione", "funzioni", "metodo", "metodi", "classe", "classi", "test", "bug",
        "repository", "repo", "progetto", "modulo", "sorgente", "sorgenti", "script", "cartella", "directory",
        "commit", "branch", "compilazione", "interfaccia", "variabile",
        "code", "function", "functions", "method", "methods", "class", "classes", "tests", "source", "module",
        "project", "folder", "codebase",
    };

    private static readonly HashSet<string> ShellVerbs = new(StringComparer.Ordinal)
    {
        "esegui", "eseguire", "lancia", "lanciare", "avvia", "avviare", "compila", "compilare", "installa",
        "run", "execute", "build", "compile", "install",
    };

    private static readonly HashSet<string> ShellTools = new(StringComparer.Ordinal)
    {
        "dotnet", "npm", "npx", "yarn", "pnpm", "git", "pytest", "make", "cargo", "mvn", "gradle", "pip",
        "powershell", "bash", "docker",
    };

    private static readonly string[] WideScope =
    [
        "tutto il progetto", "intero progetto", "tutti i file", "intera soluzione", "tutto il codice",
        "architettura", "whole project", "entire project", "entire codebase", "all files", "whole codebase",
    ];

    private static readonly Regex FilePath = new(
        @"(?:^|[\s`'""(])[\w.\-/\\]+\.(?:cs|csproj|sln|ts|tsx|js|jsx|py|java|go|rs|cpp|c|h|hpp|json|ya?ml|xml|md|sql|ps1|sh|toml|html|css)(?:$|[\s`'"").,:;])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(200));

    /// <summary>Classifica il testo scritto dall'operatore.</summary>
    public static RequestClassification Classify(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        string text = Normalize(input);
        HashSet<string> words = [.. Words(text)];
        List<string> signals = [];

        string? change = words.FirstOrDefault(ChangeVerbs.Contains);
        string? read = words.FirstOrDefault(ReadVerbs.Contains);
        string? shellVerb = words.FirstOrDefault(ShellVerbs.Contains);
        string? tool = words.FirstOrDefault(ShellTools.Contains);
        string? codeObject = words.FirstOrDefault(CodeObjects.Contains);

        bool path = SafeMatch(FilePath, input);
        bool fence = input.Contains("```", StringComparison.Ordinal);

        if (codeObject is not null)
        {
            signals.Add("oggetto:" + codeObject);
        }

        if (path)
        {
            signals.Add("percorso");
        }

        if (fence)
        {
            signals.Add("blocco-di-codice");
        }

        bool aboutCode = codeObject is not null || path || fence;

        bool shell = aboutCode && (shellVerb is not null || tool is not null);

        if (shell)
        {
            signals.Add("comando:" + (tool ?? shellVerb));
        }

        Intent intent;

        if (!aboutCode)
        {
            // Nessun oggetto di codice: qualunque verbo resta una conversazione. «Scrivi una mail»
            // non e' una modifica di file.
            intent = Intent.Chat;
        }
        else if (change is not null || shell)
        {
            if (change is not null)
            {
                signals.Add("verbo:" + change);
            }

            intent = Intent.CodeChange;
        }
        else if (read is not null)
        {
            signals.Add("verbo:" + read);
            intent = Intent.CodeAnalysis;
        }
        else
        {
            // Un oggetto senza verbo — «che cos'e' un file json» — non basta per un agente.
            intent = Intent.Chat;
        }

        DifficultyBand floor = DifficultyBand.Trivial;

        if (intent is Intent.CodeChange)
        {
            floor = DifficultyBand.Standard;
        }

        if (intent is not Intent.Chat && WideScope.Any(scope => text.Contains(scope, StringComparison.Ordinal)))
        {
            signals.Add("ambito-esteso");
            floor = DifficultyBand.Heavy;
        }

        return new RequestClassification(intent, intent is Intent.CodeChange && shell, floor, Version, signals);
    }

    /// <summary>Minuscole, senza accenti e con l'apostrofo come separatore.</summary>
    private static string Normalize(string input)
    {
        string decomposed = input.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        StringBuilder builder = new(decomposed.Length);

        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) is not UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character is '\'' or '’' ? ' ' : character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static IEnumerable<string> Words(string text)
    {
        StringBuilder current = new();

        foreach (char character in text)
        {
            if (char.IsLetter(character))
            {
                current.Append(character);
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static bool SafeMatch(Regex regex, string input)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            // Un testo costruito per rallentare l'espressione non diventa un segnale: si scende.
            return false;
        }
    }
}
