using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Controlli che uno schema JSON non puo' esprimere: riferimenti incrociati fra config, registro
/// e profili, coerenza fra campi dichiarati e regole della beta (06-contracts/README.md,
/// «Controlli semantici oltre schema»).
///
/// Nessuno di questi controlli autorizza qualcosa: rifiutano una configurazione incoerente prima
/// che diventi una decisione. La valutazione dei candidati resta in Policy.
/// </summary>
public static class SemanticValidator
{
    /// <summary>Nome convenzionale del file di configurazione dell'operatore.</summary>
    public const string ConfigFileName = "config.yaml";

    /// <summary>Verifica i riferimenti incrociati e le regole della beta.</summary>
    public static void Validate(
        ConfigDocument config,
        RegistryDocument registry,
        IReadOnlyList<PolicyDocument> profiles,
        string registrySource,
        IReadOnlyDictionary<string, string> profileSources,
        IClock clock,
        List<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(profileSources);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(issues);

        Dictionary<string, PolicyDocument> byProfileId = new(StringComparer.Ordinal);

        ValidateProfileUniqueness(profiles, profileSources, byProfileId, issues);
        ValidateServiceUniqueness(registry, registrySource, issues);
        ValidateServices(registry, registrySource, issues);
        ValidateCurrencies(registry, registrySource, issues);
        ValidateConfigReferences(config, registry, registrySource, byProfileId, issues);
        ValidateProfiles(byProfileId, registry, profileSources, issues);
        ValidateTelemetry(config, issues);
    }

    private static void ValidateProfileUniqueness(
        IReadOnlyList<PolicyDocument> profiles,
        IReadOnlyDictionary<string, string> profileSources,
        Dictionary<string, PolicyDocument> byProfileId,
        List<ValidationIssue> issues)
    {
        foreach (PolicyDocument profile in profiles)
        {
            string source = profileSources[profile.ProfileId];

            if (!byProfileId.TryAdd(profile.ProfileId, profile))
            {
                issues.Add(new ValidationIssue(
                    source, "/profileId", null, ValidationCodes.DuplicateId,
                    "Two policy files declare the same profile identifier."));
            }
        }
    }

