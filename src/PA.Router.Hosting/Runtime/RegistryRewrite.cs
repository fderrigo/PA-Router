using System.Text.Json;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>Esito di una riscrittura di un file della configurazione.</summary>
public enum RegistryRewriteStatus
{
    /// <summary>Scritto, con l'originale accanto, e la configurazione si carica.</summary>
    Written,

    /// <summary>Nulla da cambiare: il file non e' stato toccato.</summary>
    Unchanged,

    /// <summary>Il file non esiste.</summary>
    Missing,

    /// <summary>Il file non si legge.</summary>
    Unreadable,

    /// <summary>Il risultato non si caricava: l'originale e' stato rimesso al suo posto.</summary>
    Restored,
}

/// <summary>Esito di una riscrittura di un file della configurazione.</summary>
/// <param name="Status">Che cosa e' successo.</param>
/// <param name="FileName">Nome del file riscritto.</param>
/// <param name="Issues">Problemi del risultato, quando e' stato rifiutato.</param>
public sealed record RegistryRewriteResult(
    RegistryRewriteStatus Status,
    string FileName,
    IReadOnlyList<ValidationIssue> Issues);

/// <summary>
/// Riscrittura di un file della configurazione da parte di un comando dell'operatore, in un punto
/// solo (INV-22).
///
/// `pa registry calibrate --apply` e `pa registry rate` modificano il registro, `pa classifier
/// calibrate --apply` la configurazione, con le stesse garanzie: l'originale resta accanto, la
/// configurazione viene riletta subito, e un risultato che non si carica **non resta al suo posto**.
/// Lasciarlo significherebbe un router che al comando successivo non parte per una modifica che
/// l'operatore credeva riuscita.
/// </summary>
public static class RegistryRewrite
{
    /// <summary>Applica la modifica al registro della configurazione.</summary>
    /// <param name="snapshot">Configurazione caricata.</param>
    /// <param name="backupSuffix">Suffisso della copia dell'originale.</param>
    /// <param name="mutate">Modifica del documento; restituisce falso se non c'e' nulla da cambiare.</param>
    /// <param name="clock">Orologio della rilettura.</param>
    public static RegistryRewriteResult Apply(
        ConfigurationSnapshot snapshot,
        string backupSuffix,
        Func<JsonObject, bool> mutate,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return Rewrite(snapshot, snapshot.Config.RegistryFile, backupSuffix, mutate, clock);
    }

    /// <summary>Applica la modifica al file di configurazione principale.</summary>
    public static RegistryRewriteResult ApplyToConfiguration(
        ConfigurationSnapshot snapshot,
        string backupSuffix,
        Func<JsonObject, bool> mutate,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string? name = ConfigurationLoader.ConfigFileNames
            .FirstOrDefault(candidate => File.Exists(Path.Combine(snapshot.DataDirectory, candidate)));

        return name is null
            ? new RegistryRewriteResult(RegistryRewriteStatus.Missing, ConfigurationLoader.ConfigFileNames[0], [])
            : Rewrite(snapshot, name, backupSuffix, mutate, clock);
    }

    private static RegistryRewriteResult Rewrite(
        ConfigurationSnapshot snapshot,
        string relativePath,
        string backupSuffix,
        Func<JsonObject, bool> mutate,
        IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupSuffix);
        ArgumentNullException.ThrowIfNull(mutate);
        ArgumentNullException.ThrowIfNull(clock);

        string path = Path.Combine(snapshot.DataDirectory, relativePath);
        string fileName = Path.GetFileName(path);

        if (!File.Exists(path))
        {
            return new RegistryRewriteResult(RegistryRewriteStatus.Missing, fileName, []);
        }

        string original = File.ReadAllText(path);
        List<ValidationIssue> issues = [];

        if (!RestrictedYamlReader.TryRead(fileName, original, issues, out ParsedDocument? document)
            || document!.Root is not JsonObject root)
        {
            return new RegistryRewriteResult(RegistryRewriteStatus.Unreadable, fileName, issues);
        }

        if (!mutate(root))
        {
            return new RegistryRewriteResult(RegistryRewriteStatus.Unchanged, fileName, []);
        }

        File.Copy(path, path + backupSuffix, overwrite: true);

        WriteWhole(
            path,
            string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)
                ? root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n"
                : RestrictedYamlWriter.Write(root));

        ConfigurationLoadResult reloaded = new ConfigurationLoader(clock).Load(snapshot.DataDirectory);

        if (!reloaded.IsValid)
        {
            WriteWhole(path, original);

            return new RegistryRewriteResult(RegistryRewriteStatus.Restored, fileName, reloaded.Issues);
        }

        return new RegistryRewriteResult(RegistryRewriteStatus.Written, fileName, []);
    }

    /// <summary>
    /// Sostituisce il file in un colpo: un worker che rilegge la configurazione in questo momento
    /// vede il file di prima o quello di dopo, mai uno scritto a meta'.
    /// </summary>
    private static void WriteWhole(string path, string text)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("n");

        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }
}
