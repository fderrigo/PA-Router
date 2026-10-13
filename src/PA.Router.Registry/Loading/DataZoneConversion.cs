using System.Globalization;
using System.Text.Json.Nodes;
using PA.Router.Abstractions.Contracts;
using PA.Router.Registry.Probing;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Un profilo da convertire alla fase 4 di ADR-032: i suoi vincoli, modificati sul posto, e le righe della conversione.
/// </summary>
/// <param name="ProfileId">Identificatore del profilo, per le righe.</param>
/// <param name="Constraints">Vincoli del profilo, gia' portati alle zone se erano nella forma precedente.</param>
/// <param name="Changes">Righe della conversione di questo profilo.</param>
public sealed record ZoneConversionProfile(string ProfileId, JsonObject Constraints, List<string> Changes)
{
    /// <summary>Zone scritte dalla conversione, null se non le ha toccate.</summary>
    public PolicyZones? Zones { get; set; }
}

/// <summary>
/// Conversione della fase 4 di ADR-032 (25 settembre 2026): **una sola regola decide dove vanno i dati**, le zone per
/// classe. Collocazioni, autenticazioni, costi API, controlli richiesti, regioni e perimetro dei profili, e
/// <c>dataHandling</c>, <c>evidenceExpiresAt</c> e <c>unverified</c> dei servizi, si tolgono.
///
/// <para>La conversione **non allarga mai**. Per ogni profilo:</para>
/// <list type="number">
/// <item>si calcola che cosa le regole di prima ammettevano davvero, servizio per servizio, classe per classe, con e senza
/// allegati, con e senza conferma, prima e dopo l'osservazione della sonda (<see cref="OldRules"/>);</item>
/// <item>per ogni classe la zona nuova e' la piu' stretta fra quella dichiarata e la zona piu' lontana fra i servizi che
/// le regole di prima ammettevano per quella classe (nessuno: questo computer);</item>
/// <item>con le regole nuove si verifica ogni combinazione: se un servizio diventa ammesso dove prima non lo era e le
/// regole di prima non lo facevano mai usare a quella modalita', esce dall'elenco dei servizi; se le regole di prima lo
/// usavano per un'altra classe, la zona di quella classe scende sotto quella del servizio; se non si puo' scendere (questo
/// computer, o segreti e testi non classificati), il servizio esce dall'elenco. Si ripete finche' nulla si allarga, e un
/// elenco che resterebbe vuoto — vuoto vuol dire tutti — e' un problema che ferma la conversione.</item>
/// </list>
/// <para>Ogni modifica ha la sua riga, e cio' che diventa negato si elenca.</para>
///
/// <para>Una conferma che prima non contava (scaduta, o senza scadenza) e dopo conterebbe — le conferme non scadono piu' —
/// si toglie dal registro: va data di nuovo. Cosi' i controlli effettivi di ogni servizio sono gli stessi prima e dopo, e
/// l'unica differenza fra le due regole e' la regola stessa.</para>
///
/// <para>Le regole nuove sono quelle del valutatore, prese dalle stesse funzioni (<see cref="ServiceZones"/>,
/// <see cref="AdmissionControls"/>, <see cref="ZoneRule"/>, <see cref="AllowedServices"/>). Le regole di prima sono una
/// copia privata, marcata, usata solo qui: le prove le confrontano con le decisioni del valutatore di prima, registrate
/// prima di toglierlo (<c>docs/06-contracts/fixtures/migration-phase4</c>).</para>
/// </summary>
public static class DataZoneConversion
{
    /// <summary>Campi tolti dai vincoli di un profilo.</summary>
    public static IReadOnlyList<string> ProfileFields { get; } =
    [
        "allowedInferencePlacements", "allowedAuthModes", "allowApiBilling", "requiredControls", "requiredRegions",
        "externalProcessing", "maxCloudDataClass",
    ];

    /// <summary>Campi tolti da un servizio del registro.</summary>
    public static IReadOnlyList<string> ServiceFields { get; } = ["dataHandling", "evidenceExpiresAt", "unverified"];

    private static readonly IReadOnlyList<DataClass> AllClasses = Enum.GetValues<DataClass>();

    /// <summary>Vero se il registro o uno dei profili porta ancora una forma che la fase 4 toglie.</summary>
    public static bool IsNeeded(JsonArray services, IEnumerable<JsonObject> constraints)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(constraints);