    private static void ValidateServiceUniqueness(
        RegistryDocument registry,
        string source,
        List<ValidationIssue> issues)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int index = 0; index < registry.Services.Count; index++)
        {
            if (!seen.Add(registry.Services[index].Id))
            {
                issues.Add(new ValidationIssue(
                    source, ServicePointer(index) + "/id", null, ValidationCodes.DuplicateId,
                    "Two services declare the same identifier."));
            }
        }
    }

    private static void ValidateServices(
        RegistryDocument registry,
        string source,
        List<ValidationIssue> issues)
    {
        for (int index = 0; index < registry.Services.Count; index++)
        {
            ServiceDescriptor service = registry.Services[index];
            string pointer = ServicePointer(index);

            if (service.ContextWindowTokens > 0 && service.MaxOutputTokens >= service.ContextWindowTokens)
            {
                issues.Add(new ValidationIssue(
                    source, pointer + "/maxOutputTokens", null, ValidationCodes.InconsistentDeclaration,
                    "The declared output budget must stay below the context window."));
            }

            if (service.ToolCalling
                && (service.Capability is not ServiceCapability.Chat
                    || service.Adapter is not (AdapterKind.OpenAiCompatible or AdapterKind.Anthropic)))
            {
                // Per un modello locale la capacita' si osserva, e un agente non riceve strumenti delle
                // applicazioni: una dichiarazione li' non verrebbe mai letta, e sembrerebbe concedere.
                issues.Add(new ValidationIssue(
                    source, pointer + "/toolCalling", null, ValidationCodes.InconsistentDeclaration,
                    "Tool calling is declared only for remote chat services; a local model's support is observed."));
            }

            if (service.NativeSession && service.Adapter is not AdapterKind.ClaudeCodeCli)
            {
                // Solo dove l'identificatore lo assegna il router ed e' stato osservato funzionare (evidence/S116).
                issues.Add(new ValidationIssue(
                    source, pointer + "/nativeSession", null, ValidationCodes.InconsistentDeclaration,
                    "A native session is declared only for the claude-code-cli adapter."));
            }

            if (service.NativeSession && service.Controls.Contains(Control.NoPersistentTranscript))
            {
                // Le due cose si escludono: con una sessione propria il transcript resta sul disco, in chiaro. Senza il
                // controllo dichiarato, un profilo che lo pretende nega il servizio da solo — che e' il risultato giusto.
                issues.Add(new ValidationIssue(
                    source, pointer + "/nativeSession", null, ValidationCodes.InconsistentDeclaration,
                    "A service that keeps a native session cannot declare no_persistent_transcript."));
            }

            if ((service.Rank is not null || service.Levels is not null)
                && service.Capability is not (ServiceCapability.Chat or ServiceCapability.CodeAgent))
            {
                // Ordine e livelli servono a scegliere chi risponde (ADR-027): su un servizio che lavora per il router
                // non verrebbero mai letti, e un valore che non decide nulla e' un errore, non un'impostazione.
                issues.Add(new ValidationIssue(
                    source, pointer + "/rank", null, ValidationCodes.InconsistentDeclaration,
                    "Only services that answer requests take a rank or capability levels."));
            }

            if (service.PromptCaching && (service.Capability is not ServiceCapability.Chat || service.Adapter is not AdapterKind.Anthropic))
            {
                // Dichiarato altrove non verrebbe mai letto, e sembrerebbe un risparmio che non c'e'.
                issues.Add(new ValidationIssue(
                    source, pointer + "/promptCaching", null, ValidationCodes.InconsistentDeclaration,
                    "Prompt caching is declared only for an Anthropic chat service; OpenAI-compatible services cache the prefix on their own."));
            }

            if (service.Thinking is not null && (service.Capability is not ServiceCapability.Chat || service.Adapter is not AdapterKind.Ollama))
            {
                // Solo l'adapter ollama lo manda al modello: altrove sembrerebbe un'impostazione che non fa niente.
                issues.Add(new ValidationIssue(
                    source, pointer + "/thinking", null, ValidationCodes.InconsistentDeclaration,
                    "Thinking is declared only for an Ollama chat service."));
            }

            if (service.KeepAliveMinutes is not null && service.Adapter is not (AdapterKind.Ollama or AdapterKind.OllamaEmbed))
            {
                // Solo Ollama tiene un modello in memoria fra una richiesta e l'altra su richiesta del chiamante.
                issues.Add(new ValidationIssue(
                    source, pointer + "/keepAliveMinutes", null, ValidationCodes.InconsistentDeclaration,
                    "The time a model stays loaded is declared only for an Ollama service."));
            }

            foreach (ProcessingRegion region in service.ProcessingRegions)
            {
                if (!Regions.IsWellFormed(region))
                {
                    issues.Add(new ValidationIssue(
                        source, pointer + "/processingRegions", null, ValidationCodes.InconsistentDeclaration,
                        RegionMessage(region)));
                }
            }

            if (service.Capability is ServiceCapability.Embedding && service.Dimension is null)
            {
                issues.Add(new ValidationIssue(
                    source, pointer + "/dimension", null, ValidationCodes.InconsistentDeclaration,
                    "An embedding service must declare the dimension of its vectors."));
            }

            ValidateTransport(service, source, pointer, issues);

            // Un argomento che rimuove approvazioni, sandbox o fiducia negli hook viene rifiutato
            // alla **lettura** della configurazione, anche su un servizio disabilitato: un file
            // sbagliato deve fallire quando lo si legge, non quando ha gia' avviato un processo
            // (03-specs/NESTED-AGENTS.md).
            if (AgentArgumentPolicy.FirstForbidden(service.Adapter, service.Arguments) is string forbidden)
            {
                issues.Add(new ValidationIssue(
                    source, pointer + "/arguments", null, ValidationCodes.NotReadyForUse,
                    "The service declares an argument that removes a control: " + forbidden + "."));
            }

            if (!service.Enabled)
            {
                // Un servizio disabilitato puo' contenere segnaposto: e' il default degli esempi.
                continue;
            }

            if (service.HasPlaceholders)
            {
                issues.Add(new ValidationIssue(
                    source, pointer + "/enabled", null, ValidationCodes.NotReadyForUse,
                    "The service is enabled but still carries example placeholders."));
            }

            if (service.AuthMode is AuthMode.ApiKey && service.SecretRef is null)
            {
                // La chiave vive nel vault e la configurazione ne porta il nome: un servizio a
                // consumo fatturato senza nome del segreto non ha modo di autenticarsi.
                issues.Add(new ValidationIssue(
                    source, pointer + "/secretRef", null, ValidationCodes.NotReadyForUse,
                    "A service authenticated with an api key must name its secret."));
            }

            if (service.Capability is ServiceCapability.Embedding && service.Fingerprint is null)
            {
                issues.Add(new ValidationIssue(
                    source, pointer + "/fingerprint", null, ValidationCodes.NotReadyForUse,
                    "An enabled embedding service must declare the observed fingerprint."));
            }

            // Dal 25 settembre 2026 (ADR-032, fase 4) una conferma non scade e non ne pretende una data: supera la regola di
            // S30 «senza scadenza l'attestazione viene rifiutata». Resta che non si conferma cio' che il servizio non
            // dichiara.
            foreach (Control control in service.Attestations.Keys)
            {
                if (!service.Controls.Contains(control))
                {
                    issues.Add(new ValidationIssue(
                        source, pointer + "/attestations/" + control.ToWire(), null,
                        ValidationCodes.InconsistentDeclaration,
                        "The service attests a control it does not declare."));
                }
            }
        }
    }

    /// <summary>
    /// I prezzi si confrontano solo nella stessa valuta. Convertire richiederebbe un cambio che il
    /// router non ha e che cambia ogni giorno: due valute nello stesso registro sono un errore, non
    /// una conversione fatta a occhio.
    /// </summary>
    private static void ValidateCurrencies(RegistryDocument registry, string source, List<ValidationIssue> issues)
    {
        string? first = null;

        for (int index = 0; index < registry.Services.Count; index++)
        {
            if (registry.Services[index].Pricing is not ServicePricing pricing)
            {
                continue;
            }

            first ??= pricing.Currency;

            if (!string.Equals(first, pricing.Currency, StringComparison.Ordinal))
            {
                issues.Add(new ValidationIssue(
                    source, ServicePointer(index) + "/pricing/currency", null, ValidationCodes.InconsistentDeclaration,
                    "All prices in the registry must use the same currency."));
            }
        }
    }

    /// <summary>
    /// Un adapter HTTP richiede un endpoint, un programma richiede un eseguibile. L'endpoint viene
    /// poi verificato contro la regola della collocazione dichiarata.
    /// </summary>
    private static void ValidateTransport(
        ServiceDescriptor service,
        string source,
        string pointer,
        List<ValidationIssue> issues)
    {
        switch (service.Adapter)
        {
            case AdapterKind.Ollama or AdapterKind.OllamaEmbed or AdapterKind.OpenAiCompatible:
                ValidateEndpoint(service, source, pointer, issues);
                break;

            case AdapterKind.Anthropic:
                ValidateEndpoint(service, source, pointer, issues);

                if (service.MaxOutputTokens <= 0)
                {
                    // Quell'API pretende il tetto di uscita nella richiesta. Senza, l'unico modo di
                    // partire sarebbe dedurlo dai byte con un rapporto inventato: meglio rifiutare
                    // la configurazione adesso che scoprirlo a meta' di un turno.
                    issues.Add(new ValidationIssue(
                        source, pointer + "/maxOutputTokens", null, ValidationCodes.InconsistentDeclaration,
                        "An Anthropic service must declare its output ceiling in tokens."));
                }

                break;

            case AdapterKind.CodexCli or AdapterKind.ClaudeCodeCli or AdapterKind.OpenCodeCli:
                ValidateExecutable(service, source, pointer, issues);
                break;

            case AdapterKind.OnnxLocal or AdapterKind.Deterministic:
                if (service.InferencePlacement is not Placement.Loopback)
                {
                    issues.Add(new ValidationIssue(
                        source, pointer + "/inferencePlacement", null, ValidationCodes.InconsistentDeclaration,
                        "An in-process adapter can only declare loopback inference."));
                }

                break;

            default:
                break;
        }
    }

    private static void ValidateEndpoint(
        ServiceDescriptor service,
        string source,
        string pointer,
        List<ValidationIssue> issues)
    {
        if (service.Endpoint is null)
        {
            issues.Add(new ValidationIssue(
                source, pointer + "/endpoint", null, ValidationCodes.InvalidEndpoint,
                "A service reached over HTTP must declare its endpoint."));
            return;
        }

        // Una regola per collocazione: quella loopback non e' stata allentata, le altre tre sono
        // altrettanto strette su cio' che la collocazione promette (ADR-016).
        if (PlacementRules.Violation(service.InferencePlacement, service.Endpoint) is string reason)
        {
            issues.Add(new ValidationIssue(
                source, pointer + "/endpoint", null, ValidationCodes.InvalidEndpoint,
                Describe(reason)));
        }
    }

    private static string Describe(string reason) => reason switch
    {
        "endpoint_missing" => "The endpoint is missing or is not an absolute address.",
        "endpoint_has_credentials" => "The endpoint must not carry credentials.",
        "endpoint_not_origin" => "The endpoint must be an origin, without a path or a query.",
        "endpoint_not_loopback" => "A service declared loopback must use a numeric loopback endpoint.",
        "endpoint_not_numeric" => "A service declared lan must use a numeric address, not a name.",
        "endpoint_not_private" => "A service declared lan must use a private address.",
        "endpoint_not_tls" => "A service that leaves the machine must use https.",
        _ => "The endpoint does not satisfy the rule of its declared placement.",
    };

    private static void ValidateExecutable(
        ServiceDescriptor service,
        string source,
        string pointer,
        List<ValidationIssue> issues)
    {
        if (service.Executable is null)
        {
            issues.Add(new ValidationIssue(
                source, pointer + "/executable", null, ValidationCodes.InvalidPath,
                "An agent CLI service must declare the executable to launch."));
            return;
        }

        if (!PathRules.IsAcceptableExecutablePath(service.Executable))
        {
            issues.Add(new ValidationIssue(
                source, pointer + "/executable", null, ValidationCodes.InvalidPath,
                "The executable must be an absolute local path without traversal."));
        }
    }

    /// <summary>
    /// Rifiuta un servizio di embedding i cui vettori superano il limite dichiarato da questa
    /// installazione.
    ///
    /// Il limite non e' una proprieta' del prodotto ma della macchina: quanto costa tenere in
    /// memoria e su disco un vettore lungo dipende da dove gira, e la macchina di chi sviluppa non
    /// decide quali modelli un'altra macchina puo' permettersi. Chi ha piu' risorse alza
    /// <c>memory.maxEmbeddingDimension</c> e usa modelli piu' capaci.
    ///
    /// Il rifiuto arriva alla lettura della configurazione, non al primo embedding: un servizio che
    /// questa installazione non puo' servire va detto subito, non dopo mezzo turno.
    /// </summary>
    private static void ValidateEmbeddingDimensions(
        ConfigDocument config,
        RegistryDocument registry,
        string registrySource,
        List<ValidationIssue> issues)
    {
        for (int index = 0; index < registry.Services.Count; index++)
        {
            ServiceDescriptor service = registry.Services[index];

            if (service.Capability is not ServiceCapability.Embedding
                || service.Dimension is not int dimension
                || dimension <= config.Memory.MaxEmbeddingDimension)
            {
                continue;
            }

            issues.Add(new ValidationIssue(
                registrySource,
                "/services/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/dimension",
                null,
                ValidationCodes.InconsistentDeclaration,
                "The declared dimension exceeds memory.maxEmbeddingDimension of this installation ("
                + config.Memory.MaxEmbeddingDimension.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "). Raise that limit if this machine can afford longer vectors."));
        }
    }

    /// <summary>
    /// L'analizzatore legge il testo dell'operatore **prima** della privacy, come il classificatore: il suo servizio
    /// deve avere la capacita' giusta, parlare Ollama e girare su loopback (ADR-026, ADR-029: la catena che decide sta
    /// sulla macchina dell'utente).
    /// </summary>
    private static void ValidateAnalyzer(ConfigDocument config, RegistryDocument registry, List<ValidationIssue> issues)
    {
        if (config.Routing.Analyzer is not AnalyzerConfig analyzer)
        {
            return;
        }

        ServiceDescriptor? service = registry.Services.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, analyzer.ServiceId, StringComparison.Ordinal));

        if (service is null)
        {
            issues.Add(new ValidationIssue(
                ConfigFileName, "/routing/analyzer/serviceId", null, ValidationCodes.UnknownReference,
                "The request analyzer service is not declared by the registry."));

            return;
        }

        if (service.Capability is not ServiceCapability.RequestAnalysis || service.Adapter is not AdapterKind.Ollama)
        {
            issues.Add(new ValidationIssue(
                ConfigFileName, "/routing/analyzer/serviceId", null, ValidationCodes.InconsistentDeclaration,
                "The request analyzer needs a request-analysis service served by Ollama."));
        }

        if (service.InferencePlacement is not Placement.Loopback)
        {
            issues.Add(new ValidationIssue(
                ConfigFileName, "/routing/analyzer/serviceId", null, ValidationCodes.InconsistentDeclaration,
                "The request analyzer reads the operator's own text before privacy: its service must run on loopback."));
        }
    }

    /// <summary>
    /// Sorgenti e attestazioni degli strumenti (ADR-020). Una sorgente HTTP rispetta le regole di rete delle
    /// sorgenti; una stdio dichiara un programma con percorso assoluto; un'attestazione nomina una sorgente
    /// configurata o un indirizzo ammesso, con luoghi scrivibili.
    /// </summary>
    private static void ValidateTools(ConfigDocument config, List<ValidationIssue> issues)
    {
        if (config.Tools is not ToolsConfig tools)
        {
            return;
        }

        HashSet<string> ids = new(StringComparer.Ordinal);

        for (int index = 0; index < tools.Sources.Count; index++)
        {
            ToolSourceConfig source = tools.Sources[index];
            string pointer = "/tools/sources/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (!ids.Add(source.Id))
            {
                issues.Add(new ValidationIssue(
                    ConfigFileName, pointer + "/id", null, ValidationCodes.InconsistentDeclaration,
                    "Two tool sources share the same identifier."));
            }

            if (source.Kind is ToolSourceKind.McpStdio)
            {
                if (source.Url is not null || source.Command is null || !Path.IsPathFullyQualified(source.Command))
                {
                    issues.Add(new ValidationIssue(
                        ConfigFileName, pointer + "/command", null, ValidationCodes.InconsistentDeclaration,
                        "A stdio tool source declares a program by absolute path and no url."));
                }
            }
            else if (source.Command is not null || source.Arguments.Count > 0 || ToolSourceRules.Violation(source.Url) is not null)
            {
                issues.Add(new ValidationIssue(
                    ConfigFileName, pointer + "/url", null, ValidationCodes.InconsistentDeclaration,
                    "An HTTP tool source declares an https or numeric loopback url, without credentials, query or fragment, and no program."));
            }
        }

        bool KnownSource(string reference) =>
            ids.Contains(reference)
            || (Uri.TryCreate(reference, UriKind.Absolute, out Uri? url) && ToolSourceRules.Violation(url) is null);

        for (int index = 0; index < tools.Attestations.Count; index++)
        {
            ToolAttestationConfig attestation = tools.Attestations[index];
            string pointer = "/tools/attestations/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (!KnownSource(attestation.Source))
            {
                issues.Add(new ValidationIssue(
                    ConfigFileName, pointer + "/source", null, ValidationCodes.UnknownReference,
                    "The attestation names a source that is neither configured nor an admissible source url."));
            }

            if (attestation.Regions.Any(region => !Regions.IsWellFormed(region)))
            {
                issues.Add(new ValidationIssue(
                    ConfigFileName, pointer + "/regions", null, ValidationCodes.InconsistentDeclaration,
                    "A processing region must be a two-letter country code or a known group."));
            }
        }

        for (int index = 0; index < tools.Revocations.Count; index++)
        {
            if (!KnownSource(tools.Revocations[index].Source))
            {
                issues.Add(new ValidationIssue(
                    ConfigFileName,
                    "/tools/revoked/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/source",
                    null,
                    ValidationCodes.UnknownReference,
                    "The revocation names a source that is neither configured nor an admissible source url."));
            }
        }
    }

    private static void ValidateConfigReferences(
        ConfigDocument config,
        RegistryDocument registry,
        string registrySource,
        IReadOnlyDictionary<string, PolicyDocument> byProfileId,
        List<ValidationIssue> issues)
    {
        ValidateEmbeddingService(config, registry, issues);
        ValidateEmbeddingDimensions(config, registry, registrySource, issues);
        ValidateAnalyzer(config, registry, issues);
        ValidateTools(config, issues);

        if (!byProfileId.ContainsKey(config.DefaultProfileId))
        {
            issues.Add(new ValidationIssue(
                ConfigFileName, "/defaultProfileId", null, ValidationCodes.UnknownReference,
                "The default profile is not defined by any policy file."));
        }

        if (!byProfileId.TryGetValue(config.Api.ServiceProfileId, out PolicyDocument? serviceProfile))
        {
            issues.Add(new ValidationIssue(
                ConfigFileName, "/api/serviceProfileId", null, ValidationCodes.UnknownReference,
                "The service profile is not defined by any policy file."));

            return;
        }

        // Il profilo di servizio e' imposto dal backend: il browser non lo sceglie e nella beta
        // non puo' raggiungere agenti o credenziali di subscription (INV-09). Dal 24 settembre 2026 (ADR-032, fase 2) il
        // profilo non dichiara piu' un canale: il canale chatbot lo da' l'host API, e questi due controlli valgono per il
        // profilo che l'host usa davvero, qualunque sia. Il turno nega comunque capacita' e recinto sul canale di servizio
        // (INV-23).
        if (AllowsAgentService(serviceProfile, registry))
        {
            issues.Add(new ValidationIssue(
                ConfigFileName, "/api/serviceProfileId", null, ValidationCodes.InconsistentDeclaration,
                "In the beta the service profile may not allow a code agent service."));
        }

        // Dal 25 settembre 2026 (ADR-032, fase 4) le autenticazioni ammesse non sono piu' un campo del profilo: la regola
        // «solo none» diventa la stessa cosa detta sui servizi, cioe' il profilo delle applicazioni non ammette un
        // servizio in abbonamento (le credenziali di INV-09). Un servizio con chiave API lo decide la zona, e la spesa il
        // tetto per richiesta. Con l'elenco vuoto il profilo ammette ogni servizio del registro, e il controllo vale per
        // tutti.
        if (registry.Services.Any(service =>
                service.AuthMode is AuthMode.Subscription && serviceProfile.Constraints.AllowsService(service.Id)))
        {
            issues.Add(new ValidationIssue(
                ConfigFileName, "/api/serviceProfileId", null, ValidationCodes.InconsistentDeclaration,
                "In the beta the service profile may not allow a service signed in with a subscription."));
        }
    }

    private static void ValidateEmbeddingService(
        ConfigDocument config,
        RegistryDocument registry,
        List<ValidationIssue> issues)
    {
        if (config.Memory.EmbeddingServiceId is not string serviceId)
        {
            // Nessun servizio scelto: la ricerca degrada in lessicale dichiarato.
            return;
        }

        ServiceDescriptor? service = registry.Services
            .FirstOrDefault(candidate => string.Equals(candidate.Id, serviceId, StringComparison.Ordinal));

        if (service is null)
        {
            issues.Add(new ValidationIssue(
                ConfigFileName, "/memory/embeddingServiceId", null, ValidationCodes.UnknownReference,
                "The memory refers to a service the registry does not declare."));
            return;
        }

        if (service.Capability is not ServiceCapability.Embedding)
        {
            issues.Add(new ValidationIssue(
                ConfigFileName, "/memory/embeddingServiceId", null, ValidationCodes.InconsistentDeclaration,
                "The memory must refer to a service that offers the embedding capability."));
        }

        if (service.InferencePlacement is not Placement.Loopback)
        {
            // La memoria locale embedda ricordi di ogni classe, personali compresi, ed e' l'unica (ADR-032): nessun
            // ricordo esce dalla macchina.
            issues.Add(new ValidationIssue(
                ConfigFileName, "/memory/embeddingServiceId", null, ValidationCodes.InconsistentDeclaration,
                "The local memory embeds memories of every class: its embedding service must run on loopback."));
        }

    }

    /// <summary>
    /// Messaggio di un termine non riconosciuto. Nomina i gruppi ammessi, perche' la causa piu'
    /// probabile e' un valore della versione precedente: <c>other</c> non esiste piu', proprio
    /// perche' metteva insieme giurisdizioni che non si somigliano.
    /// </summary>
    private static string RegionMessage(ProcessingRegion region) =>
        "The processing region '" + region.Code + "' is neither a two-letter country code nor one of "
        + string.Join(", ", Regions.GroupNames) + ".";

    private static void ValidateProfiles(
        IReadOnlyDictionary<string, PolicyDocument> byProfileId,
        RegistryDocument registry,
        IReadOnlyDictionary<string, string> profileSources,
        List<ValidationIssue> issues)
    {
        HashSet<string> serviceIds = new(registry.Services.Select(service => service.Id), StringComparer.Ordinal);

        foreach (PolicyDocument profile in byProfileId.Values)
        {
            string source = profileSources[profile.ProfileId];

            foreach (string allowed in profile.Constraints.AllowedServiceIds)
            {
                if (!serviceIds.Contains(allowed))
                {
                    issues.Add(new ValidationIssue(
                        source, "/constraints/allowedServiceIds", null, ValidationCodes.UnknownReference,
                        "The profile allows a service identifier the registry does not declare."));
                }
            }
        }
    }

    /// <summary>
    /// Vero se il profilo ammette un servizio che il registro dichiara come agente. Sostituisce
    /// il vincolo su <c>allowedKinds</c>: il tipo non e' piu' un campo, lo dice la capacita'.
    /// </summary>
    private static bool AllowsAgentService(PolicyDocument profile, RegistryDocument registry) =>
        registry.Services.Any(service =>
            service.Capability is ServiceCapability.CodeAgent
            && profile.Constraints.AllowsService(service.Id));

    private static void ValidateTelemetry(ConfigDocument config, List<ValidationIssue> issues)
    {
        switch (config.Telemetry.Exporter)
        {
            case TelemetryExporter.None when config.Telemetry.Endpoint is not null:
                issues.Add(new ValidationIssue(
                    ConfigFileName, "/telemetry/endpoint", null, ValidationCodes.InconsistentDeclaration,
                    "No endpoint may be configured while the exporter is none."));
                break;

            case TelemetryExporter.Otlp when config.Telemetry.Endpoint is null:
                issues.Add(new ValidationIssue(
                    ConfigFileName, "/telemetry/endpoint", null, ValidationCodes.InconsistentDeclaration,
                    "The otlp exporter requires an endpoint."));
                break;

            case TelemetryExporter.Otlp when !string.Equals(config.Telemetry.Endpoint!.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                                             && !config.Telemetry.Endpoint.IsLoopback:
                // Una destinazione remota in chiaro sarebbe un nuovo canale di uscita dei dati.
                issues.Add(new ValidationIssue(
                    ConfigFileName, "/telemetry/endpoint", null, ValidationCodes.InconsistentDeclaration,
                    "A remote telemetry endpoint must use https."));
                break;

            default:
                break;
        }
    }

    private static string ServicePointer(int index) =>
        "/services/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
