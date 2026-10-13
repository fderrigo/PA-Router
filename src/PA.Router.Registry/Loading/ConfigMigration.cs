using System.Text.Json.Nodes;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Loading;

/// <summary>Esito della conversione di un singolo file.</summary>
/// <param name="RelativePath">Percorso del file, relativo alla directory dati.</param>
/// <param name="Kind">Contratto riconosciuto.</param>
/// <param name="Changes">Modifiche che la conversione applica, in ordine di lettura.</param>
/// <param name="Converted">Documento convertito, gia' serializzato.</param>
public sealed record MigratedFile(
    string RelativePath,
    ContractSchema Kind,
    IReadOnlyList<string> Changes,
    string Converted)
{
    /// <summary>Profilo convertito, per nominarlo davanti a una persona; null per gli altri contratti.</summary>
    public string? ProfileId { get; init; }

    /// <summary>Nome dato dall'operatore al profilo convertito (<c>name</c>), se lo ha; null altrimenti.</summary>
    public string? ProfileName { get; init; }

    /// <summary>
    /// Zone calcolate dalla forma precedente, quando la conversione le ha scritte (24 settembre 2026): la resa le dice
    /// con i nomi delle zone invece della riga tecnica di <see cref="Changes"/>.
    /// </summary>
    public PolicyZones? Zones { get; init; }
}

/// <summary>Esito complessivo della migrazione.</summary>
/// <param name="Files">File convertiti.</param>
/// <param name="Problems">Motivi che impediscono la conversione, in ordine di scoperta.</param>
/// <param name="BackupSuffix">
/// Suffisso della copia dell'originale. Ogni conversione ha il proprio: la copia di una conversione
/// precedente documenta un altro stato di partenza e non va confusa con questa.
/// </param>
public sealed record MigrationPlan(
    IReadOnlyList<MigratedFile> Files,
    IReadOnlyList<string> Problems,
    string BackupSuffix = ConfigMigration.BackupSuffix)
{
    /// <summary>Vero se il piano si puo' applicare.</summary>
    public bool IsApplicable => Problems.Count == 0 && Files.Count > 0;
}

/// <summary>
/// Conversione dei contratti dalla versione 1 alla 2 (ADR-015, ADR-016), e delle forme sostituite
/// dentro la versione 2: dalla S38 il numero unico di qualita' diventa un voto per abilita', e dal
/// 24 settembre 2026 si tolgono i campi rimossi da ADR-032 (<see cref="RemovedFields"/>) — i voti diventano il posto
/// nell'ordine dei modelli (<see cref="RemovedFields.RatingsToRank"/>). Nessun profilo viene rinominato: gli
/// identificatori dei profili sono dati dell'operatore, e il codice non ne conosce nessuno (24 settembre 2026).
///
/// La conversione non avviene mai alla lettura: un file di sicurezza convertito in silenzio e' una
/// comodita' che si paga. <c>pa config migrate</c> mostra che cosa cambierebbe e scrive solo su
/// richiesta esplicita, conservando l'originale.
/// </summary>
public static class ConfigMigration
{
    /// <summary>Suffisso della copia dell'originale.</summary>
    public const string BackupSuffix = ".v1.bak";

    /// <summary>Suffisso della copia dell'originale nella conversione dei voti di qualita'.</summary>
    public const string RatingsBackupSuffix = ".pre-ratings.bak";

    /// <summary>Legge la directory dati e costruisce il piano di conversione, all'istante corrente.</summary>
    public static MigrationPlan Plan(string dataDirectory) => Plan(dataDirectory, DateTimeOffset.UtcNow);

