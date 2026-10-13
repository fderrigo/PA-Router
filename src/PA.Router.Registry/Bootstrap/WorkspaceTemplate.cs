using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using PA.Router.Abstractions.Contracts;
using PA.Router.Registry.Loading;

namespace PA.Router.Registry.Bootstrap;

/// <summary>File scritto o lasciato intatto da <c>pa init</c>.</summary>
/// <param name="RelativePath">Percorso relativo alla directory dati.</param>
/// <param name="Created">Vero se il file e' stato creato adesso.</param>
public readonly record struct BootstrapFile(string RelativePath, bool Created);

/// <summary>
/// Modelli installabili di <c>pa init</c>. Sono gli stessi esempi di
/// <c>docs/06-contracts/examples</c>, inclusi come risorsa: cio' che l'operatore si ritrova nella
/// directory dati e' esattamente il contratto documentato, non una copia riscritta.
///
/// I target restano disabilitati e i segnaposto restano segnaposto: <c>doctor</c> deve completare
/// la configurazione prima che qualcosa sia utilizzabile (03-specs/CONFIG.md, «Bootstrap»).
/// </summary>
public static class WorkspaceTemplate
{
    private const string ExampleProjectId = "00000000-0000-4000-8000-000000000001";
    private const string MemoryOnlyLine = "contentMode: memory_only";
    private const string EncryptedLine = "contentMode: encrypted";

    /// <summary>Prefisso delle risorse dei profili consegnati (cartella <c>Policies</c> degli esempi).</summary>
    private const string PoliciesFolder = "Policies.";

    /// <summary>
    /// Profili installati insieme alla configurazione, nell'ordine in cui la configurazione consegnata li elenca
    /// (<c>policyFiles</c>), come percorsi relativi alla directory dati. Il codice non conosce nessun profilo
    /// (24 settembre 2026): l'elenco e' un dato del modello, e ogni file nominato deve esistere fra le risorse della
    /// cartella dei profili.
    /// </summary>
    public static IReadOnlyList<string> PolicyFiles { get; } = ReadPolicyFiles();

