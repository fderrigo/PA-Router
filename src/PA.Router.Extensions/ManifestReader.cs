using System.Text.Json;
using System.Text.RegularExpressions;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Extensions;

/// <summary>
/// Lettura del manifesto di un pacchetto (06-contracts/plugin.schema.json).
///
/// La lettura e' severa quanto lo schema: campi sconosciuti rifiutati, identificatori e versioni
/// con la forma prevista, percorsi relativi normalizzati senza <c>..</c>, senza radice e senza
/// due punti. Un manifesto e' il documento su cui si basa un'autorizzazione: accettarne uno
/// approssimativo significherebbe autorizzare qualcosa di diverso da cio' che si e' letto.
/// </summary>
public static partial class ManifestReader
{
    /// <summary>Nome del file di manifesto.</summary>
    public const string FileName = "pa-plugin.json";

    /// <summary>Versione di schema supportata.</summary>
    public const int SupportedSchemaVersion = 1;

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern { get; }

    [GeneratedRegex(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern { get; }

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern { get; }

    [GeneratedRegex("^[A-Za-z0-9_./-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern { get; }

    /// <summary>Legge e valida un manifesto dal suo testo JSON.</summary>
    public static PluginManifest Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw Invalid("manifest_not_json");
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object)
            {
                throw Invalid("manifest_not_object");
            }

            RejectUnknown(root, [
                "schemaVersion", "id", "version", "description", "files",
                "permissions", "skills", "tools", "nativeBindings"]);

            int schemaVersion = Integer(root, "schemaVersion");

            if (schemaVersion != SupportedSchemaVersion)
            {
                throw Invalid("manifest_schema_version");
            }

            string id = Pattern(root, "id", IdentifierPattern, "manifest_id");
            string version = Pattern(root, "version", VersionPattern, "manifest_version");
            string description = String(root, "description");

            if (description.Length > 1024)
            {
                throw Invalid("manifest_description_too_long");
            }

            RejectDeclaredTools(root);
            RejectDeclaredPermissions(root);

            return new PluginManifest(
                schemaVersion,
                id,
                version,
                description,
                ReadFiles(root),
                ReadSkills(root),
                ReadBindings(root));
        }
    }

    /// <summary>
    /// Verifica che un percorso dichiarato resti dentro la radice del pacchetto.
    ///
    /// Il controllo e' su tre livelli: la forma testuale, il risultato della normalizzazione e il
    /// tipo di voce sul disco. Un <c>..</c> scritto, un percorso che dopo la combinazione esce
    /// dalla radice e un link o reparse point vengono tutti rifiutati; il terzo caso e' quello che
    /// una verifica puramente testuale non intercetterebbe.
    /// </summary>
    public static string ResolveInside(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (!PathPattern.IsMatch(relativePath)
            || relativePath.StartsWith('/')
            || relativePath.Contains(':', StringComparison.Ordinal)
            || Segments(relativePath).Any(segment => segment is ".."))
        {
            throw Invalid("path_not_relative");
        }

        string fullRoot = Path.GetFullPath(root);
        string combined = Path.GetFullPath(Path.Combine(fullRoot, relativePath));

        if (!combined.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw Invalid("path_escapes_root");
        }

        FileInfo file = new(combined);

        if (file.Exists && file.LinkTarget is not null)
        {
            // Un link o un reparse point punta altrove: il file verificato non sarebbe quello letto.
            throw Invalid("path_is_link");
        }

        foreach (string segment in Segments(relativePath)[..^1])
        {
            DirectoryInfo directory = new(Path.Combine(fullRoot, segment));

            if (directory.Exists && directory.LinkTarget is not null)
            {
                throw Invalid("path_is_link");
            }
        }

        return combined;
    }

    private static string[] Segments(string relativePath) =>
        relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyList<PluginFile> ReadFiles(JsonElement root)
    {
        JsonElement files = Array(root, "files");

        if (files.GetArrayLength() is 0 or > ExtensionLimits.MaxFiles)
        {
            throw Invalid("manifest_files_count");
        }

        List<PluginFile> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement item in files.EnumerateArray())
        {
            RejectUnknown(item, ["path", "sha256"]);

            string path = Pattern(item, "path", PathPattern, "manifest_file_path");
            string digest = Pattern(item, "sha256", DigestPattern, "manifest_file_digest");

            if (!seen.Add(path))
            {
                // Due voci per lo stesso file darebbero due digest possibili per lo stesso byte.
                throw Invalid("manifest_file_duplicate");
            }

            result.Add(new PluginFile(path, digest));
        }

        return result;
    }

