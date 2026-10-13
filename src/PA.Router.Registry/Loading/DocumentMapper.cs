using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Traduzione esplicita da documento validato a DTO immutabile. Nessuna deserializzazione
/// automatica: i nomi wire sono un contratto e la mappatura deve essere leggibile e verificabile.
/// </summary>
internal static class DocumentMapper
{
    internal static ConfigDocument ToConfig(JsonNode root)
    {
        JsonObject document = JsonAccess.Object(root);
        JsonObject api = JsonAccess.Object(document, "api");
        JsonObject telemetry = JsonAccess.Object(document, "telemetry");
        JsonObject retention = JsonAccess.Object(document, "retention");
        JsonObject limits = JsonAccess.Object(document, "limits");
        JsonObject memory = JsonAccess.Object(document, "memory");
        JsonObject extensions = JsonAccess.Object(document, "extensions");
        JsonObject routing = JsonAccess.Object(document, "routing");

        return new ConfigDocument(
            SchemaVersion: JsonAccess.Int(document, "schemaVersion"),
            DefaultProfileId: JsonAccess.String(document, "defaultProfileId"),
            PolicyFiles: JsonAccess.StringList(document, "policyFiles"),
            RegistryFile: JsonAccess.String(document, "registryFile"),
            ContentMode: ParseContentMode(JsonAccess.String(document, "contentMode")),
            PreferSubscription: JsonAccess.Bool(document, "preferSubscription"),
            Api: new ApiConfig(
                Host: JsonAccess.String(api, "host"),
                Port: JsonAccess.Int(api, "port"),
                ServiceProfileId: JsonAccess.String(api, "serviceProfileId"),
                ServiceTokenRef: JsonAccess.String(api, "serviceTokenRef"),
                ProjectId: JsonAccess.Guid(api, "projectId")),
            Telemetry: new TelemetryConfig(
                Exporter: JsonAccess.Enum<TelemetryExporter>(telemetry, "exporter"),
                Endpoint: JsonAccess.UriOrNull(telemetry, "endpoint")),
            Retention: new RetentionConfig(
                AuditDays: JsonAccess.Int(retention, "auditDays"),
                ContentDays: JsonAccess.Int(retention, "contentDays"),
                EventHours: JsonAccess.Int(retention, "eventHours")),
            Limits: new LimitsConfig(
                MaxConcurrency: JsonAccess.Int(limits, "maxConcurrency"),
                TimeoutSeconds: JsonAccess.Int(limits, "timeoutSeconds"),
                MaxAttempts: JsonAccess.Int(limits, "maxAttempts"))
            {
                HistoryTurns = JsonAccess.IntOrDefault(limits, "historyTurns", LimitsConfig.DefaultHistoryTurns),
                // Dal 24 settembre 2026 (ADR-032, fase 2) i limiti in byte stanno qui e non nel profilo; assenti valgono il
                // tetto della beta, che e' cio' che valeva prima per ogni profilo consegnato.
                MaxInputBytes = JsonAccess.IntOrDefault(limits, "maxInputBytes", LimitsConfig.DefaultMaxInputBytes),
                MaxContextBytes = JsonAccess.IntOrDefault(limits, "maxContextBytes", LimitsConfig.DefaultMaxContextBytes),
                MaxOutputBytes = JsonAccess.IntOrDefault(limits, "maxOutputBytes", LimitsConfig.DefaultMaxOutputBytes),
                // ADR-032, fase 5: assente vuol dire nessun tetto, cioe' cio' che valeva prima.
                MaxCostPerRequest = JsonAccess.NumberOrNull(limits, "maxCostPerRequest"),
            },
            Memory: new MemoryConfig(
                Enabled: JsonAccess.Bool(memory, "enabled"),
                Required: JsonAccess.Bool(memory, "required"),
                EmbeddingServiceId: JsonAccess.StringOrNull(memory, "embeddingServiceId"),
                TopK: JsonAccess.Int(memory, "topK"),
                MaxChunksPerProject: JsonAccess.Int(memory, "maxChunksPerProject"),
                CacheMiB: JsonAccess.Int(memory, "cacheMiB"),
                RetentionDays: JsonAccess.Int(memory, "retentionDays"),
                // Assente significa il valore predefinito: una configurazione scritta prima che
                // questo campo esistesse continua a valere, e vale quello che valeva prima.
                MaxEmbeddingDimension: JsonAccess.IntOrNull(memory, "maxEmbeddingDimension")
                    ?? MemoryLimits.DefaultMaxDimension),
            Extensions: new ExtensionsConfig(
                Enabled: JsonAccess.Bool(extensions, "enabled"),
                PluginRoot: JsonAccess.String(extensions, "pluginRoot"),
                AllowExecutableTools: JsonAccess.Bool(extensions, "allowExecutableTools")),
            Routing: new RoutingConfig(
                PiiLatencyBudgetMs: JsonAccess.IntOrDefault(routing, "piiLatencyBudgetMs", 0),
                Analyzer: ToAnalyzer(JsonAccess.ObjectOrNull(routing, "analyzer"))),
            Tools: ToTools(JsonAccess.ObjectOrNull(document, "tools")),
            Language: JsonAccess.StringOrNull(document, "language") ?? "en");
    }

