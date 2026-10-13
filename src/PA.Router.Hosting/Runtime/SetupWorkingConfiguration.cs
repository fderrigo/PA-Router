using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Hosting.Adapters;
using PA.Router.Hosting.Security;
using PA.Router.Infrastructure.Crypto;
using PA.Router.Infrastructure.Storage;
using PA.Router.Policy;
using PA.Router.Registry.Bootstrap;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Copia di lavoro del setup. Contiene soltanto contratti, mai database, chiavi o transcript.
/// Il commit pubblica una generazione completa cambiando atomicamente il solo file radice.
/// </summary>
public sealed class SetupWorkingConfiguration : IDisposable
{
    private readonly IClock clock;
    private readonly string source;
    private readonly Dictionary<string, byte[]> originals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> secrets = new(StringComparer.Ordinal);
    private readonly HashSet<string> selected = new(StringComparer.Ordinal);
    private readonly JsonObject config;
    private readonly JsonObject registry;
    private readonly List<JsonObject> profiles = [];
    private bool dirty;
    private bool apiTokenRequested;
    private bool replacementRequested;
    private JsonObject baseline = new();

    private SetupWorkingConfiguration(string source, IClock clock, bool replaceInvalid = false)
    {
        this.source = Path.GetFullPath(source);
        this.clock = clock;
        DirectoryPath = Path.Combine(Path.GetTempPath(), "pa-router-setup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        string? configName = ConfigurationLoader.ConfigFileNames.FirstOrDefault(name => File.Exists(Path.Combine(source, name)));
        if (configName is null || replaceInvalid)
        {
            if (configName is not null)
            {
                originals[configName] = File.ReadAllBytes(Path.Combine(source, configName));
                replacementRequested = true;
            }
            WorkspaceTemplate.Write(DirectoryPath, Guid.NewGuid(), persistContent: false);
            config = Read(Path.Combine(DirectoryPath, "config.yaml"));
            registry = Read(Path.Combine(DirectoryPath, config["registryFile"]!.GetValue<string>()));
            foreach (JsonNode? name in config["policyFiles"]!.AsArray())
            {
                profiles.Add(Read(Path.Combine(DirectoryPath, name!.GetValue<string>())));
            }

            File.Delete(Path.Combine(DirectoryPath, "config.yaml"));
        }
        else
        {
            config = Capture(configName);
            registry = Capture(config["registryFile"]!.GetValue<string>());
            foreach (JsonNode? name in config["policyFiles"]!.AsArray())
            {
                profiles.Add(Capture(name!.GetValue<string>()));
            }
        }

        foreach (JsonObject service in Services)
        {
            if (service["enabled"]?.GetValue<bool>() == true)
            {
                selected.Add(service["id"]!.GetValue<string>());
            }

            service["enabled"] = false;
        }

        Flush();
        baseline = Baseline();
    }

    /// <summary>Directory temporanea dei soli contratti.</summary>
    public string DirectoryPath { get; }

    /// <summary>Servizi richiesti dall'operatore; non sono ancora abilitati sul disco reale.</summary>
    public IReadOnlySet<string> Selected => selected;

    /// <summary>Names only; no decrypted values cross into the setup presentation.</summary>
    public IReadOnlyList<string> SecretReferences => [.. LocalSecretVault.List(StorePaths.For(source))
        .Concat(secrets.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>Reads installed local model bundles from the actual data directory, not the draft.</summary>
    /// <summary>
    /// La cartella in cui si cercano i modelli locali. La resa la mostra: «nessun modello rilevato» senza dire dove si
    /// e' guardato ha lasciato un operatore con il modello installato in un'altra cartella e nessun modo di capirlo.
    /// </summary>
    public string LocalModelsDirectory => StorePaths.For(source).ModelsDirectory;

    public IReadOnlyList<string> InstalledLocalModels => SetupDiscovery.LocalModels(StorePaths.For(source).ModelsDirectory);

    /// <summary>Discovery before model selection, using staged credentials where present.</summary>
    public Task<IReadOnlyList<string>?> DiscoverModelsAsync(string id, CancellationToken cancellationToken)
    {
        ServiceDescriptor service = Service(id);
        return service.Adapter is AdapterKind.OpenAiCompatible or AdapterKind.Anthropic
            ? SetupDiscovery.RemoteModelsAsync(service, clock,
                name => secrets.TryGetValue(name, out string? value) ? value
                    : LocalSecretVault.TryRead(StorePaths.For(source), PlatformKeyProtector.Create(), name), cancellationToken)
            : SetupDiscovery.ModelsAsync(service, clock, cancellationToken);
    }

    /// <summary>Observed local model metadata, fetched only by an explicit discovery action.</summary>
    public Task<IReadOnlyDictionary<string, PA.Router.Adapters.Ollama.OllamaProbe.InstalledModel>?> DiscoverModelCatalogueAsync(string id, CancellationToken cancellationToken)
        => SetupDiscovery.ModelCatalogueAsync(Service(id), clock, cancellationToken);

    /// <summary>Indica modifiche nella copia di lavoro non ancora pubblicate.</summary>
    public bool IsDirty => replacementRequested || apiTokenRequested || secrets.Count > 0 || !JsonNode.DeepEquals(baseline, Baseline());

    /// <summary>Richiede la creazione del token API nel vault esistente, solo al commit.</summary>
    public void RequestApiToken() => apiTokenRequested = true;

    /// <summary>Probe esplicita del servizio: nessuna inferenza, nessuna abilitazione persistita.</summary>
    public async Task<ProbeResult?> TestServiceAsync(string id, CancellationToken cancellationToken)
    {
        var snapshot = Health.Snapshot!;
        ServiceDescriptor service = Service(id);
        var blockers = ServiceSetupCatalog.Blockers(snapshot, service, clock);
        if (blockers.Count > 0) { throw new InvalidOperationException(string.Join("; ", blockers)); }
        StorePaths paths = StorePaths.For(source);
        using var adapters = new LocalAdapters(clock,
            name => secrets.TryGetValue(name, out string? value) ? value : LocalSecretVault.TryRead(paths, PlatformKeyProtector.Create(), name), paths.ModelsDirectory);
        var probes = await adapters.ObserveAsync([service with { Enabled = true }], cancellationToken).ConfigureAwait(false);
        return probes.GetValueOrDefault(id);
    }

    private JsonObject Baseline() => new()
    {
        ["config"] = config.DeepClone(),
        ["registry"] = registry.DeepClone(),
        ["profiles"] = new JsonArray([.. profiles.Select(profile => (JsonNode?)profile.DeepClone())]),
        ["selected"] = new JsonArray([.. selected.Order(StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id))]),
    };

    /// <summary>Profilo scelto per la configurazione personale.</summary>
    public string ProfileId => config["defaultProfileId"]!.GetValue<string>();

    /// <summary>Stato strutturale della bozza; i servizi sono spenti fino alla validazione finale.</summary>
    public ConfigurationReport Health => Inspect();

    /// <summary>Carica una bozza; le migrazioni devono essere state accettate prima.</summary>
    public static SetupWorkingConfiguration Open(string source, IClock clock) => new(source, clock);

    /// <summary>Recupero esplicito: prepara template nella bozza e conserva i file originali fino al commit con backup.</summary>
    public static SetupWorkingConfiguration Recreate(string source, IClock clock) => new(source, clock, replaceInvalid: true);

    private IEnumerable<JsonObject> Services => registry["services"]!.AsArray().OfType<JsonObject>();

    /// <summary>Servizio corrente come contratto, mai una copia di regole nella TUI.</summary>
    public ServiceDescriptor Service(string id) => Health.Snapshot!.Registry.Services.Single(service => service.Id == id);

    /// <summary>Campi non segreti del servizio per la resa.</summary>
    public string Value(string id, string field) => Find(id)[field]?.ToString() ?? string.Empty;

    /// <summary>Copia dei soli dati configurabili per un editor assistito; nessun segreto.</summary>
    public JsonObject Document(string kind, string? id = null) => (JsonObject)DocumentCore(kind, id).DeepClone();

    private JsonObject DocumentCore(string kind, string? id) => kind switch
    {
        "config" => config,
        "registry" => registry,
        "policy" => profiles.Single(profile => profile["profileId"]!.GetValue<string>() == (id ?? ProfileId)),
        "service" => Find(id ?? throw new ArgumentNullException(nameof(id))),
        _ => throw new ArgumentException("Documento sconosciuto.", nameof(kind)),
    };

    /// <summary>Conferma atomica di una scheda: il loader resta l'unico gate strutturale.</summary>
    public IReadOnlyList<ValidationIssue> ApplyDocument(string kind, string? id, JsonObject edited)
        => ApplyDocumentCore(kind, id, edited, false);

    /// <summary>Identifies changes requiring an explicit review of model or environment evidence.</summary>
    public bool IdentityChanged(string id, JsonObject edited) => ModelIdentityChanged(id, edited)
        || EnvironmentChanged(Find(id), edited);

    /// <summary>Whether model-specific facts would belong to a different target after this edit.</summary>
    public bool ModelIdentityChanged(string id, JsonObject edited) => !JsonNode.DeepEquals(Find(id)["model"], edited["model"]);

    private static bool EnvironmentChanged(JsonObject before, JsonObject after) =>
        new[] { "endpoint", "inferencePlacement", "authMode", "secretRef", "executable", "arguments" }
            .Any(field => field == "endpoint"
                ? !UriEquals(before[field]?.ToString(), after[field]?.ToString())
                : !JsonNode.DeepEquals(before[field], after[field]));

    private static bool UriEquals(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal)
        || (Uri.TryCreate(left, UriKind.Absolute, out Uri? a) && Uri.TryCreate(right, UriKind.Absolute, out Uri? b) && a == b);

    private IReadOnlyList<ValidationIssue> ApplyDocumentCore(string kind, string? id, JsonObject edited, bool modelMetadataReviewed)
    {
        JsonObject target = DocumentCore(kind, id);
        string[] managed = kind switch
        {
            "config" => ["schemaVersion", "registryFile", "policyFiles"],
            "registry" => ["schemaVersion", "services"],
            "policy" => ["schemaVersion", "profileId"],
            _ => ["id", "adapter", "enabled"],
        };
        if (managed.Any(key => !JsonNode.DeepEquals(target[key], edited[key])))
        {
            throw new ArgumentException("La scheda modifica un campo gestito dal router.", nameof(edited));
        }
        JsonObject before = (JsonObject)target.DeepClone();
        edited = (JsonObject)edited.DeepClone();
        if (kind == "service")
        {
            bool modelChanged = ModelIdentityChanged(id!, edited);
            if (modelChanged && !modelMetadataReviewed)
            {
                if (Service(id!).Capability == ServiceCapability.Embedding && JsonNode.DeepEquals(before["dimension"], edited["dimension"]))
                {
                    return [new ValidationIssue("registry", "/services/dimension", null, ValidationCodes.InconsistentDeclaration,
                        "The new embedding model requires its own verified vector dimension.")];
                }
                foreach (string field in ModelMetadata)
                {
                    if (JsonNode.DeepEquals(before[field], edited[field])) { edited.Remove(field); }
                }
            }
            if (modelChanged || EnvironmentChanged(before, edited)) { InvalidateEvidence(edited); }
        }
        Copy(edited, target);
        Flush();
        ConfigurationReport report = Health;
        if (kind == "config" && report.Snapshot is { } candidate)
        {
            IReadOnlyList<ValidationIssue> declarationIssues = NormalizeToolDeclarations(before, target, candidate);
            if (declarationIssues.Count > 0) { Copy(before, target); Flush(); return declarationIssues; }
            Flush();
            report = Health;
        }
        if (report.Snapshot is null) { Copy(before, target); Flush(); }
        else { dirty |= !JsonNode.DeepEquals(before, target); }
        return report.Issues;
    }

    private static void Copy(JsonObject from, JsonObject to)
    {
        to.Clear();
        foreach ((string key, JsonNode? value) in from) { to[key] = value?.DeepClone(); }
    }

    /// <summary>Valore di una impostazione operativa gia' prevista dallo schema config.</summary>
    public string ConfigValue(string section, string field) => config[section] is JsonObject node && node[field] is JsonNode value
        ? value.ToString()
        : string.Empty;

    /// <summary>Modifica un campo operativo esistente; non crea proprieta' fuori contratto.</summary>
    public IReadOnlyList<ValidationIssue> EditConfig(string section, string field, JsonNode value)
    {
        if (config[section] is not JsonObject node || node[field] is null)
        {
            throw new ArgumentException("Impostazione non prevista dal contratto.", nameof(field));
        }

        JsonNode previous = node[field]!.DeepClone();
        bool wasDirty = dirty;
        node[field] = value.DeepClone();
        dirty = true;
        Flush();
        ConfigurationReport report = Health;
        if (report.Snapshot is null)
        {
            node[field] = previous;
            dirty = wasDirty;
            Flush();
        }

        return report.Issues;
    }

    /// <summary>
    /// Sceglie il servizio che analizza le richieste (ADR-026, ADR-030 §3). Il modo non si scrive piu' (ADR-032, fase 2):
    /// configurato vuol dire acceso, e decide se il modello e' misurato. A differenza degli altri campi
    /// operativi, <c>routing.analyzer</c> in una configurazione nuova **non esiste**: qui nasce, con la stessa rilettura
    /// che annulla la modifica se il risultato non si carica. Con <c>serviceId</c> null la voce viene tolta.
    /// </summary>
    public IReadOnlyList<ValidationIssue> SetAnalyzer(string? serviceId)
    {
        JsonObject routing = config["routing"] as JsonObject ?? throw new ArgumentException("Impostazione non prevista dal contratto.", nameof(serviceId));
        JsonNode? previous = routing["analyzer"]?.DeepClone();
        bool wasDirty = dirty;

        if (serviceId is null)
        {
            routing.Remove("analyzer");
        }
        else
        {
            routing["analyzer"] = new JsonObject { ["serviceId"] = serviceId };
        }

        dirty = true;
        Flush();
        ConfigurationReport report = Health;

        if (report.Snapshot is null)
        {
            if (previous is null) { routing.Remove("analyzer"); } else { routing["analyzer"] = previous; }
            dirty = wasDirty;
            Flush();
        }

        return report.Issues;
    }

    /// <summary>Modifica un valore annidato gia' previsto dallo schema, con lo stesso gate inline.</summary>
    public IReadOnlyList<ValidationIssue> EditConfigPath(string path, JsonNode value)
    {
        string[] parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) { throw new ArgumentException("Percorso operativo non valido.", nameof(path)); }
        JsonNode? current = config;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            current = current?[parts[index]];
            if (current is not JsonObject) { throw new ArgumentException("Impostazione non prevista dal contratto.", nameof(path)); }
        }

        JsonObject parent = (JsonObject)current!;
        string field = parts[^1];
        if (parent[field] is null) { throw new ArgumentException("Impostazione non prevista dal contratto.", nameof(path)); }
        JsonNode previous = parent[field]!.DeepClone();
        bool wasDirty = dirty;
        parent[field] = value.DeepClone();
        dirty = true;
        Flush();
        ConfigurationReport report = Health;
        if (report.Snapshot is null)
        {
            parent[field] = previous;
            dirty = wasDirty;
            Flush();
        }
        return report.Issues;
    }

