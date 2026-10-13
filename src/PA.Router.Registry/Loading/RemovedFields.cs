using System.Globalization;
using System.Text.Json.Nodes;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Campi tolti dalla versione 2 da ADR-032, fase 1 (24 settembre 2026): parametri che nel turno non facevano nulla, o
/// che servivano la sola memoria potenziata, rimossa per decisione dell'operatore.
///
/// Lo schema li rifiuterebbe comunque, ma con un generico «proprieta' non definita» che non dice come uscirne: qui li si
/// riconosce, si nomina il comando che li toglie (<c>MIGRATION_REQUIRED</c>) e la conversione li toglie dichiarando
/// ogni modifica. Come per le altre forme sostituite, la conversione non avviene mai alla lettura
/// (<see cref="ConfigMigration"/>).
///
/// Nessuna rimozione allarga cio' che raggiunge un terzo:
/// <list type="bullet">
/// <item><c>allowedPluginIds</c> e <c>allowedSkillIds</c> non venivano mai consultati nel turno; una skill entra solo con
/// il permesso del pacchetto per il progetto e per il digest, che resta.</item>
/// <item><c>allowedCapabilityServices</c> si converte restringendo <c>allowedServiceIds</c> ai soli servizi che l'elenco
/// per capacita' ammetteva: lo stesso insieme di prima, detto una volta.</item>
/// <item><c>requireLocalMemoryRouting</c> e <c>memory.enhancedEmbeddingServiceId</c> servivano l'indice potenziato, che
/// non esiste piu': ogni ricordo va all'indice locale.</item>
/// <item><c>processPlacement</c> non lo leggeva nessun controllo, che guardano <c>inferencePlacement</c>.</item>
/// <item><c>calibration.cost</c> e <c>calibration.quota</c> erano numeri scritti a mano: il costo viene da
/// <c>pricing</c>, la quota da cio' che la sonda osserva.</item>
/// </list>
///
/// Fase 2 (stesso giorno): i doppioni.
/// <list type="bullet">
/// <item>I limiti di esecuzione del profilo (<c>timeoutSeconds</c>, <c>maxAttempts</c>, <c>maxInputBytes</c>,
/// <c>maxContextBytes</c>, <c>maxOutputBytes</c>, <c>maxMemoryRetentionDays</c>) stanno solo nella configurazione: la
/// conversione vi scrive il **minimo** fra il valore della configurazione e quello di ogni profilo
/// (<see cref="FoldProfileLimits"/>), quindi nessun limite si allarga.</item>
/// <item><c>channel</c> lo dice il front end: l'host API e' <c>chatbot</c>, terminale, REPL, worker e script sono
/// <c>personal_cli</c>. L'API non raggiunge mai un agente (INV-23) per il canale da cui arriva, non per un campo.</item>
/// <item><c>routing.piiAttachmentBudgetMs</c>: il budget degli allegati si ricava da quello della richiesta
/// (<c>RoutingConfig.AttachmentBudgetFactor</c>).</item>
/// <item><c>routing.analyzer.mode</c>: l'analizzatore configurato e' acceso e decide solo se misurato; <c>off</c> si
/// converte togliendo l'analizzatore, cioe' resta spento.</item>
/// <item><c>routing.classifier</c>: il classificatore a embedding non esiste piu'; chat o codice lo dicono le regole
/// (<c>RequestClassifier</c>), come gia' accadeva senza classificatore configurato.</item>
/// <item><c>memory.minimumSimilarity</c>: una costante interna, 0,5.</item>
/// </list>
///
/// Fase 3 (stesso giorno): una scala sola per la qualita', l'ordine dei modelli (<c>rank</c>).
/// <list type="bullet">
/// <item><c>ratings</c> di un servizio: la conversione ne ricava il posto nell'ordine per chi non ne ha uno
/// (<see cref="RatingsToRank"/>), lasciando com'e' ogni posto gia' dato, e poi li toglie.</item>
/// <item><c>routing.bands</c> e <c>minimumBandRequirements</c>: nessuna qualita' minima per difficolta'; la finestra che
/// una richiesta pretende si calcola dalla sua dimensione, mai sotto 4096 token.</item>
/// </list>
/// La qualita' non ha mai deciso dove vanno i dati: nessuna di queste rimozioni cambia che cosa esce dalla macchina.
///
/// Fase 4 (25 settembre 2026): dove vanno i dati lo decidono solo le zone. I campi tolti — collocazioni, autenticazioni,
/// costi API, controlli e regioni richiesti, perimetro, forma precedente alle zone nei profili; <c>dataHandling</c>,
/// <c>evidenceExpiresAt</c> e <c>unverified</c> nei servizi — li converte <see cref="DataZoneConversion"/>, che verifica
/// servizio per servizio e classe per classe che nulla si allarghi. Qui se ne riconosce solo la presenza.
/// </summary>
public static class RemovedFields
{
    /// <summary>Campi tolti dai vincoli di un profilo.</summary>
    public static IReadOnlyList<string> Profile { get; } =
    [
        "allowedPluginIds", "allowedSkillIds", "allowedCapabilityServices", "requireLocalMemoryRouting",
        "channel", "timeoutSeconds", "maxAttempts", "maxInputBytes", "maxContextBytes", "maxOutputBytes",
        "maxMemoryRetentionDays", "minimumBandRequirements",
    ];