    /// <summary>
    /// Analizzatore della richiesta (ADR-026); assente significa spento. Configurato e' acceso: se decide o gira in ombra
    /// lo dice la misura del modello, non un campo (ADR-032, fase 2).
    /// </summary>
    private static AnalyzerConfig? ToAnalyzer(JsonObject? analyzer) =>
        analyzer is null ? null : new AnalyzerConfig(JsonAccess.String(analyzer, "serviceId"));

    /// <summary>Strumenti delle applicazioni; assente significa spenti.</summary>
    private static ToolsConfig? ToTools(JsonObject? tools)
    {
        if (tools is null)
        {
            return null;
        }

        JsonObject limits = JsonAccess.Object(tools, "limits");

        return new ToolsConfig(
            JsonAccess.Bool(tools, "enabled"),
            new ToolLimits(
                JsonAccess.Int(limits, "maxRounds"),
                JsonAccess.Int(limits, "maxCallsPerRound"),
                JsonAccess.Int(limits, "maxCalls"),
                JsonAccess.Int(limits, "callTimeoutSeconds"),
                JsonAccess.Int(limits, "maxResultBytes")),
            tools["sources"] is JsonArray sources
                ? [.. sources.Select(item => ToToolSource(JsonAccess.Object(item!)))]
                : [],
            tools["attestations"] is JsonArray attestations
                ? [.. attestations.Select(item => ToToolAttestation(JsonAccess.Object(item!)))]
                : [],
            tools["revoked"] is JsonArray revoked
                ? [.. revoked.Select(item => JsonAccess.Object(item!)).Select(entry =>
                    new ToolRevocation(JsonAccess.String(entry, "source"), JsonAccess.String(entry, "tool")))]
                : []);
    }

    private static ToolSourceConfig ToToolSource(JsonObject source) =>
        new(
            JsonAccess.String(source, "id"),
            JsonAccess.Enum<ToolSourceKind>(source, "kind"),
            JsonAccess.UriOrNull(source, "url"),
            JsonAccess.StringOrNull(source, "command"),
            JsonAccess.StringListOrEmpty(source, "arguments"));

    private static ToolAttestationConfig ToToolAttestation(JsonObject attestation) =>
        new(
            JsonAccess.String(attestation, "source"),
            JsonAccess.String(attestation, "tool"),
            JsonAccess.String(attestation, "fingerprint"),
            JsonAccess.Enum<DataClass>(attestation, "accepts"),
            JsonAccess.Enum<DataClass>(attestation, "returns"),
            JsonAccess.RegionSetOrEmpty(attestation, "regions"),
            attestation["placement"] is null ? null : JsonAccess.Enum<Placement>(attestation, "placement"),
            JsonAccess.String(attestation, "by"),
            // Lo schema la pretende: senza scadenza un'attestazione non viene letta.
            JsonAccess.DateTimeOrNull(attestation, "expiresAt") ?? DateTimeOffset.MinValue);

    /// <summary>Restrizione dei limiti degli strumenti in un profilo; ogni campo e' facoltativo.</summary>
    private static ToolLimitsRestriction? ToToolRestriction(JsonObject? limits) =>
        limits is null
            ? null
            : new ToolLimitsRestriction(
                JsonAccess.IntOrNull(limits, "maxRounds"),
                JsonAccess.IntOrNull(limits, "maxCallsPerRound"),
                JsonAccess.IntOrNull(limits, "maxCalls"),
                JsonAccess.IntOrNull(limits, "callTimeoutSeconds"),
                JsonAccess.IntOrNull(limits, "maxResultBytes"));