    /// <summary>Legge un valore operativo annidato gia' presente nello schema.</summary>
    public string ConfigPathValue(string path)
    {
        JsonNode? current = config;
        foreach (string part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            current = current?[part];
            if (current is null) { return string.Empty; }
        }
        return current.ToString();
    }

    /// <summary>Modifica validata inline. Un errore lascia la bozza precedente intatta.</summary>
    public IReadOnlyList<ValidationIssue> EditService(string id, string field, JsonNode? value)
        => EditServiceFields(id, new Dictionary<string, JsonNode?> { [field] = value });

    /// <summary>Modifica insieme campi dipendenti, per esempio collocazione e indirizzo.</summary>
    public IReadOnlyList<ValidationIssue> EditServiceFields(string id, IReadOnlyDictionary<string, JsonNode?> changes)
    {
        if (changes.Keys.Any(field => !ServiceSetupCatalog.EditableFields.Contains(field)))
        {
            throw new ArgumentException("Campo non modificabile dal setup.", nameof(changes));
        }

        JsonObject service = Document("service", id);
        foreach ((string field, JsonNode? value) in changes) { service[field] = value?.DeepClone(); }
        return ApplyDocument("service", id, service);
    }

    /// <summary>
    /// La copia di lavoro resta **sempre** leggibile: ogni schermata del setup legge lo snapshot, e uno snapshot assente
    /// faceva cadere la Home con un <see cref="NullReferenceException"/>. Una modifica il cui risultato non si carica
    /// viene annullata per intero — documenti, scelte e stato «modificato» — e diventa un errore dell'operatore.
    /// </summary>
    private void Guarded(Action change) => Guarded(() => { change(); return true; });