    /// <summary>
    /// Limiti del profilo che dalla fase 2 vivono solo nella configurazione: nome nel profilo, sezione e nome nella
    /// configurazione, valore che la configurazione ha quando non lo dichiara (null: lo schema lo pretende).
    /// </summary>
    public static IReadOnlyList<(string Profile, string Section, string Config, int? Default)> Limits { get; } =
    [
        ("timeoutSeconds", "limits", "timeoutSeconds", null),
        ("maxAttempts", "limits", "maxAttempts", null),
        ("maxInputBytes", "limits", "maxInputBytes", LimitsConfig.DefaultMaxInputBytes),
        ("maxContextBytes", "limits", "maxContextBytes", LimitsConfig.DefaultMaxContextBytes),
        ("maxOutputBytes", "limits", "maxOutputBytes", LimitsConfig.DefaultMaxOutputBytes),
        ("maxMemoryRetentionDays", "memory", "retentionDays", null),
    ];

    /// <summary>Campi tolti da un servizio del registro.</summary>
    public static IReadOnlyList<string> Service { get; } = ["processPlacement", "ratings"];

    /// <summary>Calibrazioni scritte a mano tolte da un servizio del registro.</summary>
    public static IReadOnlyList<string> Calibration { get; } = ["cost", "quota"];

    /// <summary>Campi tolti dalla sezione <c>memory</c> della configurazione.</summary>
    public static IReadOnlyList<string> Memory { get; } = ["enhancedEmbeddingServiceId", "minimumSimilarity"];

    /// <summary>Campi tolti dalla sezione <c>routing</c> della configurazione.</summary>
    public static IReadOnlyList<string> Routing { get; } = ["piiAttachmentBudgetMs", "classifier", "bands"];

    private const string Message = "This field was removed (ADR-032): run 'pa config migrate' to remove it.";

    /// <summary>Coda delle righe della fase 3: la qualita' non ha mai deciso dove vanno i dati.</summary>
    private const string NothingLeaves = "Quality never decided where data may go: nothing about data leaving the machine changes";

    /// <summary>
    /// Finestra minima di ogni richiesta, in token: <c>RequestAnalyzer.MinimumWindowTokens</c>, ripetuta qui perche' il
    /// registro non dipende dall'analisi. Serve solo a dirlo nelle righe della conversione.
    /// </summary>
    private const int MinimumWindowTokens = 4096;

    /// <summary>Posto massimo che lo schema ammette per <c>rank</c>.</summary>
    private const int MaximumRank = 64;