    internal static RegistryDocument ToRegistry(JsonNode root)
    {
        JsonObject document = JsonAccess.Object(root);

        return new RegistryDocument(
            SchemaVersion: JsonAccess.Int(document, "schemaVersion"),
            Revision: JsonAccess.String(document, "revision"),
            Services: [.. JsonAccess.Array(document, "services").Select(item => ToService(JsonAccess.Object(item!)))]);
    }

    internal static PolicyDocument ToPolicy(JsonNode root)
    {
        JsonObject document = JsonAccess.Object(root);
        JsonObject constraints = JsonAccess.Object(document, "constraints");

        // Dal 25 settembre 2026 (ADR-032, fase 4) le zone sono l'unica regola su dove vanno i dati e lo schema le pretende;
        // l'elenco dei servizi e' facoltativo, e assente o vuoto vuol dire tutti.
        return new PolicyDocument(
            SchemaVersion: JsonAccess.Int(document, "schemaVersion"),
            Revision: JsonAccess.String(document, "revision"),
            ProfileId: JsonAccess.String(document, "profileId"),
            Constraints: new PolicyConstraints(
                MinimumDataClass: JsonAccess.Enum<DataClass>(constraints, "minimumDataClass"),
                AllowedServiceIds: JsonAccess.StringSetOrEmpty(constraints, "allowedServiceIds"),
                Zones: ToZones(JsonAccess.Object(constraints, "zones")),
                AllowedCapabilities: JsonAccess.EnumSet<Capability>(constraints, "allowedCapabilities"),
                RedactContacts: JsonAccess.Bool(constraints, "redactContacts"),
                AllowLocalMemoryProcessing: JsonAccess.Bool(constraints, "allowLocalMemoryProcessing"))
            {
                ToolLimits = ToToolRestriction(JsonAccess.ObjectOrNull(constraints, "toolLimits")),
                PersonalDataGrading = JsonAccess.EnumOrDefault(constraints, "personalDataGrading", PersonalDataGrading.Graded),
            })
        {
            Name = JsonAccess.StringOrNull(document, "name") is { Length: > 0 } name ? name : null,
        };
    }

    private static PolicyZones ToZones(JsonObject zones)
    {
        JsonObject maxZone = JsonAccess.Object(zones, "maxZone");
        Dictionary<DataClass, TrustZone> limits = [];

        foreach (DataClass dataClass in PolicyZones.DeclaredClasses)
        {
            limits[dataClass] = JsonAccess.Enum<TrustZone>(maxZone, dataClass.ToWire());
        }

        // Il perimetro e' fisso (ADR-032, fase 4): non si legge.
        return new PolicyZones(
            limits,
            JsonAccess.EnumSetOrEmpty<DataClass>(zones, "confirmOutside"),
            JsonAccess.BoolOrDefault(zones, "attachmentsOutsidePerimeter", false))
        {
            WeakPerimeter = JsonAccess.EnumOrDefault(zones, "weakPerimeter", WeakPerimeterRule.Deny),
        };
    }