    /// <summary>
    /// Scrive i file mancanti. Un file gia' presente non viene mai sovrascritto: le modifiche
    /// dell'operatore valgono piu' del modello.
    /// </summary>
    /// <param name="dataDirectory">Directory dati da inizializzare.</param>
    /// <param name="projectId">Progetto locale dedicato al chatbot.</param>
    /// <param name="persistContent">Se true la configurazione nasce in modo <c>encrypted</c>.</param>
    public static IReadOnlyList<BootstrapFile> Write(string dataDirectory, Guid projectId, bool persistContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        string root = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "policies"));

        List<BootstrapFile> written =
        [
            WriteFile(root, "config.yaml", ConfigText(projectId, persistContent)),
            WriteFile(root, "registry.yaml", RegistryText()),
        ];

        foreach (string policy in PolicyFiles)
        {
            written.Add(WriteFile(root, policy, Read(PoliciesFolder + Path.GetFileName(policy))));
        }

        return written;
    }

    /// <summary>Segnaposto dell'eseguibile nel registro consegnato, in forma Windows.</summary>
    private const string WindowsExecutablePrefix = "C:/REPLACE_WITH_VERIFIED_PATH/";

    /// <summary>Fine della riga su cui termina il valore del segnaposto.</summary>
    private static readonly char[] CarriageReturnOrNewline = ['\r', '\n'];

    /// <summary>
    /// Testo del registro iniziale, con i segnaposto degli eseguibili nella forma della
    /// piattaforma corrente.
    ///
    /// Il registro consegnato dichiara percorsi come <c>C:/REPLACE_WITH_VERIFIED_PATH/codex.exe</c>.
    /// Sono segnaposto che l'operatore deve sostituire, ma la loro **forma** viene comunque
    /// verificata alla lettura della configurazione, e fuori da Windows un percorso che inizia con
    /// una lettera di unita' non e' pienamente qualificato: <c>pa init</c> produrrebbe una
    /// configurazione non valida appena creata, e ogni comando successivo fallirebbe con
    /// POLICY_INVALID senza che nulla spieghi perche'.
    ///
    /// Qui il segnaposto diventa <c>/REPLACE_WITH_VERIFIED_PATH/codex</c>, che su Linux ha la forma
    /// giusta e resta altrettanto impossibile da usare per sbaglio. Il suffisso <c>.exe</c> se ne
    /// va con la lettera di unita': un eseguibile Unix non lo ha.
    ///
    /// La regola non viene allentata per i servizi disabilitati. Un percorso malformato resta un
    /// errore anche su un servizio spento, come per gli argomenti vietati: e' il modello consegnato
    /// che deve essere valido dove viene installato.
    /// </summary>
    private static string RegistryText()
    {
        string registry = Read("registry.yaml");

        if (OperatingSystem.IsWindows())
        {
            return registry;
        }

        StringBuilder builder = new(registry.Length);
        int position = 0;

        while (true)
        {
            int start = registry.IndexOf(WindowsExecutablePrefix, position, StringComparison.Ordinal);

            if (start < 0)
            {
                builder.Append(registry, position, registry.Length - position);
                break;
            }

            builder.Append(registry, position, start - position);
            builder.Append('/');

            int after = start + WindowsExecutablePrefix.Length;
            int end = registry.IndexOfAny(CarriageReturnOrNewline, after);

            if (end < 0)
            {
                end = registry.Length;
            }

            string name = registry[after..end].TrimEnd();

            builder.Append(WindowsExecutablePrefix.AsSpan("C:/".Length));
            builder.Append(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);

            position = after + name.Length;
        }

        return builder.ToString();
    }

    /// <summary>Testo della configurazione iniziale, con il progetto e il modo richiesti.</summary>
    public static string ConfigText(Guid projectId, bool persistContent)
    {
        string text = Read("config.yaml")
            .Replace(ExampleProjectId, projectId.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal);

        if (persistContent)
        {
            text = text.Replace(MemoryOnlyLine, EncryptedLine, StringComparison.Ordinal);
        }

        return text;
    }

    private static BootstrapFile WriteFile(string root, string relativePath, string text)
    {
        string path = Path.Combine(root, relativePath);

        if (File.Exists(path))
        {
            return new BootstrapFile(relativePath.Replace('\\', '/'), Created: false);
        }

        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new BootstrapFile(relativePath.Replace('\\', '/'), Created: true);
    }

    /// <summary>
    /// Legge <c>policyFiles</c> dalla configurazione consegnata e verifica che ogni profilo nominato sia fra le risorse, e
    /// che ogni risorsa della cartella dei profili sia nominata: un profilo consegnato che <c>pa init</c> non scrivesse, o
    /// un nome senza file, e' un difetto del pacchetto e si scopre alla prima inizializzazione, non in campo.
    /// </summary>
    private static List<string> ReadPolicyFiles()
    {
        List<ValidationIssue> issues = [];

        if (!RestrictedYamlReader.TryRead("config.yaml", Read("config.yaml"), issues, out ParsedDocument? document)
            || document!.Root is not JsonObject config
            || config["policyFiles"] is not JsonArray entries)
        {
            throw new InvalidOperationException("The embedded configuration template does not list its policy files.");
        }

        List<string> files = [.. entries.Select(entry => entry!.GetValue<string>())];

        string prefix = typeof(WorkspaceTemplate).Namespace + ".Examples." + PoliciesFolder;
        HashSet<string> embedded = new(
            typeof(WorkspaceTemplate).Assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(name => name[prefix.Length..]),
            StringComparer.Ordinal);

        HashSet<string> listed = new(files.Select(Path.GetFileName)!, StringComparer.Ordinal);

        if (!embedded.SetEquals(listed))
        {
            throw new InvalidOperationException("The embedded policy templates do not match the configuration template.");
        }

        return files;
    }

    private static string Read(string fileName)
    {
        string resourceName = typeof(WorkspaceTemplate).Namespace + ".Examples." + fileName;
        Assembly assembly = typeof(WorkspaceTemplate).Assembly;

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Missing embedded example: " + resourceName);
        using StreamReader reader = new(stream, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