    /// <summary>Come sopra; <paramref name="change"/> dice se ha cambiato qualcosa.</summary>
    private bool Guarded(Func<bool> change)
    {
        JsonObject state = Baseline();
        bool wasDirty = dirty;
        try
        {
            if (!change()) { return false; }
            dirty = true;
            Flush();
            ConfigurationReport report = Health;
            if (report.Snapshot is not null) { return true; }
            throw new ArgumentException(report.Issues.FirstOrDefault()?.Message ?? "La modifica rende la configurazione illeggibile.");
        }
        catch
        {
            Copy(state["config"]!.AsObject(), config);
            Copy(state["registry"]!.AsObject(), registry);
            JsonArray saved = state["profiles"]!.AsArray();
            profiles.RemoveRange(saved.Count, profiles.Count - saved.Count);
            for (int index = 0; index < saved.Count; index++) { Copy(saved[index]!.AsObject(), profiles[index]); }
            selected.Clear();
            selected.UnionWith(state["selected"]!.AsArray().Select(value => value!.GetValue<string>()));
            dirty = wasDirty;
            Flush();
            throw;
        }
    }

    /// <summary>Aggiunge un'altra istanza di un adapter, senza ereditare autorizzazioni o attestazioni.</summary>
    public string Duplicate(string id, string newId)
    {
        if (string.IsNullOrWhiteSpace(newId) || Services.Any(service => service["id"]!.GetValue<string>() == newId))
        {
            throw new ArgumentException("Identificatore vuoto o gia' presente.", nameof(newId));
        }

        JsonObject copy = (JsonObject)Find(id).DeepClone();
        copy["id"] = newId;
        copy["enabled"] = false;
        copy["attestations"] = new JsonObject();
        InvalidateEvidence(copy);
        registry["services"]!.AsArray().Add(copy);
        dirty = true;
        Flush();
        if (Health.Snapshot is null)
        {
            registry["services"]!.AsArray().Remove(copy);
            Flush();
            throw new ArgumentException("Identificatore non valido.", nameof(newId));
        }

        return newId;
    }