    private static ServiceDescriptor ToService(JsonObject service) =>
        new(
            Id: JsonAccess.String(service, "id"),
            Capability: JsonAccess.Enum<ServiceCapability>(service, "capability"),
            Adapter: JsonAccess.Enum<AdapterKind>(service, "adapter"),
            Enabled: JsonAccess.Bool(service, "enabled"),
            InferencePlacement: JsonAccess.Enum<Placement>(service, "inferencePlacement"),
            Model: JsonAccess.StringOrNull(service, "model"),
            Dimension: JsonAccess.IntOrNull(service, "dimension"),
            Fingerprint: JsonAccess.StringOrNull(service, "fingerprint"),
            AuthMode: JsonAccess.EnumOrDefault(service, "authMode", AuthMode.None),
            SecretRef: JsonAccess.StringOrNull(service, "secretRef"),
            Capabilities: JsonAccess.EnumSetOrEmpty<Capability>(service, "capabilities"),
            Controls: JsonAccess.EnumSetOrEmpty<Control>(service, "controls"),
            ProcessingRegions: JsonAccess.RegionSetOrEmpty(service, "processingRegions"),
            Attestations: ToAttestations(JsonAccess.ObjectOrNull(service, "attestations")),
            Latency: ToCalibration(service, "latency"),
            ContextWindowTokens: JsonAccess.IntOrDefault(service, "contextWindowTokens", 0),
            MaxOutputTokens: JsonAccess.IntOrDefault(service, "maxOutputTokens", 0),
            MaxConcurrent: JsonAccess.IntOrDefault(service, "maxConcurrent", 1),
            ThroughputKibPerSecond: JsonAccess.NumberOrNull(service, "throughputKibPerSecond"),
            Endpoint: JsonAccess.UriOrNull(service, "endpoint"),
            Executable: JsonAccess.StringOrNull(service, "executable"),
            Arguments: JsonAccess.StringListOrEmpty(service, "arguments"),
            Pricing: ToPricing(service),
            ToolCalling: JsonAccess.BoolOrDefault(service, "toolCalling", false))
        {
            Name = JsonAccess.StringOrNull(service, "name"),
            PromptCaching = JsonAccess.BoolOrDefault(service, "promptCaching", false),
            Thinking = service["thinking"] is System.Text.Json.Nodes.JsonValue thinking && thinking.TryGetValue(out bool think) ? think : null,
            KeepAliveMinutes = JsonAccess.IntOrNull(service, "keepAliveMinutes"),
            NativeSession = JsonAccess.BoolOrDefault(service, "nativeSession", false),
            Rank = JsonAccess.IntOrNull(service, "rank"),
            Levels = JsonAccess.ObjectOrNull(service, "levels") is JsonObject levels
                ? new ServiceLevels(
                    JsonAccess.IntOrNull(levels, "general"),
                    JsonAccess.IntOrNull(levels, "reasoning"),
                    JsonAccess.IntOrNull(levels, "coding"),
                    JsonAccess.IntOrNull(levels, "context"))
                : null,
        };

    /// <summary>Prezzo dichiarato; lo schema pretende valuta e due prezzi insieme.</summary>
    private static ServicePricing? ToPricing(JsonObject service) =>
        JsonAccess.ObjectOrNull(service, "pricing") is JsonObject pricing
            ? new ServicePricing(
                JsonAccess.String(pricing, "currency"),
                JsonAccess.Number(pricing, "inputPerMillionTokens"),
                JsonAccess.Number(pricing, "outputPerMillionTokens"))
            : null;

    /// <summary>
    /// Attestazioni per proprieta'. La chiave e' il nome wire di un controllo: lo schema chiude
    /// l'insieme delle chiavi ammesse, qui si traduce soltanto.
    /// </summary>
    private static IReadOnlyDictionary<Control, PropertyAttestation> ToAttestations(JsonObject? attestations)
    {
        if (attestations is null)
        {
            return new Dictionary<Control, PropertyAttestation>();
        }

        Dictionary<Control, PropertyAttestation> map = [];

        foreach (KeyValuePair<string, JsonNode?> entry in attestations)
        {
            if (entry.Value is not JsonObject attestation || !WireEnum.TryParse(entry.Key, out Control control))
            {
                continue;
            }

            map[control] = new PropertyAttestation(
                By: JsonAccess.String(attestation, "by"),
                ObservedAt: JsonAccess.DateTimeOrNull(attestation, "observedAt"),
                ExpiresAt: JsonAccess.DateTimeOrNull(attestation, "expiresAt"),
                Note: JsonAccess.StringOrNull(attestation, "note"));
        }

        return map;
    }

    /// <summary>
    /// Un valore di calibrazione assente resta assente: il ranking usa il neutro e non stima
    /// nulla, cosi che la mancanza di un dato non finga di essere un dato.
    /// </summary>
    private static CalibrationValue? ToCalibration(JsonObject service, string name)
    {
        if (JsonAccess.ObjectOrNull(service, "calibration") is not JsonObject calibration
            || JsonAccess.ObjectOrNull(calibration, name) is not JsonObject entry)
        {
            return null;
        }

        return new CalibrationValue(
            Value: JsonAccess.Number(entry, "value"),
            Source: JsonAccess.Enum<CalibrationSource>(entry, "source"),
            ObservedAt: JsonAccess.DateTimeOrNull(entry, "observedAt"));
    }

    private static ContentMode ParseContentMode(string wire) => wire switch
    {
        "memory_only" => ContentMode.MemoryOnly,
        "encrypted" => ContentMode.Encrypted,
        _ => throw new InvalidOperationException("Unknown content mode."),
    };
}