    /// <summary>Aggiunge un problema per ogni campo tolto ancora presente nel documento.</summary>
    /// <returns>Vero se il documento porta almeno un campo tolto.</returns>
    public static bool Report(ContractSchema schema, string source, ParsedDocument document, List<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(issues);

        if (document.Root is not JsonObject root)
        {
            return false;
        }

        int before = issues.Count;

        foreach (string pointer in Pointers(schema, root))
        {
            issues.Add(new ValidationIssue(source, pointer, document.LineOf(pointer), ValidationCodes.MigrationRequired, Message));
        }

        return issues.Count > before;
    }

    /// <summary>Puntatori JSON dei campi tolti presenti in un documento.</summary>
    public static IEnumerable<string> Pointers(ContractSchema schema, JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        switch (schema)
        {
            case ContractSchema.Config:
                if (root["memory"] is JsonObject memory)
                {
                    foreach (string name in Memory.Where(memory.ContainsKey))
                    {
                        yield return "/memory/" + name;
                    }
                }

                if (root["routing"] is JsonObject routing)
                {
                    foreach (string name in Routing.Where(routing.ContainsKey))
                    {
                        yield return "/routing/" + name;
                    }

                    if (routing["analyzer"] is JsonObject analyzer && analyzer.ContainsKey("mode"))
                    {
                        yield return "/routing/analyzer/mode";
                    }
                }

                break;

            case ContractSchema.Policy when root["constraints"] is JsonObject constraints:
                foreach (string name in Profile.Concat(DataZoneConversion.ProfileFields).Where(constraints.ContainsKey))
                {
                    yield return "/constraints/" + name;
                }

                // Fase 4: il perimetro e' fisso.
                if (constraints["zones"] is JsonObject zones && zones.ContainsKey("perimeter"))
                {
                    yield return "/constraints/zones/perimeter";
                }

                break;

            case ContractSchema.Registry when root["services"] is JsonArray services:
                for (int index = 0; index < services.Count; index++)
                {
                    if (services[index] is not JsonObject service)
                    {
                        continue;
                    }

                    string prefix = "/services/" + index.ToString(CultureInfo.InvariantCulture);

                    foreach (string name in Service.Concat(DataZoneConversion.ServiceFields).Where(service.ContainsKey))
                    {
                        yield return prefix + "/" + name;
                    }

                    if (service["calibration"] is JsonObject calibration)
                    {
                        foreach (string name in Calibration.Where(calibration.ContainsKey))
                        {
                            yield return prefix + "/calibration/" + name;
                        }
                    }
                }

                break;
        }
    }

    /// <summary>Toglie i campi tolti dalla configurazione, nelle sezioni <c>memory</c> e <c>routing</c>.</summary>
    public static void RemoveFromConfig(JsonObject config, List<string> changes)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(changes);

        if (config["memory"] is JsonObject memory)
        {
            RemoveFromMemory(memory, changes);
        }