    /// <summary>Aggiunge un servizio partendo da un descriptor gia' previsto dal registro.</summary>
    public string AddServiceFromTemplate(AdapterKind adapter, string newId)
    {
        string template = Services.FirstOrDefault(service =>
            service["adapter"]?.GetValue<string>() == adapter.ToWire())?["id"]?.GetValue<string>()
            ?? throw new ArgumentException("Nessun descriptor per questo adapter nel registro.", nameof(adapter));
        return Duplicate(template, newId);
    }

    /// <summary>Richiede l'abilitazione al commit, oppure la disabilitazione.</summary>
    public void Select(string id, bool enabled)
    {
        _ = Find(id);
        dirty |= enabled ? selected.Add(id) : selected.Remove(id);
    }

    /// <summary>Conferma modelli della stessa istanza senza duplicare target gia' presenti.</summary>
    public IReadOnlyList<string> ConfigureModels(string id, IReadOnlyList<string> models, IReadOnlyDictionary<string, int>? embeddingDimensions = null,
        IReadOnlyDictionary<string, int>? outputLimits = null)
    {
        ServiceDescriptor sourceService = Service(id);
        ServiceDescriptor[] instance = [.. Health.Snapshot!.Registry.Services.Where(service =>
            ServiceSetupCatalog.InstanceKey(service) == ServiceSetupCatalog.InstanceKey(sourceService))];
        JsonObject before = (JsonObject)registry.DeepClone();
        HashSet<string> previousSelection = new(selected, StringComparer.Ordinal);
        bool previousDirty = dirty;
        List<string> ids = [];
        try
        {
            foreach (string model in models.Distinct(StringComparer.Ordinal))
            {
                ServiceDescriptor? existing = instance.FirstOrDefault(service => service.Model == model);
                string target = existing?.Id ?? instance.FirstOrDefault(service =>
                    service.Model?.Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal) == true
                    && !ids.Contains(service.Id, StringComparer.Ordinal))?.Id
                    ?? Duplicate(id, NewModelId(instance, sourceService, model));
                var issues = ChangeModel(target, model, embeddingDimensions?.GetValueOrDefault(model), outputLimits?.GetValueOrDefault(model));
                if (issues.Count > 0) { throw new ArgumentException("Modello non valido: " + issues[0].Message); }
                Select(target, true);
                ids.Add(target);
            }
            foreach (ServiceDescriptor service in instance.Where(service => !ids.Contains(service.Id, StringComparer.Ordinal))) { Select(service.Id, false); }
            return ids;
        }
        catch
        {
            Copy(before, registry);
            selected.Clear();
            selected.UnionWith(previousSelection);
            dirty = previousDirty;
            Flush();
            throw;
        }
    }

    /// <summary>
    /// Identificatore di un modello aggiunto all'istanza: l'istanza, poi il modello (<see cref="ServiceIds.Readable"/>).
    /// Fino alla S117 era <c>model-</c> e un GUID, e finiva davanti all'operatore in ogni elenco che non passasse dal
    /// nome. Il prefisso e' il primo identificatore leggibile dell'istanza; se sono tutti opachi, l'adattatore.
    /// </summary>
    private string NewModelId(IReadOnlyList<ServiceDescriptor> instance, ServiceDescriptor source, string model)
    {
        string prefix = instance.Select(service => service.Id).FirstOrDefault(value => !ServiceIds.IsOpaque(value))
            ?? source.Adapter.ToWire();
        HashSet<string> taken = new(Services.Select(service => service["id"]!.GetValue<string>()), StringComparer.Ordinal);

        return ServiceIds.Readable(prefix, model, taken);
    }

    /// <summary>Changes model identity without carrying measurements or declarations across models.</summary>
    public IReadOnlyList<ValidationIssue> ChangeModel(string id, string model, int? embeddingDimension = null, int? maxOutputTokens = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        JsonObject edited = Document("service", id);
        if (edited["model"]?.ToString() == model) { return []; }
        if (Service(id).Capability == ServiceCapability.Embedding && embeddingDimension is not > 0)
        {
            throw new ArgumentException("The new embedding model requires its own verified vector dimension.", nameof(embeddingDimension));
        }
        if (Service(id).Adapter == AdapterKind.Anthropic && maxOutputTokens is not > 0)
        {
            throw new ArgumentException("An Anthropic service must declare its output ceiling in tokens.", nameof(maxOutputTokens));
        }
        edited["model"] = model;
        ResetModelMetadata(edited);
        if (embeddingDimension is > 0) { edited["dimension"] = embeddingDimension.Value; }
        if (maxOutputTokens is > 0) { edited["maxOutputTokens"] = maxOutputTokens.Value; }
        InvalidateEvidence(edited);
        return ApplyDocumentCore("service", id, edited, true);
    }

    // Il posto nell'ordine e le correzioni dei livelli descrivono il modello, come facevano i voti fino al 24 settembre 2026
    // (ADR-032, fase 3): un modello diverso non li eredita.
    private static readonly string[] ModelMetadata = ["rank", "levels", "calibration", "contextWindowTokens", "maxOutputTokens", "dimension", "fingerprint", "pricing", "throughputKibPerSecond", "toolCalling"];

    private static void ResetModelMetadata(JsonObject service)
    {
        foreach (string field in ModelMetadata)
        {
            service.Remove(field);
        }
    }

    /// <summary>
    /// Un indirizzo o una collocazione diversi sono un'altra destinazione: le conferme date per quella di prima non la
    /// coprono e si tolgono (ADR-032, fase 4: le conferme non scadono, quindi l'unico modo che smettano di valere e'
    /// toglierle).
    /// </summary>
    private static void InvalidateEvidence(JsonObject service) => service["attestations"] = new JsonObject();

    /// <summary>Preview only: which selected targets need either existing profile allowlist updated.</summary>
    public IReadOnlyList<string> AdmissionRequired(IEnumerable<string> ids)
    {
        JsonObject constraints = Document("policy")["constraints"]!.AsObject();

        // Un elenco assente o vuoto ammette ogni servizio (ADR-032, fase 4): nessuno da aggiungere.
        if (constraints["allowedServiceIds"] is not JsonArray { Count: > 0 } allowed) { return []; }
        return [.. ids.Distinct(StringComparer.Ordinal).Where(id => !allowed.Any(value => value!.ToString() == id))];
    }

    /// <summary>Explicit profile admission, updating the allowlist without changing any other constraint.</summary>
    public IReadOnlyList<ValidationIssue> AdmitServices(IEnumerable<string> ids)
    {
        JsonObject policy = Document("policy");
        JsonObject constraints = policy["constraints"]!.AsObject();
        // Un elenco assente o vuoto ammette gia' ogni servizio (ADR-032, fase 4): aggiungerne uno lo restringerebbe.
        if (constraints["allowedServiceIds"] is JsonArray { Count: > 0 } allowed)
        {
            foreach (string id in ids.Distinct(StringComparer.Ordinal))
            {
                if (!allowed.Any(item => item!.ToString() == id)) { allowed.Add(id); }
            }
        }
        return ApplyDocument("policy", ProfileId, policy);
    }

    /// <summary>Modifica il collegamento condiviso senza separare involontariamente i modelli dell'istanza.</summary>
    public IReadOnlyList<ValidationIssue> EditInstanceEndpoint(string id, string endpoint, Placement placement)
    {
        ServiceDescriptor service = Service(id);
        string[] ids = [.. Health.Snapshot!.Registry.Services.Where(item => ServiceSetupCatalog.InstanceKey(item) == ServiceSetupCatalog.InstanceKey(service)).Select(item => item.Id)];
        JsonObject before = (JsonObject)registry.DeepClone();
        foreach (string target in ids)
        {
            JsonObject edited = Document("service", target);
            if (UriEquals(Service(target).Endpoint?.ToString(), endpoint)
                && Service(target).InferencePlacement == placement) { continue; }
            edited["endpoint"] = endpoint;
            edited["inferencePlacement"] = placement.ToWire();
            InvalidateEvidence(edited);
            var issues = ApplyDocument("service", target, edited);
            if (issues.Count > 0) { Copy(before, registry); Flush(); return issues; }
        }
        return [];
    }

    /// <summary>Cambia profilo senza dedurre la policy dal caso d'uso.</summary>
    public void ChooseProfile(string id)
    {
        if (!profiles.Any(profile => profile["profileId"]!.GetValue<string>() == id))
        {
            throw new ArgumentException("Profilo non presente.", nameof(id));
        }

        Guarded(() => config["defaultProfileId"] = id);
    }

    /// <summary>Copia un profilo esistente; baseline e loader continuano ad applicare i vincoli.</summary>
    public IReadOnlyList<ValidationIssue> DuplicateProfile(string newId)
    {
        if (profiles.Any(profile => profile["profileId"]!.ToString() == newId)) { throw new ArgumentException("Questo nome di profilo esiste gia.", nameof(newId)); }
        JsonObject copy = Document("policy");
        copy["profileId"] = newId;
        // Il nome e' della modalita' copiata: due modalita' con lo stesso nome non si distinguerebbero. La copia mostra il
        // proprio identificatore finche' l'operatore non le da' un nome.
        copy.Remove("name");
        profiles.Add(copy);
        Flush();
        var report = Health;
        if (report.Snapshot is null) { profiles.Remove(copy); Flush(); }
        return report.Issues;
    }

    /// <summary>
    /// Da' un nome alla modalita' scelta (<c>name</c>, 24 settembre 2026), come il nome di un servizio: e' cio' che si
    /// legge al posto dell'identificatore. Una stringa vuota toglie il nome e fa tornare l'identificatore. Non tocca
    /// l'identificatore, a cui sono legate sessioni e configurazione. Un nome gia' usato da un'altra modalita' e' rifiutato:
    /// due modalita' con lo stesso nome non si distinguerebbero. Se il risultato non si carica, il profilo torna com'era.
    /// </summary>
    public IReadOnlyList<ValidationIssue> NameProfile(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        string trimmed = name.Trim();
        JsonObject profile = profiles.Single(item => item["profileId"]!.GetValue<string>() == ProfileId);

        if (trimmed.Length > 0 && profiles.Any(item => !ReferenceEquals(item, profile)
            && string.Equals(item["name"]?.GetValue<string>()?.Trim(), trimmed, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new ArgumentException("Another mode already has this name.", nameof(name));
        }

        bool had = profile.ContainsKey("name");
        JsonNode? before = profile["name"]?.DeepClone();
        bool wasDirty = dirty;

        if (trimmed.Length == 0) { profile.Remove("name"); }
        else if (had) { profile["name"] = trimmed; }
        else { profile.Insert(profile.IndexOf("profileId") + 1, "name", trimmed); }

        dirty = true;
        Flush();
        ConfigurationReport report = Health;

        if (report.Snapshot is null)
        {
            profile.Remove("name");
            if (had) { profile.Insert(profile.IndexOf("profileId") + 1, "name", before); }
            dirty = wasDirty;
            Flush();
            return report.Issues;
        }

        return [];
    }

    /// <summary>Modifica un vincolo del profilo scelto; schema e baseline restano i gate.</summary>
    public IReadOnlyList<ValidationIssue> EditPolicy(string field, JsonNode value)
        => EditPolicyFields(new Dictionary<string, JsonNode?> { [field] = value });

    /// <summary>
    /// Modifica insieme piu' vincoli del profilo scelto; un valore null toglie il campo. Serve alle forme che si
    /// escludono a vicenda — le zone e i due campi che sostituiscono (ADR-029): scritte una alla volta, il passaggio
    /// intermedio e' un documento che la lettura rifiuta. Se il risultato non si carica, il profilo torna com'era:
    /// un campo che prima non c'era viene tolto, non lasciato a null — un null resta nel documento e lo rende
    /// illeggibile per sempre.
    /// </summary>
    public IReadOnlyList<ValidationIssue> EditPolicyFields(IReadOnlyDictionary<string, JsonNode?> changes)
    {
        JsonObject constraints = profiles.Single(profile => profile["profileId"]!.GetValue<string>() == ProfileId)["constraints"]!.AsObject();
        JsonObject before = (JsonObject)constraints.DeepClone();
        bool wasDirty = dirty;
        foreach ((string field, JsonNode? value) in changes)
        {
            if (value is null) { constraints.Remove(field); }
            else { constraints[field] = value.DeepClone(); }
        }
        dirty = true;
        Flush();
        ConfigurationReport report = Health;
        if (report.Snapshot is null)
        {
            Copy(before, constraints);
            dirty = wasDirty;
            Flush();
        }

        return report.Issues;
    }

    /// <summary>Conservazione esplicitamente scelta dall'operatore.</summary>
    public void SetStorage(bool encrypted) => Guarded(() => config["contentMode"] = encrypted ? "encrypted" : "memory_only");

    /// <summary>Preferenza esistente, applicata dal ranking soltanto agli ammessi.</summary>
    public void PreferSubscription(bool prefer) => Guarded(() => config["preferSubscription"] = prefer);

    /// <summary>Il valore del segreto resta in memoria fino al commit, sotto un nome nuovo.</summary>
    public void SetSecret(string id, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string name = "setup-" + Guid.NewGuid().ToString("N");
        Guarded(() =>
        {
            Find(id)["secretRef"] = name;
            InvalidateEvidence(Find(id));
        });
        secrets[name] = value;
    }

    /// <summary>Changes the account for exactly the models sharing this connection, not other accounts.</summary>
    public IReadOnlyList<ValidationIssue> EditInstanceSecretReference(string id, string reference)
    {
        string key = ServiceSetupCatalog.InstanceKey(Service(id));
        string[] ids = [.. Health.Snapshot!.Registry.Services.Where(item => ServiceSetupCatalog.InstanceKey(item) == key).Select(item => item.Id)];
        JsonObject before = (JsonObject)registry.DeepClone();
        foreach (string target in ids)
        {
            var issues = EditService(target, "secretRef", JsonValue.Create(reference));
            if (issues.Count > 0) { Copy(before, registry); Flush(); return issues; }
        }
        return [];
    }

    /// <summary>
    /// Dà un nome all'istanza: vale per tutti i modelli dello stesso endpoint, perche' e' l'istanza che
    /// l'operatore nomina, non ciascun modello. Una stringa vuota toglie il nome e fa tornare quello
    /// predefinito.
    ///
    /// Non tocca l'identificatore: a quello sono legate conferme, permessi e allowlist, e rinominarlo
    /// li scollegherebbe in silenzio. Il nome invece si cambia quando si vuole (S117).
    /// </summary>
    public IReadOnlyList<ValidationIssue> NameInstance(string id, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        string key = ServiceSetupCatalog.InstanceKey(Service(id));
        string[] ids = [.. Health.Snapshot!.Registry.Services.Where(item => ServiceSetupCatalog.InstanceKey(item) == key).Select(item => item.Id)];
        JsonObject before = (JsonObject)registry.DeepClone();
        string trimmed = name.Trim();

        foreach (string target in ids)
        {
            var issues = EditService(target, "name", trimmed.Length == 0 ? null : JsonValue.Create(trimmed));
            if (issues.Count > 0) { Copy(before, registry); Flush(); return issues; }
        }

        return [];
    }

    /// <summary>Stages one credential and its shared reference; no vault write before the global commit.</summary>
    public void SetInstanceSecret(string id, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string name = "setup-" + Guid.NewGuid().ToString("N");
        var issues = EditInstanceSecretReference(id, name);
        if (issues.Count > 0) { throw new ArgumentException(issues[0].Message, nameof(value)); }
        secrets[name] = value;
    }

    /// <summary>Applies an explicitly enumerated declaration to models of one connection only.</summary>
    public void AttestServices(IReadOnlyList<string> ids, Control control, PropertyAttestation attestation)
    {
        ValidateScope(ids);
        ServiceAttestations.Validate(attestation, clock);
        Guarded(() => { foreach (string id in ids) { ServiceAttestations.Apply(registry, id, control, attestation); } });
    }

    /// <summary>Toglie una conferma gia' data, sullo stesso ambito con cui era stata data.</summary>
    public bool RevokeServices(IReadOnlyList<string> ids, Control control)
    {
        ValidateScope(ids);
        return Guarded(() =>
        {
            bool removed = false;
            foreach (string id in ids) { removed |= ServiceAttestations.Revoke(registry, id, control); }
            return removed;
        });
    }

    private void ValidateScope(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) { throw new ArgumentException("An explicit model scope is required.", nameof(ids)); }
        if (ids.Select(id => ServiceSetupCatalog.InstanceKey(Service(id))).Distinct(StringComparer.Ordinal).Count() != 1)
        {
            throw new ArgumentException("A shared declaration must refer to one connection and account.", nameof(ids));
        }
    }

    /// <summary>Condivide la validazione delle attestazioni con la CLI avanzata.</summary>
    public void Attest(string id, Control control, PropertyAttestation attestation)
    {
        ServiceAttestations.Validate(attestation, clock);
        Guarded(() => ServiceAttestations.Apply(registry, id, control, attestation));
    }

    /// <summary>Valida la configurazione finale con le abilitazioni richieste.</summary>
    public ConfigurationReport Validate(IReadOnlyDictionary<string, ProbeResult>? probes = null)
    {
        foreach (JsonObject service in Services) { service["enabled"] = selected.Contains(service["id"]!.GetValue<string>()); }
        try { Flush(); return ConfigurationHealth.Inspect(DirectoryPath, clock, probes); }
        finally
        {
            foreach (JsonObject service in Services) { service["enabled"] = false; }
            Flush();
        }
    }

    /// <summary>Existing loader and readiness gates, evaluated per requested enablement. No I/O to providers.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> EnablementIssues()
    {
        Dictionary<string, IReadOnlyList<string>> result = new(StringComparer.Ordinal);
        ConfigurationSnapshot? snapshot = Health.Snapshot;
        if (snapshot is null) { return result; }
        foreach (ServiceDescriptor service in snapshot.Registry.Services.Where(service => selected.Contains(service.Id)))
        {
            List<string> reasons = [.. ServiceSetupCatalog.Blockers(snapshot, service, clock)];
            if (MissingCredential(service)) { reasons.Add("AUTH_UNAVAILABLE"); }
            JsonObject item = Find(service.Id);
            item["enabled"] = true;
            try
            {
                Flush();
                reasons.AddRange(Health.Issues.Select(issue => issue.Message));
            }
            finally { item["enabled"] = false; Flush(); }
            if (reasons.Count > 0) { result[service.Id] = [.. reasons.Distinct(StringComparer.Ordinal)]; }
        }
        return result;
    }

    private bool MissingCredential(ServiceDescriptor service) => service.AuthMode == AuthMode.ApiKey && (service.SecretRef is null
        || (!secrets.ContainsKey(service.SecretRef) && !LocalSecretVault.List(StorePaths.For(source)).Contains(service.SecretRef, StringComparer.Ordinal)));

    /// <summary>Pubblica contratti validati; file precedenti e backup restano disponibili per rollback.</summary>
    public ConfigurationReport Commit()
    {
        ConfigurationReport final = Validate();
        if (final.Snapshot is null)
        {
            return final;
        }

        // Persisting operator preferences does not require a runnable service. An incomplete
        // configuration remains incomplete; all requested enablements still pass the gates below.
        // Prontezza di ciascun selezionato, non solo presenza di uno snapshot.
        foreach (ServiceDescriptor service in final.Snapshot.Registry.Services.Where(service => service.Enabled))
        {
            IReadOnlyList<string> blockers = ServiceSetupCatalog.Blockers(final.Snapshot, service, clock);
            if (blockers.Count > 0)
            {
                throw new InvalidOperationException(service.Id + ": " + string.Join(", ", blockers));
            }
            if (MissingCredential(service))
            {
                throw new InvalidOperationException(service.Id + ": credenziale non configurata.");
            }
        }

        StorePaths paths = StorePaths.For(source).EnsureCreated();
        // Configurazione e store hanno proprietari distinti: il REPL possiede gia' lo store.
        // Pubblicare contratti non apre SQLite. Solo una prima installazione deve migrarlo.
        using FileStream configurationLock = new(Path.Combine(source, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach ((string relative, byte[] bytes) in originals)
        {
            string path = Path.Combine(source, relative);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                throw new InvalidOperationException("Configurazione modificata durante il setup: riaprire /setup.");
            }
        }

        if (originals.Count == 0 && ConfigurationLoader.ConfigFileNames.Any(name => File.Exists(Path.Combine(source, name))))
        {
            throw new InvalidOperationException("Una configurazione e' stata creata durante il setup.");
        }

        if (apiTokenRequested && !ServiceTokenVault.TryCreate(paths, PlatformKeyProtector.Create(),
            final.Snapshot.Config.Api.ServiceTokenRef, out _))
        {
            throw new InvalidOperationException("Protezione del token API non disponibile: nessun token in chiaro viene scritto.");
        }

        if (secrets.Count > 0)
        {
            var protector = PlatformKeyProtector.Create();
            if (!protector.IsAvailable) { throw new InvalidOperationException("Protezione delle credenziali non disponibile."); }
            foreach ((string name, string value) in secrets)
            {
                if (!LocalSecretVault.TryWrite(paths, protector, name, value))
                {
                    throw new InvalidOperationException("Credenziale non salvata.");
                }
            }
        }

        if (!File.Exists(paths.DatabaseFile))
        {
            using StoreLock ownership = StoreLock.Acquire(paths, clock);
            new MigrationRunner(new SqliteConnectionFactory(paths), clock).Migrate();
        }
        string generation = "setup-" + Guid.NewGuid().ToString("N");
        string generationPath = Path.Combine(source, generation);
        Directory.CreateDirectory(generationPath);
        JsonObject publishedRegistry = (JsonObject)registry.DeepClone();
        foreach (JsonObject service in publishedRegistry["services"]!.AsArray().OfType<JsonObject>())
        { service["enabled"] = selected.Contains(service["id"]!.GetValue<string>()); }
        File.WriteAllText(Path.Combine(generationPath, "registry.json"), publishedRegistry.ToJsonString());
        for (int i = 0; i < profiles.Count; i++)
        {
            File.WriteAllText(Path.Combine(generationPath, ProfileFile(i)), profiles[i].ToJsonString());
        }

        JsonObject published = (JsonObject)config.DeepClone();
        published["registryFile"] = generation + "/registry.json";
        published["policyFiles"] = new JsonArray([.. Enumerable.Range(0, profiles.Count)
            .Select(i => (JsonNode?)JsonValue.Create(generation + "/" + ProfileFile(i)))]);
        string rootName = originals.Keys.FirstOrDefault(ConfigurationLoader.ConfigFileNames.Contains) ?? "config.json";
        string rootPath = Path.Combine(source, rootName);
        string temporary = rootPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, rootName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            ? RestrictedYamlWriter.Write(published) : published.ToJsonString());
        if (File.Exists(rootPath))
        {
            File.Replace(temporary, rootPath, rootPath + ConfigurationSetup.BackupSuffix + "-" + Guid.NewGuid().ToString("N"));
        }
        else { File.Move(temporary, rootPath); }

        ConfigurationReport persisted;
        try
        {
            persisted = ConfigurationHealth.Inspect(source, clock);
            if (persisted.Snapshot is null)
            {
                throw new InvalidOperationException("La configurazione scritta non supera la rilettura: "
                    + string.Join("; ", persisted.Issues.Select(issue => issue.Code)));
            }
            // Confronto dei documenti interi: include policy, strumenti, attestazioni e campi opzionali.
            if (!JsonNode.DeepEquals(published, Read(rootPath))
                || !JsonNode.DeepEquals(publishedRegistry, Read(Path.Combine(source, persisted.Snapshot.Config.RegistryFile)))
                || profiles.Where((profile, index) => !JsonNode.DeepEquals(profile,
                    Read(Path.Combine(source, persisted.Snapshot.Config.PolicyFiles[index])))).Any())
            {
                throw new InvalidOperationException("I valori riletti non coincidono con le modifiche confermate.");
            }
            CompareExpected(final.Snapshot, persisted.Snapshot);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
        {
            if (originals.TryGetValue(rootName, out byte[]? previous))
            {
                File.WriteAllBytes(temporary, previous);
                File.Move(temporary, rootPath, overwrite: true);
            }
            else { File.Delete(rootPath); }
            throw new InvalidOperationException("Configurazione non salvata. La configurazione precedente e' stata ripristinata. " + exception.Message, exception);
        }
        secrets.Clear();
        apiTokenRequested = false;
        replacementRequested = false;
        originals.Clear();
        _ = Capture(rootName);
        _ = Capture(persisted.Snapshot.Config.RegistryFile);
        foreach (string file in persisted.Snapshot.Config.PolicyFiles) { _ = Capture(file); }
        foreach (JsonObject service in Services) { service["enabled"] = false; }
        Flush();
        dirty = false;
        baseline = Baseline();
        PruneGenerations(generation, rootName);
        return persisted;
    }

    /// <summary>Generazioni e copie della configurazione conservate per tornare indietro, oltre a quella in uso.</summary>
    public const int KeptGenerations = 5;

    /// <summary>
    /// Ogni salvataggio scrive una generazione nuova e conserva la precedente: senza un tetto la cartella dati ne
    /// accumulava una per salvataggio, per sempre. Restano quella in uso e le ultime <see cref="KeptGenerations"/>;
    /// i file scritti dall'operatore prima del setup non hanno questo nome e non vengono toccati. Una pulizia che non
    /// riesce non rende fallito un salvataggio gia' verificato.
    /// </summary>
    private void PruneGenerations(string current, string rootName)
    {
        try
        {
            foreach (DirectoryInfo old in new DirectoryInfo(source).GetDirectories("setup-*")
                .Where(item => item.Name != current && item.Name.Length == "setup-".Length + 32)
                .OrderByDescending(item => item.CreationTimeUtc).Skip(KeptGenerations))
            {
                old.Delete(recursive: true);
            }

            foreach (FileInfo old in new DirectoryInfo(source).GetFiles(rootName + ConfigurationSetup.BackupSuffix + "-*")
                .OrderByDescending(item => item.LastWriteTimeUtc).Skip(KeptGenerations))
            {
                old.Delete();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void CompareExpected(ConfigurationSnapshot expected, ConfigurationSnapshot actual)
    {
        if (expected.Config.DefaultProfileId != actual.Config.DefaultProfileId
            || expected.Config.ContentMode != actual.Config.ContentMode
            || expected.Config.Limits != actual.Config.Limits)
        {
            throw new InvalidOperationException("La configurazione riletta non coincide con la working copy (profilo, conservazione o limiti).");
        }

        foreach (ServiceDescriptor expectedService in expected.Registry.Services)
        {
            ServiceDescriptor? actualService = actual.Registry.Services.FirstOrDefault(service => service.Id == expectedService.Id);
            if (actualService is null || actualService.Enabled != expectedService.Enabled
                || actualService.Endpoint != expectedService.Endpoint
                || actualService.Model != expectedService.Model
                || actualService.Executable != expectedService.Executable
                || !actualService.ProcessingRegions.SetEquals(expectedService.ProcessingRegions))
            {
                throw new InvalidOperationException("La configurazione riletta non coincide per il servizio " + expectedService.Id + ".");
            }
        }
    }

    private JsonObject Find(string id) => Services.Single(service => service["id"]!.GetValue<string>() == id);

    private JsonObject Capture(string relative)
    {
        List<ValidationIssue> issues = [];
        if (!PathRules.TryResolveRelative(source, "setup", "", null, relative, issues, out string path))
        {
            throw new InvalidOperationException("Percorso di configurazione non valido.");
        }

        originals.Add(relative, File.ReadAllBytes(path));
        return Read(path);
    }

    private static JsonObject Read(string path)
    {
        List<ValidationIssue> issues = [];
        if (!RestrictedYamlReader.TryRead(Path.GetFileName(path), File.ReadAllText(path), issues, out ParsedDocument? document)
            || document?.Root is not JsonObject root)
        {
            throw new InvalidOperationException("Documento non leggibile; correggere o ripristinare il backup.");
        }

        return root;
    }

    private static string ProfileFile(int index) => "profile-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json";

    private void Flush()
    {
        config["registryFile"] = "registry.json";
        config["policyFiles"] = new JsonArray([.. Enumerable.Range(0, profiles.Count).Select(i => (JsonNode?)JsonValue.Create(ProfileFile(i)))]);
        string[] documents = [config.ToJsonString(), registry.ToJsonString(), .. profiles.Select(profile => profile.ToJsonString())];
        string content = string.Join('\u0000', documents);

        // Contenuto uguale, file uguali: niente da riscrivere, e la rilettura precedente vale ancora.
        if (string.Equals(content, flushed, StringComparison.Ordinal)) { return; }

        File.WriteAllText(Path.Combine(DirectoryPath, "config.json"), documents[0]);
        File.WriteAllText(Path.Combine(DirectoryPath, "registry.json"), documents[1]);
        for (int i = 0; i < profiles.Count; i++) { File.WriteAllText(Path.Combine(DirectoryPath, ProfileFile(i)), documents[i + 2]); }
        flushed = content;
    }

    /// <summary>Contenuto dell'ultima scrittura della copia di lavoro.</summary>
    private string? flushed;

    /// <summary>Ultima rilettura, con il contenuto e l'istante a cui si riferisce.</summary>
    private (string Content, DateTimeOffset At, ConfigurationReport Report)? inspected;

    /// <summary>
    /// Quanto vale una rilettura a contenuto invariato. Alcune verifiche dipendono dall'ora — una conferma che scade — e non
    /// devono restare vecchie a lungo; per il resto, rileggere ogni volta costava decine di letture e validazioni complete
    /// per disegnare una sola schermata.
    /// </summary>
    private static readonly TimeSpan InspectionLifetime = TimeSpan.FromSeconds(30);

    private ConfigurationReport Inspect()
    {
        DateTimeOffset now = clock.UtcNow;

        if (inspected is { } last && string.Equals(last.Content, flushed, StringComparison.Ordinal) && now - last.At < InspectionLifetime)
        {
            return last.Report;
        }

        ConfigurationReport report = ConfigurationHealth.Inspect(DirectoryPath, clock);
        inspected = (flushed ?? string.Empty, now, report);
        return report;
    }

    private IReadOnlyList<ValidationIssue> NormalizeToolDeclarations(JsonObject before, JsonObject edited, ConfigurationSnapshot candidate)
    {
        if (candidate.Config.Tools is not { } tools) { return []; }
        JsonArray previous = before["tools"]?["attestations"] as JsonArray ?? [];
        JsonArray current = edited["tools"]?["attestations"] as JsonArray ?? [];
        try
        {
            foreach (ToolAttestationConfig attestation in tools.Attestations)
            {
                bool Matches(JsonNode? entry) => entry?["source"]?.ToString() == attestation.Source && entry?["tool"]?.ToString() == attestation.Tool;
                JsonNode? oldEntry = previous.FirstOrDefault(Matches);
                JsonNode? newEntry = current.FirstOrDefault(Matches);
                if (JsonNode.DeepEquals(oldEntry, newEntry)) { continue; }
                ToolAttestationConfig validated = ToolAdministration.PlanAttestation(candidate, attestation.Source, attestation.Tool,
                    attestation.Fingerprint, attestation.Accepts.ToWire(), attestation.Returns.ToWire(),
                    [.. attestation.Regions.Select(region => region.Code)], attestation.ExpiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    attestation.Placement?.ToWire(), attestation.By, clock.UtcNow);
                ToolAdministration.ApplyAttestation(edited, validated);
            }
            return [];
        }
        catch (PaRouterException exception)
        {
            return [new ValidationIssue("config", "/tools/attestations", null, ValidationCodes.InconsistentDeclaration, exception.Message)];
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        secrets.Clear();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