    /// <summary>
    /// Legge la directory dati e costruisce il piano di conversione. L'istante decide quali conferme ed evidenze valevano
    /// per le regole prima della fase 4 di ADR-032 (<see cref="DataZoneConversion"/>).
    /// </summary>
    public static MigrationPlan Plan(string dataDirectory, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        List<MigratedFile> files = [];
        List<string> problems = [];

        string? configPath = ConfigurationLoader.ConfigFileNames
            .Select(name => Path.Combine(dataDirectory, name))
            .FirstOrDefault(File.Exists);

        if (configPath is null)
        {
            problems.Add("No configuration file was found in the data directory.");
            return new MigrationPlan(files, problems);
        }

        if (Read(configPath, problems) is not JsonObject config)
        {
            return new MigrationPlan(files, problems);
        }

        int? version = VersionOf(config);

        if (version is ConfigurationLoader.SupportedSchemaVersion)
        {
            return PlanRatings(dataDirectory, configPath, config, now, problems);
        }

        if (version is not 1)
        {
            problems.Add("The configuration does not declare schema version 1 or 2; nothing to migrate.");
            return new MigrationPlan(files, problems);
        }

        // I profili si leggono prima della configurazione: i loro limiti finiscono li' (ADR-032, fase 2).
        List<(string Path, JsonObject Profile)> profiles = ReadProfiles(dataDirectory, config, problems);

        files.Add(MigrateConfig(configPath, dataDirectory, config, profiles));

        string registryRelative = config["registryFile"]?.GetValue<string>() ?? "registry.yaml";
        string registryPath = Path.Combine(dataDirectory, registryRelative);
        JsonObject? registry = Read(registryPath, problems);
        List<string> registryChanges = [];

        if (registry is not null)
        {
            MigrateRegistry(registry, registryChanges);
        }

        List<(string Path, JsonObject Profile, ZoneConversionProfile Conversion, PolicyZones? Legacy)> converted = [];

        foreach ((string path, JsonObject profile) in profiles)
        {
            List<string> changes = [];
            PolicyZones? legacy = MigrateProfile(profile, changes);
            converted.Add((path, profile, new ZoneConversionProfile(ProfileIdOf(path, profile), ConstraintsOf(profile), changes), legacy));
        }

        // Fase 4 di ADR-032: dopo la forma a zone, prima di togliere cio' che le zone sostituiscono.
        if (registry?["services"] is JsonArray services)
        {
            DataZoneConversion.Convert(services, [.. converted.Select(entry => entry.Conversion)], now, registryChanges, problems);
        }

        if (registry is not null)
        {
            files.Add(new MigratedFile(Relative(registryPath, dataDirectory), ContractSchema.Registry, registryChanges, Serialize(registryPath, registry)));
        }

        foreach ((string path, JsonObject profile, ZoneConversionProfile conversion, PolicyZones? legacy) in converted)
        {
            files.Add(ProfileFile(path, dataDirectory, profile, conversion, legacy));
        }

        return new MigrationPlan(files, problems);
    }

    /// <summary>
    /// Scrive i file convertiti conservando l'originale accanto. Un originale gia' salvato non
    /// viene sovrascritto: la prima copia e' quella che documenta lo stato di partenza.
    /// </summary>
    public static void Apply(string dataDirectory, MigrationPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.IsApplicable)
        {
            throw new InvalidOperationException("The migration plan cannot be applied.");
        }