        if (config["routing"] is JsonObject routing)
        {
            RemoveFromRouting(routing, changes);
        }
    }

    private static void RemoveFromMemory(JsonObject memory, List<string> changes)
    {
        if (memory.ContainsKey("enhancedEmbeddingServiceId"))
        {
            bool declared = memory["enhancedEmbeddingServiceId"] is JsonValue value && value.TryGetValue(out string? _);
            memory.Remove("enhancedEmbeddingServiceId");

            changes.Add(declared
                ? "memory/enhancedEmbeddingServiceId removed: the external memory index no longer exists (ADR-032). New memories "
                    + "are indexed only by the local embedding service; those already sent out stay searchable by text"
                : "memory/enhancedEmbeddingServiceId removed: it was empty, and the external memory index no longer exists (ADR-032)");
        }

        if (memory["minimumSimilarity"] is JsonNode similarity)
        {
            string was = similarity.ToJsonString();
            memory.Remove("minimumSimilarity");
            changes.Add("memory/minimumSimilarity removed (was " + was + "): the similarity floor is the router's own constant, "
                + MemoryLimits.DefaultMinimumSimilarity.ToString("0.0", CultureInfo.InvariantCulture) + " (ADR-032)");
        }
    }

    private static void RemoveFromRouting(JsonObject routing, List<string> changes)
    {
        if (routing["piiAttachmentBudgetMs"] is JsonNode attachments)
        {
            string was = attachments.ToJsonString();
            routing.Remove("piiAttachmentBudgetMs");

            int budget = IntOf(routing["piiLatencyBudgetMs"]) ?? 0;
            changes.Add("routing/piiAttachmentBudgetMs removed (was " + was + "): attachments get "
                + RoutingConfig.AttachmentBudgetFactor.ToString(CultureInfo.InvariantCulture) + " times piiLatencyBudgetMs, now "
                + (budget * RoutingConfig.AttachmentBudgetFactor).ToString(CultureInfo.InvariantCulture) + " ms (ADR-032). "
                + "A scan that does not finish in time still makes the turn unknown and keeps it inside");
        }

        if (routing["bands"] is JsonObject bands)
        {
            string was = string.Join(", ", new[] { "trivial", "standard", "heavy" }
                .Where(name => bands[name] is JsonObject)
                .Select(name => name + " " + (bands[name]!["minQuality"]?.ToJsonString() ?? "?") + "/"
                    + (bands[name]!["minContextTokens"]?.ToJsonString() ?? "?")));

            routing.Remove("bands");
            changes.Add("routing/bands removed (was minQuality/minContextTokens " + was + "): no minimum quality per difficulty "
                + "any more, the model order weighs in the score instead; the window a request needs is its size plus the "
                + "reply, at least " + MinimumWindowTokens.ToString(CultureInfo.InvariantCulture) + " tokens (ADR-032). " + NothingLeaves);
        }
        else if (routing.Remove("bands"))
        {
            changes.Add("routing/bands removed (ADR-032). " + NothingLeaves);
        }

        if (routing.Remove("classifier"))
        {
            changes.Add("routing/classifier removed: the embedding classifier no longer exists (ADR-032). Chat or code is "
                + "decided by the rules, as it already was whenever the classifier model was below its margin or unusable; "
                + "shell access was only ever granted by the rules");
        }

        if (routing["analyzer"] is JsonObject analyzer && analyzer["mode"] is JsonValue mode)
        {
            string wire = mode.TryGetValue(out string? text) ? text : string.Empty;

            if (string.Equals(wire, AnalyzerMode.Off.ToWire(), StringComparison.Ordinal))
            {
                routing.Remove("analyzer");
                changes.Add("routing/analyzer removed: its mode was off, and a configured analyzer is now always on (ADR-032). "
                    + "It stays off");
            }
            else
            {
                analyzer.Remove("mode");
                changes.Add("routing/analyzer/mode removed (was " + wire + "): the analyzer decides when its model and prompt "
                    + "have a measured benchmark result, otherwise it runs in shadow by itself (ADR-032). It never changes where "
                    + "data may go");
            }
        }
    }

    /// <summary>
    /// Porta nella configurazione i limiti che i profili dichiaravano (ADR-032, fase 2), **senza allargare**: per ogni
    /// limite vale il minimo fra la configurazione e ogni profilo. Prima valeva il piu' stretto fra i due (tempo e
    /// tentativi) o quello del profilo (byte e ricordi): il minimo su tutti non e' mai piu' largo di nessuno dei due per
    /// nessuna modalita'. Le modalita' piu' larghe si restringono a quella piu' stretta: e' il prezzo di un posto solo, e
    /// ogni riga lo dice.
    /// </summary>
    /// <param name="config">Configurazione, modificata sul posto.</param>
    /// <param name="profiles">Profili con i loro vincoli, prima di togliere i campi.</param>
    /// <param name="changes">Modifiche dichiarate.</param>
    public static void FoldProfileLimits(
        JsonObject config,
        IReadOnlyList<(string ProfileId, JsonObject Constraints)> profiles,
        List<string> changes)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(changes);

        foreach ((string inProfile, string sectionName, string inConfig, int? fallback) in Limits)
        {
            List<(string ProfileId, int Value)> declared =
            [
                .. profiles
                    .Where(profile => IntOf(profile.Constraints[inProfile]) is not null)
                    .Select(profile => (profile.ProfileId, IntOf(profile.Constraints[inProfile])!.Value)),
            ];

            if (declared.Count == 0)
            {
                continue;
            }

            if (config[sectionName] is not JsonObject section)
            {
                section = [];
                config[sectionName] = section;
            }

            int? current = IntOf(section[inConfig]) ?? fallback;
            int strictest = declared.Min(entry => entry.Value);
            int value = current is int configured ? Math.Min(configured, strictest) : strictest;

            string from = string.Join(", ", declared.Select(entry => entry.ProfileId + " " + entry.Value.ToString(CultureInfo.InvariantCulture)));
            string path = sectionName + "/" + inConfig;
            string before = current?.ToString(CultureInfo.InvariantCulture) ?? "absent";

            section[inConfig] = value;

            changes.Add(current == value
                ? path + " = " + value.ToString(CultureInfo.InvariantCulture) + ": " + inProfile + " moved from the profiles ("
                    + from + "); no profile was stricter than the configuration, nothing widens (ADR-032)"
                : path + ": " + before + " -> " + value.ToString(CultureInfo.InvariantCulture) + ": " + inProfile
                    + " moved from the profiles (" + from + "); the strictest value applies to every profile, nothing widens (ADR-032)");
        }
    }

    /// <summary>Toglie da un servizio del registro la collocazione del processo e le calibrazioni scritte a mano.</summary>
    public static void RemoveFromService(JsonObject service, List<string> changes)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(changes);

        string id = service["id"]?.GetValue<string>() ?? "unknown";

        if (service.Remove("processPlacement"))
        {
            changes.Add(id + ": processPlacement removed: no rule read it, decisions use inferencePlacement (ADR-032)");
        }

        if (service["calibration"] is not JsonObject calibration)
        {
            return;
        }

        if (calibration.Remove("cost"))
        {
            changes.Add(id + ": calibration/cost removed: the cost comes from pricing, relative to the cheapest (ADR-032)");
        }

        if (calibration.Remove("quota"))
        {
            changes.Add(id + ": calibration/quota removed: the quota is what the probe observes (ADR-032)");
        }

        if (calibration.Count == 0)
        {
            service.Remove("calibration");
        }
    }

    /// <summary>
    /// I voti per abilita' diventano il posto nell'ordine dei modelli (ADR-032, fase 3), poi si tolgono.
    ///
    /// <list type="bullet">
    /// <item>Un posto gia' dato dall'operatore resta com'e': l'ordine e' una sua scelta, piu' recente dei voti.</item>
    /// <item>Solo i servizi che rispondono alle richieste — chat e agenti — hanno un posto; un voto su un altro servizio
    /// non era mai letto e si toglie.</item>
    /// <item>Il voto che conta e' il piu' alto fra le abilita' per cui il servizio puo' essere scelto (una chat: chat; un
    /// agente: analisi o modifica del codice).</item>
    /// <item>I servizi senza posto si mettono **dopo** quelli che ne hanno uno, per voto decrescente. Pari voto, pari posto:
    /// l'ordine ammette la pari posizione, e inventare una differenza fra due modelli che l'operatore aveva giudicato
    /// uguali sarebbe una scelta non sua. A pari voto si elencano per identificatore.</item>
    /// </list>
    ///
    /// Idempotente: dopo il primo passaggio nessun servizio porta <c>ratings</c>, e il secondo non trova niente.
    /// </summary>
    /// <param name="services">Servizi del registro, modificati sul posto.</param>
    /// <param name="changes">Modifiche dichiarate.</param>
    public static void RatingsToRank(JsonArray services, List<string> changes)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(changes);

        JsonObject[] all = [.. services.OfType<JsonObject>()];
        JsonObject[] rated = [.. all.Where(service => service.ContainsKey("ratings"))];

        if (rated.Length == 0)
        {
            return;
        }

        int last = all.Select(service => IntOf(service["rank"]) ?? 0).DefaultIfEmpty(0).Max();

        (JsonObject Service, double Rating)[] waiting =
        [
            .. rated
                .Where(service => AnswersRequests(service) && IntOf(service["rank"]) is null && BestRating(service) is not null)
                .Select(service => (service, BestRating(service)!.Value))
                .OrderByDescending(entry => entry.Item2)
                .ThenBy(entry => IdOf(entry.service), StringComparer.Ordinal),
        ];

        Dictionary<JsonObject, int> assigned = new(ReferenceEqualityComparer.Instance);

        for (int index = 0; index < waiting.Length; index++)
        {
            // Competizione: pari voto, pari posto; chi viene dopo prende il posto che gli spetta contando chi lo precede.
            assigned[waiting[index].Service] = index > 0 && waiting[index].Rating.Equals(waiting[index - 1].Rating)
                ? assigned[waiting[index - 1].Service]
                : Math.Min(last + index + 1, MaximumRank);
        }

        foreach (JsonObject service in rated)
        {
            string id = IdOf(service);
            string was = service["ratings"] is JsonObject ratings
                ? string.Join(", ", ratings.Select(pair => pair.Key + " " + (pair.Value?.ToJsonString() ?? "null")))
                : service["ratings"]?.ToJsonString() ?? "null";

            service.Remove("ratings");

            if (assigned.TryGetValue(service, out int rank))
            {
                service["rank"] = rank;
                changes.Add(id + ": ratings (" + was + ") -> rank " + rank.ToString(CultureInfo.InvariantCulture)
                    + ": the model order is the only quality scale (ADR-032); an equal rating gets an equal place, after the "
                    + "services the operator had already ordered. " + NothingLeaves);
            }
            else if (IntOf(service["rank"]) is int kept)
            {
                changes.Add(id + ": ratings (" + was + ") removed: its place in the model order, rank "
                    + kept.ToString(CultureInfo.InvariantCulture) + ", stays as the operator gave it (ADR-032). " + NothingLeaves);
            }
            else
            {
                changes.Add(id + ": ratings (" + was + ") removed: "
                    + (AnswersRequests(service) ? "no rating for an ability this service is chosen for" : "this service does not answer requests")
                    + ", so it gets no place in the model order (ADR-032). " + NothingLeaves);
            }
        }
    }

    private static string IdOf(JsonObject service) => service["id"]?.GetValue<string>() ?? "unknown";

    /// <summary>Chat e agenti: i soli servizi che hanno un posto nell'ordine.</summary>
    private static bool AnswersRequests(JsonObject service) =>
        service["capability"]?.GetValue<string>() is string capability
        && (capability == ServiceCapability.Chat.ToWire() || capability == ServiceCapability.CodeAgent.ToWire());

    /// <summary>Il voto piu' alto fra le abilita' per cui il servizio puo' essere scelto; null se non ne ha.</summary>
    private static double? BestRating(JsonObject service)
    {
        if (service["ratings"] is not JsonObject ratings
            || !WireEnum.TryParse(service["capability"]?.GetValue<string>() ?? string.Empty, out ServiceCapability capability))
        {
            return null;
        }

        double[] values =
        [
            .. CapabilityRouting.AbilitiesOf(capability)
                .Select(ability => NumberOf(ratings[ability.ToWire()]))
                .Where(value => value is not null)
                .Select(value => value!.Value),
        ];

        return values.Length == 0 ? null : values.Max();
    }

    /// <summary>
    /// Toglie i campi tolti dai vincoli di un profilo.
    ///
    /// L'elenco per capacita' restringeva: la conversione lo porta dentro <c>allowedServiceIds</c>, togliendo i servizi
    /// che non ammetteva. Per farlo serve la capacita' di ciascun servizio, letta dal registro; un servizio che il registro
    /// non dichiara non era ammissibile comunque e resta com'e', perche' la validazione lo segnala gia'.
    /// </summary>
    /// <param name="constraints">Vincoli del profilo, modificati sul posto.</param>
    /// <param name="capabilityOf">Capacita' di ciascun servizio del registro, per identificatore.</param>
    /// <param name="changes">Modifiche dichiarate.</param>
    public static void RemoveFromProfile(JsonObject constraints, IReadOnlyDictionary<string, string> capabilityOf, List<string> changes)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        ArgumentNullException.ThrowIfNull(capabilityOf);
        ArgumentNullException.ThrowIfNull(changes);

        foreach (string name in new[] { "allowedPluginIds", "allowedSkillIds" })
        {
            if (constraints.Remove(name))
            {
                changes.Add(name + " removed: a skill or plugin still needs the operator's grant for this project and "
                    + "digest, and this list was never checked in a turn (ADR-032)");
            }
        }

        if (constraints["allowedCapabilityServices"] is JsonObject byCapability)
        {
            FoldCapabilityServices(constraints, byCapability, capabilityOf, changes);
        }

        if (constraints.Remove("allowedCapabilityServices"))
        {
            changes.Add("allowedCapabilityServices removed: allowedServiceIds alone says which services this profile uses (ADR-032)");
        }

        if (constraints.Remove("requireLocalMemoryRouting"))
        {
            changes.Add("requireLocalMemoryRouting removed: the external memory index no longer exists, every memory is "
                + "indexed only by the local embedding service (ADR-032)");
        }

        if (constraints["channel"] is JsonValue channel)
        {
            string wire = channel.TryGetValue(out string? text) ? text : string.Empty;
            constraints.Remove("channel");
            changes.Add("channel removed (was " + wire + "): the channel comes from the front end, chatbot for the API and "
                + "personal_cli for the terminal (ADR-032). The API still never reaches an agent, and the API profile still may "
                + "not allow agents or keys");
        }

        if (constraints["minimumBandRequirements"] is JsonNode minimum)
        {
            string was = minimum.ToJsonString();
            constraints.Remove("minimumBandRequirements");
            changes.Add("minimumBandRequirements removed (was " + was + "): no profile imposes a minimum quality or window per "
                + "difficulty any more; the window a request needs is computed from its size (ADR-032). " + NothingLeaves);
        }

        foreach ((string inProfile, string section, string inConfig, _) in Limits)
        {
            if (constraints.Remove(inProfile))
            {
                changes.Add(inProfile + " removed: " + section + "/" + inConfig + " in the configuration applies to every "
                    + "profile, set to the strictest value (ADR-032)");
            }
        }
    }

    private static void FoldCapabilityServices(
        JsonObject constraints,
        JsonObject byCapability,
        IReadOnlyDictionary<string, string> capabilityOf,
        List<string> changes)
    {
        if (constraints["allowedServiceIds"] is not JsonArray allowed)
        {
            return;
        }

        JsonArray kept = [];

        foreach (JsonNode? entry in allowed)
        {
            string id = entry?.GetValue<string>() ?? string.Empty;

            bool admitted = !capabilityOf.TryGetValue(id, out string? capability)
                || (byCapability[capability] is JsonArray list
                    && list.Any(item => string.Equals(item?.GetValue<string>(), id, StringComparison.Ordinal)));

            if (admitted)
            {
                kept.Add(id);
            }
            else
            {
                changes.Add("allowedServiceIds: " + id + " removed: allowedCapabilityServices did not allow it for " + capability);
            }
        }

        constraints["allowedServiceIds"] = kept;
    }

    /// <summary>Un numero letto da YAML o da JSON, intero o decimale.</summary>
    private static double? NumberOf(JsonNode? node) =>
        node is JsonValue value
            ? value.TryGetValue(out double number) ? number
                : value.TryGetValue(out long integer) ? integer
                : value.TryGetValue(out int small) ? small
                : null
            : null;

    /// <summary>Un intero letto da YAML o da JSON: il lettore lo conserva come <c>long</c>, un documento costruito come <c>int</c>.</summary>
    private static int? IntOf(JsonNode? node) =>
        node is JsonValue value
            ? value.TryGetValue(out int small) ? small
                : value.TryGetValue(out long large) && large is >= int.MinValue and <= int.MaxValue ? (int)large
                : null
            : null;
}
