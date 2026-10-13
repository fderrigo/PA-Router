using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Carica configurazione, registro e profili dalla directory dati e ne verifica la coerenza.
/// L'assenza di un file non produce un default permissivo: e' un errore (03-specs/CONFIG.md).
/// </summary>
public sealed class ConfigurationLoader
{
    private readonly IClock clock;

    public ConfigurationLoader(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
    }

    /// <summary>Versione di schema supportata da questa build.</summary>
    public const int SupportedSchemaVersion = 2;

    /// <summary>Nomi accettati per il file di configurazione, in ordine di preferenza.</summary>
    public static IReadOnlyList<string> ConfigFileNames { get; } = ["config.yaml", "config.json"];

    /// <summary>
    /// La lingua dichiarata nel file di configurazione, letta anche quando il resto non e' valido (23 settembre 2026): e'
    /// proprio allora che si mostrano gli errori, e uscivano in inglese. Legge solo la riga `language:` del file YAML, o la
    /// chiave `language` del JSON; null se non c'e' o se il file non si legge.
    /// </summary>
    public static string? DeclaredLanguage(string dataDirectory)
    {
        try
        {
            string? path = ConfigFileNames.Select(name => Path.Combine(dataDirectory, name)).FirstOrDefault(File.Exists);
            if (path is null) { return null; }

            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(path),
                @"^\s*""?language""?\s*:\s*""?(?<code>[A-Za-z]{2,3})""?",
                System.Text.RegularExpressions.RegexOptions.Multiline,
                TimeSpan.FromMilliseconds(100));

            return match.Success ? match.Groups["code"].Value : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>Carica lo snapshot dalla directory dati.</summary>
    public ConfigurationLoadResult Load(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        string root = Path.GetFullPath(dataDirectory);
        List<ValidationIssue> issues = [];

        if (!TryReadConfig(root, issues, out ConfigDocument? config, out JsonNode? configRoot))
        {
            return new ConfigurationLoadResult(null, issues);
        }

        ConfigDocument configuration = config!;

        RegistryDocument? registry = ReadRegistry(root, configuration, issues, out JsonNode? registryRoot);
        List<PolicyDocument> profiles = ReadProfiles(
            root, configuration, issues, out List<JsonNode> profileRoots, out Dictionary<string, string> profileSources);

        if (registry is null || issues.Count > 0)
        {
            return new ConfigurationLoadResult(null, issues);
        }

        SemanticValidator.Validate(
            configuration, registry, profiles, configuration.RegistryFile, profileSources, clock, issues);

        if (issues.Count > 0)
        {
            return new ConfigurationLoadResult(null, issues);
        }

        ConfigurationSnapshot snapshot = new(
            root,
            configuration,
            registry,
            profiles.ToDictionary(profile => profile.ProfileId, StringComparer.Ordinal),
            CanonicalJson.Digest(configRoot),
            CanonicalJson.Digest(registryRoot),
            CanonicalJson.Digest([.. profileRoots.Select(CanonicalJson.Digest)]),
            clock.UtcNow);

        return new ConfigurationLoadResult(snapshot, issues);
    }

    private bool TryReadConfig(
        string root,
        List<ValidationIssue> issues,
        out ConfigDocument? config,
        out JsonNode? documentRoot)
    {
        config = null;
        documentRoot = null;

        string? path = ConfigFileNames
            .Select(name => Path.Combine(root, name))
            .FirstOrDefault(File.Exists);

        if (path is null)
        {
            issues.Add(new ValidationIssue(
                SemanticValidator.ConfigFileName, "", null, ValidationCodes.MissingFile,
                "The data directory has no configuration file. Run 'pa init' to create one."));

            return false;
        }

        string source = Path.GetFileName(path);

        if (!TryReadDocument(ContractSchema.Config, source, path, issues, out JsonNode? node))
        {
            return false;
        }

        documentRoot = node;
        config = DocumentMapper.ToConfig(node!);
        return true;
    }

    private RegistryDocument? ReadRegistry(
        string root,
        ConfigDocument config,
        List<ValidationIssue> issues,
        out JsonNode? documentRoot)
    {
        documentRoot = null;

        if (!PathRules.TryResolveRelative(
                root, SemanticValidator.ConfigFileName, "/registryFile", null, config.RegistryFile, issues, out string path))
        {
            return null;
        }

        if (!TryReadDocument(ContractSchema.Registry, config.RegistryFile, path, issues, out JsonNode? node))
        {
            return null;
        }

        documentRoot = node;
        return DocumentMapper.ToRegistry(node!);
    }

    private List<PolicyDocument> ReadProfiles(
        string root,
        ConfigDocument config,
        List<ValidationIssue> issues,
        out List<JsonNode> documentRoots,
        out Dictionary<string, string> profileSources)
    {
        List<PolicyDocument> profiles = [];
        documentRoots = [];
        profileSources = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int index = 0; index < config.PolicyFiles.Count; index++)
        {
            string relative = config.PolicyFiles[index];
            string pointer = "/policyFiles/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (!PathRules.TryResolveRelative(
                    root, SemanticValidator.ConfigFileName, pointer, null, relative, issues, out string path))
            {
                continue;
            }

            if (!TryReadDocument(ContractSchema.Policy, relative, path, issues, out JsonNode? node))
            {
                continue;
            }

            PolicyDocument profile = DocumentMapper.ToPolicy(node!);
            profiles.Add(profile);
            documentRoots.Add(node!);
            profileSources.TryAdd(profile.ProfileId, relative);
        }

        return profiles;
    }

    private static bool TryReadDocument(
        ContractSchema schema,
        string source,
        string path,
        List<ValidationIssue> issues,
        out JsonNode? root)
    {
        root = null;

        if (!File.Exists(path))
        {
            issues.Add(new ValidationIssue(source, "", null, ValidationCodes.MissingFile, "The file does not exist."));
            return false;
        }

        string text;

        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            issues.Add(new ValidationIssue(source, "", null, ValidationCodes.MissingFile, "The file cannot be read."));
            return false;
        }

        if (!RestrictedYamlReader.TryRead(source, text, issues, out ParsedDocument? document))
        {
            return false;
        }

        // Controllo esplicito prima dello schema: una versione sconosciuta merita un errore che
        // dice che cos'e', non un generico vincolo 'const' violato.
        if (document!.Root is JsonObject root0
            && root0["schemaVersion"] is JsonValue version
            && version.TryGetValue(out long declared)
            && declared != SupportedSchemaVersion)
        {
            issues.Add(new ValidationIssue(
                source,
                "/schemaVersion",
                document.LineOf("/schemaVersion"),
                ValidationCodes.UnknownSchemaVersion,
                "This build supports schema version 2 only."));

            return false;
        }

        // Una forma sostituita merita il nome del comando che la converte, non un generico
        // vincolo violato: la conversione non avviene mai alla lettura. I campi tolti da ADR-032 si
        // dicono tutti insieme, anche accanto al numero unico di qualita'.
        bool legacy = schema is ContractSchema.Registry && LegacyRegistryForms.Report(source, document!, issues);
        legacy |= RemovedFields.Report(schema, source, document!, issues);

        if (legacy)
        {
            return false;
        }

        if (!JsonSchemaValidator.Validate(schema, source, document!, issues))
        {
            return false;
        }

        root = document!.Root;
        return true;
    }
}