        foreach (MigratedFile file in plan.Files)
        {
            string path = Path.Combine(dataDirectory, file.RelativePath);
            string backup = path + plan.BackupSuffix;

            if (!File.Exists(backup))
            {
                File.Copy(path, backup);
            }

            File.WriteAllText(path, file.Converted);
        }
    }

    private static JsonObject? Read(string path, List<string> problems)
    {
        if (!File.Exists(path))
        {
            problems.Add("The file is referenced by the configuration but does not exist: " + Path.GetFileName(path));
            return null;
        }

        List<ValidationIssue> issues = [];

        if (!RestrictedYamlReader.TryRead(Path.GetFileName(path), File.ReadAllText(path), issues, out ParsedDocument? document))
        {
            problems.Add("The file cannot be read: " + Path.GetFileName(path));
            return null;
        }

        return document!.Root as JsonObject;
    }

    /// <summary>Profili elencati dalla configurazione, con il loro percorso; un file illeggibile diventa un problema.</summary>
    private static List<(string Path, JsonObject Profile)> ReadProfiles(string dataDirectory, JsonObject config, List<string> problems)
    {
        List<(string Path, JsonObject Profile)> profiles = [];

        foreach (JsonNode? entry in config["policyFiles"]?.AsArray() ?? [])
        {
            string path = Path.Combine(dataDirectory, entry!.GetValue<string>());

            if (Read(path, problems) is JsonObject profile)
            {
                profiles.Add((path, profile));
            }
        }

        return profiles;
    }

    /// <summary>Vincoli di ciascun profilo, con il suo identificatore: cio' che serve a portare i limiti nella configurazione.</summary>
    private static List<(string ProfileId, JsonObject Constraints)> ConstraintsOf(IEnumerable<(string Path, JsonObject Profile)> profiles) =>
    [
        .. profiles
            .Where(entry => entry.Profile["constraints"] is JsonObject)
            .Select(entry => (
                entry.Profile["profileId"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(entry.Path),
                (JsonObject)entry.Profile["constraints"]!)),
    ];

    private static int? VersionOf(JsonObject document) =>
        document["schemaVersion"] is JsonValue value && value.TryGetValue(out long version) ? (int)version : null;

    // ── configurazione ────────────────────────────────────────────────────────

    private static MigratedFile MigrateConfig(
        string path,
        string dataDirectory,
        JsonObject config,
        IReadOnlyList<(string Path, JsonObject Profile)> profiles)
    {
        List<string> changes = [];

        config["schemaVersion"] = 2;
        changes.Add("schemaVersion: 1 -> 2");

        if (config["memory"] is JsonObject memory)
        {
            foreach (string gone in new[] { "embeddingModel", "embeddingModelFingerprint", "dimension" })
            {
                if (memory.Remove(gone))
                {
                    changes.Add("memory/" + gone + " removed: the embedding service declares it now");
                }
            }

            // Nessun servizio viene inventato: la memoria resta senza servizio finche' l'operatore
            // non ne dichiara uno nel registro, e la ricerca lo dichiara come lessicale.
            memory["embeddingServiceId"] = null;
            changes.Add("memory/embeddingServiceId added as null: choose an embedding service in the registry");
        }

        // Le soglie per difficolta' non esistono piu' (ADR-032, fase 3): la sezione porta il solo budget del rilevatore.
        config["routing"] = new JsonObject
        {
            ["piiLatencyBudgetMs"] = 1500,
        };

        changes.Add("routing added with the documented PII budget");

        // I limiti dei profili vivono nella configurazione (ADR-032, fase 2): il piu' stretto di tutti, senza allargare.
        RemovedFields.FoldProfileLimits(config, ConstraintsOf(profiles), changes);

        return new MigratedFile(Relative(path, dataDirectory), ContractSchema.Config, changes, Serialize(path, config));
    }

    // ── registro ──────────────────────────────────────────────────────────────

    private static void MigrateRegistry(JsonObject registry, List<string> changes)
    {
        registry["schemaVersion"] = 2;
        changes.Add("schemaVersion: 1 -> 2");

        JsonArray services = [];

        foreach (JsonNode? entry in registry["targets"]?.AsArray() ?? [])
        {
            if (entry is JsonObject target)
            {
                services.Add(MigrateTarget(target, changes));
            }
        }

        registry.Remove("targets");
        registry["services"] = services;
        changes.Add("targets -> services");

        // Il voto ricavato dal numero unico di qualita' diventa subito il posto nell'ordine (ADR-032, fase 3).
        RemovedFields.RatingsToRank(services, changes);
    }

    private static JsonObject MigrateTarget(JsonObject target, List<string> changes)
    {
        string id = target["id"]?.GetValue<string>() ?? "unknown";
        bool agent = string.Equals(target["kind"]?.GetValue<string>(), "agent", StringComparison.Ordinal);
        bool cloud = string.Equals(target["boundary"]?.GetValue<string>(), "cloud", StringComparison.Ordinal);

        JsonObject service = new()
        {
            ["id"] = id,
            ["capability"] = agent ? ServiceCapability.CodeAgent.ToWire() : ServiceCapability.Chat.ToWire(),
            ["adapter"] = target["adapter"]?.GetValue<string>(),
            ["enabled"] = target["enabled"]?.GetValue<bool>() ?? false,

            // Conta dove finiscono i token: dove gira il processo non lo legge nessun controllo (ADR-032).
            ["inferencePlacement"] = cloud ? Placement.ThirdParty.ToWire() : Placement.Loopback.ToWire(),
        };

        changes.Add(id + ": kind/boundary -> capability " + service["capability"]!.GetValue<string>()
            + ", inference " + service["inferencePlacement"]!.GetValue<string>());

        Copy(target, service, "endpoint");
        Copy(target, service, "executable");
        Copy(target, service, "model");
        Copy(target, service, "authMode");
        Copy(target, service, "capabilities");
        Copy(target, service, "controls");
        Copy(target, service, "processingRegions");
        Copy(target, service, "evidenceExpiresAt");
        Copy(target, service, "contextWindowTokens");
        Copy(target, service, "maxOutputTokens");
        Copy(target, service, "maxConcurrent");

        JsonObject calibration = [];

        AddCalibration(calibration, target, "quality", "quality");
        AddCalibration(calibration, target, "latencyScore", "latency");

        // Costo e quota scritti a mano non esistono piu' (ADR-032): il costo viene da `pricing`, la quota dalla sonda.
        foreach (string gone in new[] { "costScore", "quotaScore" })
        {
            if (target.ContainsKey(gone))
            {
                changes.Add(id + ": " + gone + " removed: cost comes from pricing and quota from the probe");
            }
        }

        if (calibration.Count > 0)
        {
            service["calibration"] = calibration;
            changes.Add(id + ": scores -> calibration marked " + CalibrationSource.Declared.ToWire());
        }

        // Un'installazione v1 arriva direttamente alla forma corrente, senza passare da quella
        // sostituita.
        ConvertQuality(service, changes);

        return service;
    }

    // ── voti per abilita' ─────────────────────────────────────────────────────

    /// <summary>
    /// Piano per un'installazione gia' alla versione 2 che usa ancora il numero unico di qualita'.
    /// Tocca il solo registro: configurazione e profili non cambiano.
    /// </summary>
    private static MigrationPlan PlanRatings(string dataDirectory, string configPath, JsonObject config, DateTimeOffset now, List<string> problems)
    {
        List<MigratedFile> files = [];
        Dictionary<string, string> renames = new(StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> capabilityOf = new Dictionary<string, string>(StringComparer.Ordinal);

        string registryRelative = config["registryFile"]?.GetValue<string>() ?? "registry.yaml";
        string registryPath = Path.Combine(dataDirectory, registryRelative);
        JsonObject? registry = Read(registryPath, problems);
        List<string> registryChanges = [];

        if (registry is not null)
        {
            foreach (JsonNode? entry in registry["services"]?.AsArray() ?? [])
            {
                if (entry is JsonObject service)
                {
                    ConvertQuality(service, registryChanges);
                    RemovedFields.RemoveFromService(service, registryChanges);
                }
            }

            // Dopo la conversione del numero unico: i voti, vecchi o appena ricavati, diventano l'ordine (ADR-032, fase 3).
            if (registry["services"] is JsonArray services)
            {
                RemovedFields.RatingsToRank(services, registryChanges);
            }

            RenameOpaqueIds(registry, renames, registryChanges);
            capabilityOf = CapabilitiesOf(registry, renames);
        }

        // Un documento **gia' in versione 2** puo' portare una forma che la versione 2 ha tolto dopo:
        // dalla S85 i permessi delle estensioni. Fino alla S89 questo percorso guardava solo il
        // registro, e una directory nata prima di quella rimozione non si caricava piu' **e non era
        // convertibile**: `pa config migrate` rispondeva «already current» su una configurazione che
        // lo schema rifiutava, mentre il commento dello schema prometteva che il comando l'avrebbe
        // azzerata. Nessuna prova copriva il caso, e il difetto e' emerso su una installazione vera.
        // Un identificatore cambiato va cambiato **ovunque** lo si nomini, nello stesso passo: un profilo che
        // ammette ancora il vecchio nome negherebbe il servizio, e una memoria che lo cerca non lo troverebbe.
        // I profili si leggono prima della configurazione: i limiti che dichiarano ancora finiscono li', al valore piu'
        // stretto (ADR-032, fase 2), e solo dopo si tolgono dai profili.
        List<(string Path, JsonObject Profile)> profiles = ReadProfiles(dataDirectory, config, problems);
        MigratedFile? configFile = null;

        {
            List<string> changes = [];
            RemovedFields.RemoveFromConfig(config, changes);
            RemovedFields.FoldProfileLimits(config, ConstraintsOf(profiles), changes);
            if (renames.Count > 0) { RewriteReferences(config, renames, inService: false, "", changes); }

            if (changes.Count > 0)
            {
                configFile = new MigratedFile(Relative(configPath, dataDirectory), ContractSchema.Config, changes, Serialize(configPath, config));
            }
        }

        List<(string Path, JsonObject Profile, ZoneConversionProfile Conversion, PolicyZones? Legacy)> converted = [];

        foreach ((string path, JsonObject profile) in profiles)
        {
            List<string> changes = [];
            PolicyZones? zones = null;

            if (profile["constraints"] is JsonObject constraints)
            {
                ClearExtensionPermissions(constraints, changes);
                RemovedFields.RemoveFromProfile(constraints, capabilityOf, changes);
                zones = ConvertToZones(constraints, changes);
            }
            if (renames.Count > 0) { RewriteReferences(profile, renames, inService: false, "", changes); }

            converted.Add((path, profile, new ZoneConversionProfile(ProfileIdOf(path, profile), ConstraintsOf(profile), changes), zones));
        }

        // Fase 4 di ADR-032: dopo la forma a zone, prima di togliere cio' che le zone sostituiscono. Idempotente: su
        // registro e profili gia' convertiti non trova nulla da fare.
        if (registry?["services"] is JsonArray all)
        {
            DataZoneConversion.Convert(all, [.. converted.Select(entry => entry.Conversion)], now, registryChanges, problems);
        }

        if (registry is not null && registryChanges.Count > 0)
        {
            files.Add(new MigratedFile(Relative(registryPath, dataDirectory), ContractSchema.Registry, registryChanges, Serialize(registryPath, registry)));
        }

        if (configFile is not null)
        {
            files.Add(configFile);
        }

        foreach ((string path, JsonObject profile, ZoneConversionProfile conversion, PolicyZones? legacy) in converted)
        {
            if (conversion.Changes.Count > 0)
            {
                files.Add(ProfileFile(path, dataDirectory, profile, conversion, legacy));
            }
        }

        // Nessun file da toccare significa che la configurazione e' davvero corrente, ed e' anche
        // cio' che rende la conversione idempotente: la seconda esecuzione non trova piu' nulla.
        if (files.Count == 0)
        {
            problems.Add("The configuration is already current; nothing to migrate.");
        }

        return new MigrationPlan(files, problems, RatingsBackupSuffix);
    }

    /// <summary>File di un profilo convertito, con le zone da mostrare: quelle della fase 4, o quelle della forma precedente.</summary>
    private static MigratedFile ProfileFile(
        string path, string dataDirectory, JsonObject profile, ZoneConversionProfile conversion, PolicyZones? legacy) =>
        new(Relative(path, dataDirectory), ContractSchema.Policy, conversion.Changes, Serialize(path, profile))
        {
            ProfileId = profile["profileId"]?.GetValue<string>(),
            ProfileName = profile["name"]?.GetValue<string>(),
            Zones = conversion.Zones ?? legacy,
        };

    private static string ProfileIdOf(string path, JsonObject profile) =>
        profile["profileId"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(path);

    /// <summary>Vincoli di un profilo; un profilo senza vincoli ne riceve di vuoti, che la conversione segnala.</summary>
    private static JsonObject ConstraintsOf(JsonObject profile)
    {
        if (profile["constraints"] is not JsonObject constraints)
        {
            constraints = [];
        }

        return constraints;
    }

    /// <summary>
    /// Gli identificatori opachi <c>model-...</c> generati prima della S117 diventano leggibili: l'istanza da
    /// cui il modello viene, poi il modello (<see cref="ServiceIds.Readable"/>). L'istanza si riconosce da un
    /// servizio con lo stesso adattatore e lo stesso indirizzo il cui identificatore non sia opaco; senza,
    /// il prefisso e' l'adattatore.
    /// </summary>
    private static void RenameOpaqueIds(JsonObject registry, Dictionary<string, string> renames, List<string> changes)
    {
        JsonObject[] services = [.. (registry["services"]?.AsArray() ?? []).OfType<JsonObject>()];
        HashSet<string> taken = new(services.Select(service => service["id"]?.GetValue<string>() ?? string.Empty), StringComparer.Ordinal);

        foreach (JsonObject service in services)
        {
            string id = service["id"]?.GetValue<string>() ?? string.Empty;
            if (!ServiceIds.IsOpaque(id)) { continue; }

            string adapter = service["adapter"]?.GetValue<string>() ?? "service";
            string? endpoint = service["endpoint"]?.GetValue<string>();
            string prefix = services
                .Where(other => other["adapter"]?.GetValue<string>() == adapter && other["endpoint"]?.GetValue<string>() == endpoint)
                .Select(other => other["id"]?.GetValue<string>() ?? string.Empty)
                .FirstOrDefault(other => other.Length > 0 && !ServiceIds.IsOpaque(other)) ?? adapter;
            string model = service["model"]?.GetValue<string>() ?? string.Empty;

            string readable = ServiceIds.Readable(prefix, model, taken);
            taken.Add(readable);
            renames[id] = readable;
            service["id"] = readable;
            changes.Add("services." + readable + ": identifier " + id + " renamed to " + readable);
        }
    }

    /// <summary>
    /// Capacita' di ciascun servizio del registro, per identificatore: quello di adesso e, per un servizio rinominato, anche
    /// quello vecchio, che i profili nominano ancora finche' i riferimenti non sono riscritti.
    /// </summary>
    private static Dictionary<string, string> CapabilitiesOf(JsonObject registry, IReadOnlyDictionary<string, string> renames)
    {
        Dictionary<string, string> capabilityOf = new(StringComparer.Ordinal);

        foreach (JsonObject service in (registry["services"]?.AsArray() ?? []).OfType<JsonObject>())
        {
            if (service["id"]?.GetValue<string>() is string id && service["capability"]?.GetValue<string>() is string capability)
            {
                capabilityOf[id] = capability;
            }
        }

        foreach ((string before, string after) in renames)
        {
            if (capabilityOf.TryGetValue(after, out string? capability))
            {
                capabilityOf[before] = capability;
            }
        }

        return capabilityOf;
    }

    /// <summary>
    /// Riscrive i riferimenti a un servizio rinominato. Un riferimento sta sotto una chiave che parla di servizi
    /// (<c>embeddingServiceId</c>, <c>allowedServiceIds</c>): cercarlo solo li'
    /// evita di toccare un testo che per caso coincida, anche se un identificatore opaco e' di fatto unico.
    /// </summary>
    private static void RewriteReferences(JsonNode? node, IReadOnlyDictionary<string, string> renames, bool inService, string path, List<string> changes)
    {
        switch (node)
        {
            case JsonObject item:
                foreach (string key in item.Select(pair => pair.Key).ToArray())
                {
                    bool service = inService || key.Contains("service", StringComparison.OrdinalIgnoreCase);
                    string child = path.Length == 0 ? key : path + "." + key;

                    if (item[key] is JsonValue value && service && value.TryGetValue(out string? text) && renames.TryGetValue(text, out string? renamed))
                    {
                        item[key] = renamed;
                        changes.Add(child + ": " + text + " renamed to " + renamed);
                    }
                    else
                    {
                        RewriteReferences(item[key], renames, service, child, changes);
                    }
                }

                break;

            case JsonArray list:
                for (int index = 0; index < list.Count; index++)
                {
                    string child = path + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";

                    if (list[index] is JsonValue value && inService && value.TryGetValue(out string? text) && renames.TryGetValue(text, out string? renamed))
                    {
                        list[index] = renamed;
                        changes.Add(child + ": " + text + " renamed to " + renamed);
                    }
                    else
                    {
                        RewriteReferences(list[index], renames, inService, child, changes);
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Il numero unico di qualita' diventa lo stesso voto per ogni abilita' per cui il servizio puo'
    /// essere scelto: 0,8 diventa 8. E' un passo intermedio: subito dopo <see cref="RemovedFields.RatingsToRank"/> ne ricava
    /// il posto nell'ordine e lo toglie. E' la lettura piu' fedele di cio' che l'operatore aveva scritto
    /// — un numero valido per tutto — e non inventa differenze che nessuno ha dichiarato.
    /// </summary>
    private static void ConvertQuality(JsonObject service, List<string> changes)
    {
        if (service["calibration"] is not JsonObject calibration || calibration["quality"] is not JsonObject quality)
        {
            return;
        }

        string id = service["id"]?.GetValue<string>() ?? "unknown";

        calibration.Remove("quality");

        if (calibration.Count == 0)
        {
            service.Remove("calibration");
        }

        IReadOnlyList<Intent> abilities =
            WireEnum.TryParse(service["capability"]?.GetValue<string>() ?? string.Empty, out ServiceCapability capability)
                ? CapabilityRouting.AbilitiesOf(capability)
                : [];

        if (quality["value"] is not JsonValue value || abilities.Count == 0)
        {
            // Una capacita' che non passa dal ranking non ha abilita' da votare: il numero non
            // orientava nessuna scelta, e non viene trasformato in un voto che nessuno leggerebbe.
            changes.Add(id + ": calibration/quality removed: this capability is not ranked");
            return;
        }

        if (service["ratings"] is JsonObject)
        {
            changes.Add(id + ": calibration/quality removed: ratings are already declared");
            return;
        }

        double score = value.TryGetValue(out long integer) ? integer : value.GetValue<double>();
        double rating = Math.Round(Math.Clamp(score, 0, 1) * 10, 2, MidpointRounding.ToEven);

        JsonObject ratings = [];

        foreach (Intent ability in abilities)
        {
            ratings[ability.ToWire()] = rating;
        }

        service["ratings"] = ratings;

        changes.Add(id + ": calibration/quality -> ratings "
            + string.Join(", ", abilities.Select(ability => ability.ToWire()))
            + " = " + rating.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AddCalibration(JsonObject calibration, JsonObject target, string from, string to)
    {
        if (target[from] is not JsonValue value)
        {
            return;
        }

        double number = value.TryGetValue(out long integer) ? integer : value.GetValue<double>();

        calibration[to] = new JsonObject
        {
            // Un punteggio scritto a mano resta dichiarato: la migrazione non lo promuove a misura.
            ["value"] = number,
            ["source"] = CalibrationSource.Declared.ToWire(),
        };
    }

    // ── profili ───────────────────────────────────────────────────────────────

    private static PolicyZones? MigrateProfile(JsonObject profile, List<string> changes)
    {
        PolicyZones? zones = null;

        profile["schemaVersion"] = 2;
        changes.Add("schemaVersion: 1 -> 2");

        if (profile["constraints"] is JsonObject constraints)
        {
            if (constraints["allowedTargetIds"] is JsonNode allowed)
            {
                constraints.Remove("allowedTargetIds");
                constraints["allowedServiceIds"] = allowed.DeepClone();
                changes.Add("allowedTargetIds -> allowedServiceIds");
            }

            if (constraints.Remove("allowedKinds"))
            {
                changes.Add("allowedKinds removed: the capability of a service says whether it is an agent");
            }

            bool external = string.Equals(
                constraints["externalProcessing"]?.GetValue<string>(), "allowed", StringComparison.Ordinal);

            // La conversione non allarga: un profilo che vietava l'elaborazione esterna resta
            // ammesso al solo loopback. Le collocazioni ammesse si tolgono subito dopo (ADR-032, fase 4), ma servono
            // alla conversione per sapere che cosa il profilo ammetteva.
            constraints["allowedInferencePlacements"] = external
                ? new JsonArray(
                    Placement.Loopback.ToWire(),
                    Placement.Lan.ToWire(),
                    Placement.PrivateRemote.ToWire(),
                    Placement.ThirdParty.ToWire())
                : new JsonArray(Placement.Loopback.ToWire());

            changes.Add("allowedInferencePlacements derived from externalProcessing: "
                + (external ? "all four" : "loopback only"));

            ClearExtensionPermissions(constraints, changes);
            RemovedFields.RemoveFromProfile(constraints, new Dictionary<string, string>(StringComparer.Ordinal), changes);
            zones = ConvertToZones(constraints, changes);
        }

        return zones;
    }

    /// <summary>
    /// Azzera i permessi delle estensioni dichiarati da un profilo.
    ///
    /// I permessi non esistono piu' (S85): un pacchetto porta solo testo e nessun percorso li
    /// consuma. Il campo resta obbligatorio nello schema e deve essere **vuoto**, quindi la
    /// conversione lo azzera invece di lasciar passare un documento che la validazione rifiuterebbe
    /// subito dopo. La modifica viene dichiarata come le altre: l'originale resta accanto e
    /// l'operatore vede che cosa e' stato tolto.
    ///
    /// Vale per **entrambi** i percorsi di conversione — dalla versione 1 e dentro la versione 2 —
    /// ed e' scritta una volta sola: una seconda copia avrebbe ripulito un profilo convertito da v1
    /// e lasciato sporco un profilo gia' v2, che e' esattamente il difetto chiuso in S89.
    /// </summary>
    private static void ClearExtensionPermissions(JsonObject constraints, List<string> changes)
    {
        if (constraints["allowedExtensionPermissions"] is JsonArray declared && declared.Count > 0)
        {
            constraints["allowedExtensionPermissions"] = new JsonArray();
            changes.Add("allowedExtensionPermissions cleared: extension permissions are no longer part of the product");
        }
    }

    /// <summary>
    /// La forma precedente alle zone diventa zone (ADR-029), con la stessa traduzione che la compilazione applica a un
    /// profilo non convertito (<see cref="PolicyZones.FromLegacy"/>): per costruzione il profilo convertito non ammette
    /// nessuna destinazione che prima negava. Conferme e uscita degli allegati restano spente: le decide l'operatore.
    ///
    /// Vale per entrambi i percorsi di conversione, come l'azzeramento dei permessi delle estensioni (S89).
    /// </summary>
    private static PolicyZones? ConvertToZones(JsonObject constraints, List<string> changes)
    {
        if (constraints["zones"] is not null
            || constraints["externalProcessing"] is not JsonValue externalValue
            || constraints["maxCloudDataClass"] is not JsonValue maxValue)
        {
            return null;
        }

        ExternalProcessing external = WireEnum.Parse<ExternalProcessing>(externalValue.GetValue<string>());
        DataClass maxCloud = WireEnum.Parse<DataClass>(maxValue.GetValue<string>());

        // Il tetto della beta vale anche qui: oltre `personal` non si usciva comunque.
        PolicyZones zones = PolicyZones.FromLegacy(external, maxCloud > DataClass.Personal ? DataClass.Personal : maxCloud);

        JsonObject maxZone = [];
        foreach (DataClass dataClass in PolicyZones.DeclaredClasses)
        {
            maxZone[dataClass.ToWire()] = zones.For(dataClass).ToWire();
        }

        constraints.Remove("externalProcessing");
        constraints.Remove("maxCloudDataClass");
        constraints["zones"] = new JsonObject
        {
            ["maxZone"] = maxZone,
            ["perimeter"] = new JsonArray([.. zones.Perimeter.Order().Select(zone => (JsonNode)JsonValue.Create(zone.ToWire())!)]),
            ["confirmOutside"] = new JsonArray(),
            ["attachmentsOutsidePerimeter"] = false,
        };

        changes.Add("externalProcessing " + external.ToWire() + " and maxCloudDataClass " + maxCloud.ToWire()
            + " -> zones.maxZone " + string.Join(", ", PolicyZones.DeclaredClasses.Select(dataClass => dataClass.ToWire() + ": " + zones.For(dataClass).ToWire()))
            + "; attachments stay inside the perimeter");

        return zones;
    }

    // ── utilita' ──────────────────────────────────────────────────────────────

    private static void Copy(JsonObject source, JsonObject destination, string name)
    {
        if (source[name] is JsonNode node)
        {
            destination[name] = node.DeepClone();
        }
    }

    private static string Serialize(string path, JsonObject document) =>
        string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)
            ? document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n"
            : RestrictedYamlWriter.Write(document);

    private static string Relative(string path, string dataDirectory) =>
        Path.GetRelativePath(dataDirectory, path).Replace('\\', '/');
}