        return services.OfType<JsonObject>().Any(service => ServiceFields.Any(service.ContainsKey))
            || constraints.Any(HasOldFields);
    }

    /// <summary>Converte registro e profili sul posto, senza allargare nulla.</summary>
    /// <param name="services">Servizi del registro, nella forma di prima.</param>
    /// <param name="profiles">Profili da convertire.</param>
    /// <param name="now">Istante della conversione: decide quali conferme e quali evidenze valevano.</param>
    /// <param name="registryChanges">Righe della conversione del registro.</param>
    /// <param name="problems">Motivi che impediscono la conversione.</param>
    public static void Convert(
        JsonArray services,
        IReadOnlyList<ZoneConversionProfile> profiles,
        DateTimeOffset now,
        List<string> registryChanges,
        List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(registryChanges);
        ArgumentNullException.ThrowIfNull(problems);

        if (!IsNeeded(services, profiles.Select(profile => profile.Constraints)))
        {
            return;
        }

        List<Service> parsed = [.. services.OfType<JsonObject>().Select(entry => Service.Parse(entry, now)).OfType<Service>()];

        foreach (ZoneConversionProfile profile in profiles)
        {
            ConvertProfile(profile, parsed, now, problems);
        }

        foreach (JsonObject service in services.OfType<JsonObject>())
        {
            StripService(service, now, registryChanges);
        }
    }

    // ── profilo ───────────────────────────────────────────────────────────────

    private static void ConvertProfile(ZoneConversionProfile profile, IReadOnlyList<Service> services, DateTimeOffset now, List<string> problems)
    {
        JsonObject constraints = profile.Constraints;

        if (constraints["zones"] is not JsonObject zonesNode || zonesNode["maxZone"] is not JsonObject maxZoneNode)
        {
            problems.Add("The profile " + profile.ProfileId + " declares neither zones nor the form before zones; it cannot be converted.");
            return;
        }

        OldRules.Profile old = OldRules.Profile.Parse(constraints, now);
        HashSet<Scenario> before = [.. Scenarios(services).Where(scenario => OldRules.Admits(old, Find(services, scenario), scenario))];

        // 1. La zona piu' lontana che le regole di prima raggiungevano per ciascuna classe.
        Dictionary<DataClass, TrustZone> declared = [];
        Dictionary<DataClass, TrustZone> zones = [];

        foreach (DataClass dataClass in PolicyZones.DeclaredClasses)
        {
            declared[dataClass] = old.Zones.For(dataClass);

            TrustZone reached = before
                .Where(scenario => scenario.Class == dataClass)
                .Select(scenario => NewZone(Find(services, scenario), scenario.Observed))
                .DefaultIfEmpty(TrustZone.SameHost)
                .Max();

            zones[dataClass] = Min(declared[dataClass], reached);

            if (zones[dataClass] < declared[dataClass])
            {
                profile.Changes.Add("zones.maxZone." + dataClass.ToWire() + ": " + declared[dataClass].ToWire() + " -> "
                    + zones[dataClass].ToWire() + ": the rules before ADR-032 phase 4 (placements, sign-in, API costs, required "
                    + "controls, regions, service list) let this class reach at most " + zones[dataClass].ToWire());
            }
        }

        // 2. Verifica con le regole nuove, e restrizioni finche' nulla si allarga.
        List<string> allowed = [.. old.AllowedServiceIds];
        HashSet<string> everAdmitted = new(before.Select(scenario => scenario.ServiceId), StringComparer.Ordinal);
        IReadOnlyList<string> everyService = [.. services.Select(service => service.Id)];

        while (true)
        {
            New rules = new(allowed, zones, old);
            Scenario[] widened = [.. Scenarios(services).Where(scenario => !before.Contains(scenario) && rules.Admits(Find(services, scenario), scenario))];

            if (widened.Length == 0)
            {
                break;
            }

            // Prima l'elenco: esce chi le regole di prima non facevano mai usare a questa modalita' e le nuove userebbero.
            // Resta chi le regole di prima usavano per almeno una classe, e chi non risponde alle richieste (embedding,
            // rilevatori), che l'elenco non riguarda. Un elenco che resterebbe vuoto non si scrive: vuoto vuol dire tutti.
            HashSet<string> intruders = new(
                widened.Select(scenario => scenario.ServiceId).Where(id => !everAdmitted.Contains(id)),
                StringComparer.Ordinal);
            List<string> narrowed = [.. (allowed.Count > 0 ? allowed : everyService).Where(id => !intruders.Contains(id))];

            if (intruders.Count > 0 && narrowed.Count > 0)
            {
                foreach (string removed in (allowed.Count > 0 ? allowed : everyService).Where(intruders.Contains))
                {
                    profile.Changes.Add("allowedServiceIds: " + removed + " removed: the rules before ADR-032 phase 4 never "
                        + "let this mode use it, and under the new rules it would have been used");
                }

                allowed = narrowed;
                continue;
            }

            Scenario wide = widened[0];
            Service service = Find(services, wide);
            TrustZone zone = NewZone(service, wide.Observed);

            if (zones.TryGetValue(wide.Class, out TrustZone current) && zone > TrustZone.SameHost)
            {
                TrustZone lowered = Min(current, zone - 1);
                profile.Changes.Add("zones.maxZone." + wide.Class.ToWire() + ": " + current.ToWire() + " -> " + lowered.ToWire()
                    + ": otherwise " + service.Id + " would receive " + wide.Class.ToWire() + " data, which the rules before "
                    + "ADR-032 phase 4 denied");
                zones[wide.Class] = lowered;
                continue;
            }

            List<string> without = [.. (allowed.Count > 0 ? allowed : everyService).Where(id => !string.Equals(id, service.Id, StringComparison.Ordinal))];

            if (without.Count == 0)
            {
                problems.Add("The profile " + profile.ProfileId + " cannot be converted without widening: under the new rules "
                    + service.Id + " would receive " + wide.Class.ToWire() + " data, and removing it would leave an empty service "
                    + "list, which now means every service. Add a service to allowedServiceIds or remove the profile.");
                return;
            }

            profile.Changes.Add("allowedServiceIds: " + service.Id + " removed: under the new rules it would receive "
                + wide.Class.ToWire() + " data, which the rules before ADR-032 phase 4 denied");
            allowed = without;
        }

        // 3. Cio' che diventa negato si elenca.
        New final = new(allowed, zones, old);
        foreach (IGrouping<(string ServiceId, string Reason), Scenario> lost in before
            .Where(scenario => !final.Admits(Find(services, scenario), scenario))
            .GroupBy(scenario => (scenario.ServiceId, Reason: final.Denial(Find(services, scenario), scenario) ?? string.Empty))
            .OrderBy(group => group.Key.ServiceId, StringComparer.Ordinal))
        {
            string classes = string.Join(", ", lost.Select(scenario => scenario.Class).Distinct().Order().Select(value => value.ToWire()));
            profile.Changes.Add(lost.Key.ServiceId + " is no longer used for " + classes + ": " + lost.Key.Reason);
        }

        // 4. Scrittura.
        WriteProfile(profile, constraints, zonesNode, maxZoneNode, zones, allowed, old);
    }

    private static void WriteProfile(
        ZoneConversionProfile profile,
        JsonObject constraints,
        JsonObject zonesNode,
        JsonObject maxZoneNode,
        IReadOnlyDictionary<DataClass, TrustZone> zones,
        IReadOnlyList<string> allowed,
        OldRules.Profile old)
    {
        foreach (string name in ProfileFields)
        {
            if (constraints[name] is JsonNode node)
            {
                string was = Describe(node);
                constraints.Remove(name);
                profile.Changes.Add(name + " removed (was " + was + "): " + Why(name));
            }
        }

        if (zonesNode["perimeter"] is JsonNode perimeter)
        {
            string was = Describe(perimeter);
            zonesNode.Remove("perimeter");
            profile.Changes.Add("zones.perimeter removed (was " + was + "): the perimeter is the same for every mode: this "
                + "computer, the home network and your own server in the EU (ADR-032)");
        }

        bool zonesChanged = false;

        foreach (DataClass dataClass in PolicyZones.DeclaredClasses)
        {
            string wire = zones[dataClass].ToWire();

            if (!string.Equals(maxZoneNode[dataClass.ToWire()]?.GetValue<string>(), wire, StringComparison.Ordinal))
            {
                maxZoneNode[dataClass.ToWire()] = wire;
                zonesChanged = true;
            }
        }

        if (!old.AllowedServiceIds.SequenceEqual(allowed, StringComparer.Ordinal))
        {
            constraints["allowedServiceIds"] = new JsonArray([.. allowed.Select(id => (JsonNode)JsonValue.Create(id)!)]);
        }

        if (profile.Changes.Count == 0 && !zonesChanged)
        {
            return;
        }

        profile.Zones = new PolicyZones(
            new Dictionary<DataClass, TrustZone>(zones),
            old.Zones.ConfirmOutside,
            old.Zones.AttachmentsOutsidePerimeter);

        profile.Changes.Add("zones.maxZone " + string.Join(", ", PolicyZones.DeclaredClasses.Select(dataClass =>
            dataClass.ToWire() + ": " + zones[dataClass].ToWire())) + ": the zones alone decide where data may go (ADR-032); "
            + "nothing this mode denied is allowed now");
    }

    private static string Why(string field) => field switch
    {
        "allowedInferencePlacements" => "the zone of each class decides where data may go (ADR-032)",
        "allowedAuthModes" => "a sign-in method does not make a service less safe; the zones decide where data may go and "
            + "limits.maxCostPerRequest caps what one request may cost (ADR-032)",
        "allowApiBilling" => "set limits.maxCostPerRequest in the configuration to cap what one request may cost (ADR-032)",
        "requiredControls" => "a chat must still receive only authorized content (closed_payload) and an agent the controls "
            + "for what the turn allows it; a service on this computer or the home network counts as local only with "
            + "local_inference confirmed (ADR-032)",
        "requiredRegions" => "your own server in the EU is the private_eu zone; a third party is decided by its zone (ADR-032)",
        _ => "the form before zones was converted to zones first (ADR-029, ADR-032)",
    };

    // ── registro ──────────────────────────────────────────────────────────────

    private static void StripService(JsonObject service, DateTimeOffset now, List<string> changes)
    {
        string id = service["id"]?.GetValue<string>() ?? "unknown";

        if (service["dataHandling"] is JsonObject handling)
        {
            string classes = handling["allowedClasses"] is JsonArray list ? Describe(list) : "none";
            service.Remove("dataHandling");
            changes.Add(id + ": dataHandling removed (allowed classes " + classes + "): the zones of each mode decide where data "
                + "may go; a mode that relied on it was narrowed, see its lines (ADR-032)");
        }
        else
        {
            service.Remove("dataHandling");
        }

        if (service.ContainsKey("evidenceExpiresAt"))
        {
            JsonNode? evidence = service["evidenceExpiresAt"];
            string placement = service["inferencePlacement"]?.GetValue<string>() ?? string.Empty;
            service.Remove("evidenceExpiresAt");
            changes.Add(evidence is null
                ? id + ": evidenceExpiresAt removed (it was empty, ADR-032)"
                : id + ": evidenceExpiresAt removed (was " + Describe(evidence) + "): "
                    + (placement == Placement.PrivateRemote.ToWire()
                        ? "your own server counts as private_eu from its declared regions alone"
                        : "the zone of a third party does not depend on it")
                    + "; no evidence expires any more (ADR-032)");
        }

        if (service.ContainsKey("unverified"))
        {
            int count = service["unverified"] is JsonArray notes ? notes.Count : 0;
            service.Remove("unverified");
            changes.Add(id + ": unverified removed (" + count.ToString(CultureInfo.InvariantCulture) + " notes): what the router "
                + "cannot see is a confirmation in the setup, and enabling a service is the operator's choice (ADR-032)");
        }

        if (service["attestations"] is not JsonObject attestations)
        {
            return;
        }

        foreach (string control in attestations.Select(pair => pair.Key).ToArray())
        {
            if (attestations[control] is not JsonObject entry)
            {
                continue;
            }

            PropertyAttestation attestation = Attestation(entry);

            if (attestation.IsValidAt(now) && !OldRules.AttestationValid(attestation, now))
            {
                attestations.Remove(control);
                changes.Add(id + ": attestations/" + control + " removed: "
                    + (attestation.ExpiresAt is DateTimeOffset expiry
                        ? "it expired on " + expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : "it had no expiry date")
                    + ", so it counted for nothing; confirmations no longer expire, confirm it again in the setup if it still "
                    + "holds (ADR-032)");
            }
        }

        if (attestations.Count == 0)
        {
            service.Remove("attestations");
        }
    }

    // ── scenari ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Una combinazione da confrontare: servizio, classe, capacita' del turno, allegati, conferma dell'uscita, e se la
    /// sonda ha gia' osservato cio' che sa osservare.
    /// </summary>
    private readonly record struct Scenario(
        string ServiceId,
        DataClass Class,
        string Capabilities,
        bool Attachments,
        bool Confirmed,
        bool Observed)
    {
        public IReadOnlySet<Capability> CapabilitySet =>
            new HashSet<Capability>(Capabilities.Split('+').Select(WireEnum.Parse<Capability>));
    }

    private static IEnumerable<Scenario> Scenarios(IReadOnlyList<Service> services)
    {
        foreach (Service service in services.Where(item => item.Answers))
        {
            foreach (string capabilities in CapabilitySets(service.Capability))
            {
                foreach (DataClass dataClass in AllClasses)
                {
                    foreach (bool attachments in new[] { false, true })
                    {
                        foreach (bool confirmed in new[] { false, true })
                        {
                            foreach (bool observed in new[] { false, true })
                            {
                                yield return new Scenario(service.Id, dataClass, capabilities, attachments, confirmed, observed);
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Le capacita' che un turno puo' chiedere a un servizio: una chat solo la chat; un agente lettura del codice, con o
    /// senza scrittura e comandi.
    /// </summary>
    private static IEnumerable<string> CapabilitySets(ServiceCapability capability) =>
        capability is ServiceCapability.Chat
            ? [Capability.Chat.ToWire()]
            :
            [
                Capability.CodeRead.ToWire(),
                Capability.CodeRead.ToWire() + "+" + Capability.FileWrite.ToWire(),
                Capability.CodeRead.ToWire() + "+" + Capability.Shell.ToWire(),
                Capability.CodeRead.ToWire() + "+" + Capability.FileWrite.ToWire() + "+" + Capability.Shell.ToWire(),
            ];

    private static Service Find(IReadOnlyList<Service> services, Scenario scenario) =>
        services.First(service => string.Equals(service.Id, scenario.ServiceId, StringComparison.Ordinal));

    private static TrustZone NewZone(Service service, bool observed) =>
        ServiceZones.Of(service.Placement, service.Effective(observed), service.Regions);

    private static TrustZone Min(TrustZone first, TrustZone second) => first < second ? first : second;

    private static bool CapabilitiesAdmit(Service service, IReadOnlySet<Capability> allowedCapabilities, Scenario scenario)
    {
        IReadOnlySet<Capability> required = scenario.CapabilitySet;

        return service.Capability == CapabilityRouting.ForRequest(required)
            && required.All(allowedCapabilities.Contains)
            && required.All(service.Capabilities.Contains);
    }

    // ── servizio ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Un servizio del registro nella forma di prima, con cio' che serve a entrambe le regole. I controlli effettivi sono
    /// gli stessi per le due: le conferme che prima non contavano e dopo conterebbero si tolgono dal registro.
    /// </summary>
    private sealed record Service(
        string Id,
        ServiceCapability Capability,
        AdapterKind Adapter,
        AuthMode AuthMode,
        Placement Placement,
        IReadOnlySet<Capability> Capabilities,
        IReadOnlySet<Control> Controls,
        IReadOnlySet<ProcessingRegion> Regions,
        DateTimeOffset? EvidenceExpiresAt,
        IReadOnlySet<DataClass>? HandlingClasses,
        bool HandlingValid,
        IReadOnlySet<Control> Attested)
    {
        public bool Answers => Capability is ServiceCapability.Chat or ServiceCapability.CodeAgent;

        /// <summary>Controlli effettivi: dichiarati ∩ (osservati dalla sonda ∪ confermati).</summary>
        public IReadOnlySet<Control> Effective(bool observed)
        {
            HashSet<Control> known = [.. Attested];

            if (observed)
            {
                known.UnionWith(ProbeObservations.Controls(Adapter));
            }

            HashSet<Control> effective = [.. Controls];
            effective.IntersectWith(known);
            return effective;
        }

        public static Service? Parse(JsonObject entry, DateTimeOffset now)
        {
            if (entry["id"]?.GetValue<string>() is not string id
                || !WireEnum.TryParse(entry["capability"]?.GetValue<string>() ?? string.Empty, out ServiceCapability capability)
                || !WireEnum.TryParse(entry["adapter"]?.GetValue<string>() ?? string.Empty, out AdapterKind adapter)
                || !WireEnum.TryParse(entry["inferencePlacement"]?.GetValue<string>() ?? string.Empty, out Placement placement))
            {
                return null;
            }

            AuthMode auth = WireEnum.TryParse(entry["authMode"]?.GetValue<string>() ?? string.Empty, out AuthMode parsed) ? parsed : AuthMode.None;

            HashSet<Control> attested = [];

            if (entry["attestations"] is JsonObject attestations)
            {
                foreach ((string key, JsonNode? value) in attestations)
                {
                    if (value is JsonObject item && WireEnum.TryParse(key, out Control control)
                        && OldRules.AttestationValid(Attestation(item), now))
                    {
                        attested.Add(control);
                    }
                }
            }

            IReadOnlySet<DataClass>? handlingClasses = null;
            bool handlingValid = false;

            if (entry["dataHandling"] is JsonObject handling)
            {
                handlingClasses = EnumSet<DataClass>(handling["allowedClasses"]);
                handlingValid = handling["attestation"] is JsonObject declaration && OldRules.HandlingValid(Attestation(declaration), now);
            }

            return new Service(
                id,
                capability,
                adapter,
                auth,
                placement,
                EnumSet<Capability>(entry["capabilities"]),
                EnumSet<Control>(entry["controls"]),
                new HashSet<ProcessingRegion>((entry["processingRegions"] as JsonArray ?? [])
                    .Select(item => item?.GetValue<string>())
                    .OfType<string>()
                    .Select(PA.Router.Abstractions.Contracts.Regions.Parse)),
                DateOf(entry["evidenceExpiresAt"]),
                handlingClasses,
                handlingValid,
                attested);
        }
    }

    // ── regole nuove ──────────────────────────────────────────────────────────

    /// <summary>
    /// Le regole della fase 4, composte dalle stesse funzioni del valutatore: elenco dei servizi (vuoto: tutti),
    /// capacita', controlli richiesti dal tipo di servizio, zona.
    /// </summary>
    private sealed class New(IReadOnlyList<string> allowed, IReadOnlyDictionary<DataClass, TrustZone> zones, OldRules.Profile profile)
    {
        private readonly HashSet<string> allowedSet = new(allowed, StringComparer.Ordinal);

        private readonly PolicyZones policyZones = new(
            new Dictionary<DataClass, TrustZone>(zones),
            profile.Zones.ConfirmOutside,
            profile.Zones.AttachmentsOutsidePerimeter);

        public bool Admits(Service service, Scenario scenario) => Denial(service, scenario) is null;

        public string? Denial(Service service, Scenario scenario)
        {
            if (!AllowedServices.Allows(allowedSet, service.Id))
            {
                return "it is not in the service list of this mode";
            }

            if (!CapabilitiesAdmit(service, profile.AllowedCapabilities, scenario))
            {
                return "this mode does not allow what the turn needs";
            }

            IReadOnlySet<Control> effective = service.Effective(scenario.Observed);
            Control[] missing = [.. AdmissionControls.For(service.Capability, scenario.CapabilitySet).Where(control => !effective.Contains(control))];

            if (missing.Length > 0)
            {
                return "it lacks " + string.Join(", ", missing.Select(control => control.ToWire()).Order(StringComparer.Ordinal));
            }

            TrustZone zone = ServiceZones.Of(service.Placement, effective, service.Regions);
            PolicyReason? reason = ZoneRule.Evaluate(
                policyZones, scenario.Class, ScanCertainty.Complete, zone, service.Placement, scenario.Attachments, scenario.Confirmed);

            return reason switch
            {
                null => null,
                _ when ServiceZones.LocalButUnconfirmed(service.Placement, effective) =>
                    "local_inference is not confirmed, so it counts as a third party",
                PolicyReason.ExternalConfirmationRequired => "it is outside the perimeter and this class needs a confirmation",
                _ when scenario.Attachments && zone <= policyZones.For(scenario.Class) => "attachments stay inside the perimeter",
                _ => "its zone " + zone.ToWire() + " is beyond " + policyZones.For(scenario.Class).ToWire(),
            };
        }
    }

    // ── regole prima della fase 4, usate solo per convertire ──────────────────

    /// <summary>
    /// **Regole prima della fase 4 di ADR-032, usate solo per convertire.** Copia privata dei controlli del valutatore
    /// com'era al commit <c>abe9075</c> (elenco, capacita', collocazione, autenticazione e costi API, controlli richiesti,
    /// regioni con evidenza, <c>dataHandling</c>, zona con perimetro del profilo), limitata a cio' che dipende dalla
    /// configurazione: disponibilita', sonda, quota, budget, costo e finestra valgono uguale prima e dopo e non si
    /// confrontano. Non usarla per decidere un turno. Le prove la confrontano con le decisioni registrate del valutatore di
    /// prima.
    /// </summary>
    private static class OldRules
    {
        /// <summary>Perimetro di un profilo a zone che non lo dichiarava: la macchina e la sua rete.</summary>
        private static readonly IReadOnlySet<TrustZone> DefaultPerimeter = new HashSet<TrustZone> { TrustZone.SameHost, TrustZone.Lan };

        /// <summary>Attestazione valida prima della fase 4: con scadenza, non scaduta.</summary>
        public static bool AttestationValid(PropertyAttestation attestation, DateTimeOffset now) =>
            attestation.IsValidAt(now)
            && attestation.ObservedAt is DateTimeOffset observed
            && attestation.ExpiresAt is DateTimeOffset expiry
            && observed < expiry
            && now < expiry;

        /// <summary><c>ServiceDataHandling.Allows</c> senza la classe: dichiarazione completa e non scaduta.</summary>
        public static bool HandlingValid(PropertyAttestation attestation, DateTimeOffset now) => AttestationValid(attestation, now);

        public static bool Admits(Profile profile, Service service, Scenario scenario)
        {
            // 3. Elenco: nella forma di prima un elenco vuoto negava tutto.
            if (profile.IsOldForm
                ? !profile.AllowedServiceIds.Contains(service.Id, StringComparer.Ordinal)
                : !AllowedServices.Allows(new HashSet<string>(profile.AllowedServiceIds, StringComparer.Ordinal), service.Id))
            {
                return false;
            }

            if (!CapabilitiesAdmit(service, profile.AllowedCapabilities, scenario))
            {
                return false;
            }

            // 3. Autenticazione e costi API.
            if (profile.AuthModes is { } auth && !auth.Contains(service.AuthMode))
            {
                return false;
            }

            if (service.AuthMode is AuthMode.ApiKey && profile.ApiBilling is false)
            {
                return false;
            }

            // 4. Collocazione.
            if (profile.Placements is { } placements && !placements.Contains(service.Placement))
            {
                return false;
            }

            // 5. Controlli richiesti e regioni.
            IReadOnlySet<Control> effective = service.Effective(scenario.Observed);
            IReadOnlySet<Control> required = profile.RequiredControls ?? AdmissionControls.For(service.Capability, scenario.CapabilitySet);

            if (!required.IsSubsetOf(effective))
            {
                return false;
            }

            if (profile.RequiredRegions.Count > 0)
            {
                IReadOnlySet<ProcessingRegion> declaredRegions = Regions.Expand(service.Regions);

                if (declaredRegions.Count == 0
                    || !declaredRegions.IsSubsetOf(profile.RequiredRegions)
                    || service.EvidenceExpiresAt is not DateTimeOffset expiry
                    || expiry <= profile.Now)
                {
                    return false;
                }
            }

            // 7. dataHandling del servizio.
            if (service.HandlingClasses is { } classes && !(classes.Contains(scenario.Class) && service.HandlingValid))
            {
                return false;
            }

            // 7. Zona, con la derivazione e il perimetro di prima.
            TrustZone zone = service.Placement switch
            {
                Placement.Loopback => TrustZone.SameHost,
                Placement.Lan => TrustZone.Lan,
                Placement.PrivateRemote => ServiceZones.InEuropeanUnion(service.Regions)
                    && service.EvidenceExpiresAt is DateTimeOffset evidence && evidence > profile.Now
                    ? TrustZone.PrivateEu
                    : TrustZone.ThirdParty,
                _ => effective.Contains(Control.ProcessingAgreement) ? TrustZone.ThirdPartyDpa : TrustZone.ThirdParty,
            };

            if (zone > profile.Zones.For(scenario.Class))
            {
                return false;
            }

            if (profile.Perimeter.Contains(zone))
            {
                return true;
            }

            if (scenario.Attachments && !profile.Zones.AttachmentsOutsidePerimeter)
            {
                return false;
            }

            return !profile.Zones.ConfirmOutside.Contains(scenario.Class) || scenario.Confirmed;
        }

        /// <summary>Un profilo letto nella forma di prima; i campi assenti non restringono.</summary>
        public sealed record Profile(
            bool IsOldForm,
            IReadOnlyList<string> AllowedServiceIds,
            IReadOnlySet<AuthMode>? AuthModes,
            bool? ApiBilling,
            IReadOnlySet<Placement>? Placements,
            IReadOnlySet<Control>? RequiredControls,
            IReadOnlySet<ProcessingRegion> RequiredRegions,
            IReadOnlySet<Capability> AllowedCapabilities,
            PolicyZones Zones,
            IReadOnlySet<TrustZone> Perimeter,
            DateTimeOffset Now)
        {
            public static Profile Parse(JsonObject constraints, DateTimeOffset now)
            {
                bool oldForm = HasOldFields(constraints);
                JsonObject zones = (JsonObject)constraints["zones"]!;
                JsonObject maxZone = (JsonObject)zones["maxZone"]!;

                Dictionary<DataClass, TrustZone> limits = [];

                foreach (DataClass dataClass in PolicyZones.DeclaredClasses)
                {
                    if (maxZone[dataClass.ToWire()]?.GetValue<string>() is string wire && WireEnum.TryParse(wire, out TrustZone zone))
                    {
                        limits[dataClass] = zone;
                    }
                }

                IReadOnlySet<TrustZone> perimeter = zones["perimeter"] is JsonArray declaredPerimeter
                    ? EnumSet<TrustZone>(declaredPerimeter)
                    : oldForm ? DefaultPerimeter : PolicyZones.FixedPerimeter;

                return new Profile(
                    oldForm,
                    [.. (constraints["allowedServiceIds"] as JsonArray ?? []).Select(item => item?.GetValue<string>()).OfType<string>()],
                    constraints.ContainsKey("allowedAuthModes") ? EnumSet<AuthMode>(constraints["allowedAuthModes"]) : null,
                    constraints["allowApiBilling"] is JsonValue billing && billing.TryGetValue(out bool value) ? value : null,
                    constraints.ContainsKey("allowedInferencePlacements") ? EnumSet<Placement>(constraints["allowedInferencePlacements"]) : null,
                    constraints.ContainsKey("requiredControls") ? EnumSet<Control>(constraints["requiredControls"]) : null,
                    Regions.Expand(new HashSet<ProcessingRegion>((constraints["requiredRegions"] as JsonArray ?? [])
                        .Select(item => item?.GetValue<string>())
                        .OfType<string>()
                        .Select(Regions.Parse))),
                    EnumSet<Capability>(constraints["allowedCapabilities"]),
                    new PolicyZones(limits, EnumSet<DataClass>(zones["confirmOutside"]),
                        zones["attachmentsOutsidePerimeter"] is JsonValue attachments && attachments.TryGetValue(out bool outside) && outside),
                    perimeter,
                    now);
            }
        }
    }

    // ── utilita' ──────────────────────────────────────────────────────────────

    private static bool HasOldFields(JsonObject constraints) =>
        ProfileFields.Any(constraints.ContainsKey)
        || (constraints["zones"] is JsonObject zones && zones.ContainsKey("perimeter"));

    private static PropertyAttestation Attestation(JsonObject entry) =>
        new(
            entry["by"]?.GetValue<string>() ?? string.Empty,
            DateOf(entry["observedAt"]),
            DateOf(entry["expiresAt"]),
            entry["note"] is JsonValue note && note.TryGetValue(out string? text) ? text : null);

    private static DateTimeOffset? DateOf(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out DateTimeOffset instant))
        {
            return instant;
        }

        return value.TryGetValue(out string? text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private static HashSet<T> EnumSet<T>(JsonNode? node)
        where T : struct, Enum
    {
        HashSet<T> set = [];

        foreach (JsonNode? item in node as JsonArray ?? [])
        {
            if (item is JsonValue value && value.TryGetValue(out string? wire) && WireEnum.TryParse(wire, out T parsed))
            {
                set.Add(parsed);
            }
        }

        return set;
    }

    private static string Describe(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray list => list.Count == 0 ? "empty" : string.Join(", ", list.Select(item => item?.ToString() ?? "null")),
        JsonValue value when value.TryGetValue(out string? text) => text,
        _ => node.ToJsonString(),
    };
}