    /// <summary>
    /// Rifiuta un pacchetto che dichiara permessi (S85).
    ///
    /// I permessi delle estensioni non esistono piu': l'unico codice che ne pretendeva uno era il
    /// broker degli strumenti e il ponte verso la memoria, rimossi in S81, e da allora nessun
    /// percorso di esecuzione li consultava. Dichiararne uno oggi non concede niente, e accettarlo
    /// in silenzio lascerebbe credere il contrario.
    ///
    /// Il campo resta **obbligatorio** nello schema e deve essere vuoto: toglierlo farebbe rifiutare
    /// ogni manifesto come «proprieta' sconosciuta», che e' un messaggio peggiore di un rifiuto che
    /// nomina il motivo.
    /// </summary>
    private static void RejectDeclaredPermissions(JsonElement root)
    {
        if (Array(root, "permissions").GetArrayLength() > 0)
        {
            throw Invalid("manifest_permissions_not_supported");
        }
    }

    private static IReadOnlyList<PluginSkill> ReadSkills(JsonElement root)
    {
        List<PluginSkill> skills = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (JsonElement item in Array(root, "skills").EnumerateArray())
        {
            RejectUnknown(item, ["id", "description", "entry", "resources"]);

            string id = Pattern(item, "id", IdentifierPattern, "manifest_skill_id");

            if (!seen.Add(id))
            {
                throw Invalid("manifest_skill_duplicate");
            }

            List<string> resources = [];

            foreach (JsonElement resource in Array(item, "resources").EnumerateArray())
            {
                if (resource.ValueKind is not JsonValueKind.String || !PathPattern.IsMatch(resource.GetString()!))
                {
                    throw Invalid("manifest_skill_resource");
                }

                resources.Add(resource.GetString()!);
            }

            skills.Add(new PluginSkill(
                id,
                String(item, "description"),
                Pattern(item, "entry", PathPattern, "manifest_skill_entry"),
                resources));
        }

        return skills;
    }

    /// <summary>
    /// Rifiuta un pacchetto che dichiara strumenti eseguibili (S81).
    ///
    /// Un plugin porta **testo e risorse**: istruzioni di skill, file enumerati e verificati. Il
    /// router non avvia piu' programmi di terze parti, quindi un manifesto che ne dichiara uno viene
    /// rifiutato alla lettura, dove l'operatore puo' correggerlo — non ignorato in silenzio, che
    /// lascerebbe credere di aver installato qualcosa che non verra' mai eseguito.
    ///
    /// Il campo resta **obbligatorio** nello schema. Toglierlo farebbe rifiutare ogni pacchetto gia'
    /// installato, perche' il manifesto non ammette proprieta' sconosciute e quei manifesti
    /// dichiarano tutti `"tools": []`: la compatibilita' si conserva pretendendo la lista vuota, non
    /// cancellando la chiave.
    /// </summary>
    private static void RejectDeclaredTools(JsonElement root)
    {
        if (Array(root, "tools").GetArrayLength() > 0)
        {
            throw Invalid("manifest_tools_not_supported");
        }
    }

    private static IReadOnlyList<NativeBinding> ReadBindings(JsonElement root)
    {
        List<NativeBinding> bindings = [];

        foreach (JsonElement item in Array(root, "nativeBindings").EnumerateArray())
        {
            RejectUnknown(item, ["provider", "pluginId", "versionRange", "configDigest"]);

            string provider = String(item, "provider");

            if (provider is not ("claude-code-cli" or "codex-cli"))
            {
                throw Invalid("manifest_binding_provider");
            }

            bindings.Add(new NativeBinding(
                provider,
                String(item, "pluginId"),
                String(item, "versionRange"),
                Pattern(item, "configDigest", DigestPattern, "manifest_binding_digest")));
        }

        return bindings;
    }

    private static void RejectUnknown(JsonElement element, IReadOnlyList<string> allowed)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Invalid("manifest_unknown_property");
            }
        }
    }

    private static JsonElement Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.Array
            ? value
            : throw Invalid("manifest_missing_" + name);

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()!
            : throw Invalid("manifest_missing_" + name);

    private static int Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.Number
            ? value.GetInt32()
            : throw Invalid("manifest_missing_" + name);

    private static string Pattern(JsonElement element, string name, Regex pattern, string reason)
    {
        string value = String(element, name);

        return pattern.IsMatch(value) ? value : throw Invalid(reason);
    }

    private static PaRouterException Invalid(string reason) =>
        new PaRouterException(ErrorCode.InputInvalid, "The plugin manifest is not valid.")
            .WithMetadata("reason", reason);
}
