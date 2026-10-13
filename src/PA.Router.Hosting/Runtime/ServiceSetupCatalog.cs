using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Policy;
using PA.Router.Registry.Loading;
using PA.Router.Registry.Probing;

namespace PA.Router.Hosting.Runtime;

/// <summary>Campo del setup descritto dal motore, non dal terminale.</summary>
public sealed record ServiceSetupField(string Key, string Label, bool Advanced = false);

/// <summary>Requisiti di configurazione degli adapter installati.</summary>
public sealed record ServiceSetupDescriptor(string Name, IReadOnlyList<ServiceSetupField> Fields, bool DiscoverModels);

/// <summary>Catalogo dei requisiti, separato dalla resa; non registra nuovi adapter.</summary>
public static class ServiceSetupCatalog
{
    private static readonly ServiceSetupField[] HttpFields =
    [new("endpoint", "Indirizzo del servizio"), new("model", "Modello"), new("secretRef", "Nome credenziale (valore mascherato)", true)];
    private static readonly ServiceSetupField[] ProcessFields = [new("executable", "Programma installato (percorso assoluto)")];

    /// <summary>Setup grouping only: credentials identify distinct remote connections.</summary>
    public static string InstanceKey(ServiceDescriptor service) => service.Endpoint is null ? service.Id
        : $"{service.Adapter}|{service.Capability}|{service.Endpoint}|{service.AuthMode}|{service.SecretRef}";

    /// <summary>
    /// L'impronta che il gate della memoria confrontera' con quella del provider (`MemoryEmbeddingGate`): fornitore,
    /// modello, dimensione e versione del preprocessing. E' **derivata** da cio' che l'operatore ha scelto, quindi
    /// il setup la compila: pretendere che la scrivesse a mano faceva spegnere il servizio a ogni salvataggio, con un
    /// messaggio che nessuno sapeva come soddisfare. La stessa forma la produce `OllamaEmbeddingProvider`, una volta.
    /// </summary>
    public static string EmbeddingFingerprint(string model, int dimension) =>
        new PA.Router.Embeddings.Ollama.OllamaEmbeddingOptions(new Uri("http://127.0.0.1:11434"), model, dimension).Fingerprint.ToString();

    /// <summary>
    /// Vero per i servizi che **rispondono a una richiesta** — chat e agenti — e passano quindi dal valutatore della
    /// policy con i controlli che il profilo pretende. Tutti gli altri sono **ausiliari**: embedding, rilevatori,
    /// classificatore. Lavorano per il router, non per la richiesta, e li autorizza un gate proprio
    /// (`MemoryEmbeddingGate`: `allowLocalMemoryProcessing`, impronta approvata, classe del testo — S11), non
    /// `requiredControls`. Giudicarli con le regole delle chat diceva «non utilizzabile con il tuo uso» di un
    /// servizio di embedding che il profilo ammette e usa.
    /// </summary>
    public static bool AnswersRequests(ServiceCapability capability) =>
        capability is ServiceCapability.Chat or ServiceCapability.CodeAgent;

    /// <summary>
    /// Controlli che la probe dell'adapter osserva da sola quando il servizio risponde. Serve al setup per non
    /// chiedere all'operatore cio' che il router vede da se', anche quando la verifica non e' ancora riuscita: un
    /// Ollama spento non rende <c>closed_payload</c> una domanda per chi configura. L'elenco sta in
    /// <see cref="ProbeObservations"/>, che usa anche la conversione della fase 4 di ADR-032.
    /// </summary>
    public static IReadOnlySet<Control> ObservedByProbe(AdapterKind adapter) => ProbeObservations.Controls(adapter);

    /// <summary>
    /// Garanzie che servono al servizio per fare il suo lavoro (ADR-032, fase 4), cioe' le **conferme** che il setup
    /// chiede quando la sonda non le vede: una chat riceve solo il contenuto autorizzato (<c>closed_payload</c>); un
    /// agente tiene il recinto per cio' che dichiara di saper fare (<see cref="AdmissionControls"/>); un servizio su
    /// questo computer o in casa conta come locale solo con l'elaborazione locale confermata (<c>local_inference</c>),
    /// altrimenti vale come un terzo. L'accordo di trattamento di un terzo non e' qui: senza, il terzo riceve solo cio'
    /// che le zone gli lasciano, ed e' un avviso (<see cref="AgreementMatters"/>).
    /// </summary>
    public static IReadOnlySet<Control> Guarantees(ServiceDescriptor service)
    {
        ArgumentNullException.ThrowIfNull(service);

        HashSet<Control> guarantees = AnswersRequests(service.Capability)
            ? [.. AdmissionControls.For(service.Capability, service.Capabilities)]
            : [];

        if (AnswersRequests(service.Capability) && service.InferencePlacement is Placement.Loopback or Placement.Lan)
        {
            guarantees.Add(Control.LocalInference);
        }

        return guarantees;
    }

    /// <summary>
    /// Vero se il servizio puo' avere la garanzia: la dichiara, oppure e' l'elaborazione locale di un servizio su questo
    /// computer o in casa, che l'operatore dichiara e conferma insieme (<see cref="ServiceAttestations.Apply"/>) — come
    /// l'accordo di trattamento. Le garanzie di un agente, invece, le certifica l'adapter: non dichiarate, non si danno.
    /// </summary>
    public static bool Declares(ServiceDescriptor service, Control control)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.Controls.Contains(control)
            || (control is Control.LocalInference && service.InferencePlacement is Placement.Loopback or Placement.Lan);
    }

    /// <summary>Vero se l'accordo di trattamento cambia la zona del servizio: un terzo che risponde alle richieste.</summary>
    public static bool AgreementMatters(ServiceDescriptor service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return AnswersRequests(service.Capability) && ServiceDescriptor.IsThirdParty(service.InferencePlacement);
    }

    /// <summary>
    /// Controlli effettivi senza una sonda: dichiarati ∩ (osservabili dalla sonda ∪ confermati). E' cio' che il servizio
    /// avra' quando la sonda lo avra' visto rispondere; la resa lo usa per dire la zona che il servizio avra'.
    /// </summary>
    public static IReadOnlySet<Control> ExpectedControls(ServiceDescriptor service, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(service);

        HashSet<Control> known = [.. ObservedByProbe(service.Adapter)];

        foreach ((Control control, PropertyAttestation attestation) in service.Attestations)
        {
            if (attestation.IsValidAt(now))
            {
                known.Add(control);
            }
        }

        HashSet<Control> controls = [.. service.Controls];
        controls.IntersectWith(known);
        return controls;
    }

    /// <summary>Zona che il servizio avra' con i controlli attesi (<see cref="ExpectedControls"/>).</summary>
    public static TrustZone ExpectedZone(ServiceDescriptor service, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(service);
        return TrustZones.Of(service.InferencePlacement, ExpectedControls(service, now), service.ProcessingRegions);
    }

    /// <summary>Campi modificabili senza dare accesso a flag di bypass o abilitazioni premature.</summary>
    public static IReadOnlySet<string> EditableFields { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "endpoint", "model", "executable", "secretRef", "inferencePlacement",
        "processingRegions", "contextWindowTokens", "maxOutputTokens",
        "maxConcurrent", "dimension", "fingerprint", "authMode", "promptCaching", "nativeSession", "thinking", "keepAliveMinutes",
        // L'ordine dei modelli e le correzioni per dimensione sono scelte dell'operatore (ADR-027 J): le scrive il setup.
        "rank", "levels",
    };

    /// <summary>Descrittore per adapter: le CLI non chiedono endpoint o modello.</summary>
    public static ServiceSetupDescriptor Describe(ServiceDescriptor service) => service.Adapter switch
    {
        AdapterKind.Ollama => new("Ollama", [HttpFields[0], HttpFields[1]], true),
        AdapterKind.OllamaEmbed => new("Ollama embedding", [HttpFields[0], HttpFields[1], new("dimension", "Dimensione embedding"), new("fingerprint", "Fingerprint osservato")], true),
        AdapterKind.ClaudeCodeCli => new("Claude Code", ProcessFields, false),
        AdapterKind.CodexCli => new("Codex", ProcessFields, false),
        AdapterKind.OpenCodeCli => new("OpenCode", ProcessFields, false),
        AdapterKind.OnnxLocal => new("Rilevatore ONNX locale", [new("model", "Modello installato nella cartella modelli")], false),
        AdapterKind.Deterministic => new("Rilevatore deterministico", [], false),
        AdapterKind.OpenAiCompatible or AdapterKind.Anthropic => new(service.Adapter.ToWire(),
            [.. HttpFields, new("processingRegions", "Regioni di elaborazione")], true),
        _ when service.Executable is not null => new(service.Adapter.ToWire(), ProcessFields, false),
        _ => new(service.Adapter.ToWire(), [.. HttpFields, new("processingRegions", "Regioni di elaborazione")], false),
    };

    /// <summary>
    /// Compatibilita' dichiarativa: passa dal valutatore vero e non afferma disponibilita'.
    /// Le osservazioni restano da ottenere esplicitamente con doctor.
    /// </summary>
    public static IReadOnlyList<PolicyReason> Compatibility(ConfigurationSnapshot snapshot, ServiceDescriptor service, IClock clock) =>
        Compatibility(snapshot, service, clock, snapshot.Config.DefaultProfileId);

    /// <summary>Come <see cref="Compatibility(ConfigurationSnapshot, ServiceDescriptor, IClock)"/>, per un'etichetta scelta.</summary>
    public static IReadOnlyList<PolicyReason> Compatibility(ConfigurationSnapshot snapshot, ServiceDescriptor service, IClock clock, string profileId)
    {
        if (!AnswersRequests(service.Capability)) { return []; }
        CompiledPolicy policy = PolicyCompiler.Compile(snapshot.Profiles[profileId], snapshot.Config);
        // Dichiarativa: i controlli sono quelli dichiarati, come se ogni conferma fosse data. Le conferme mancanti le
        // chiede il setup a parte (Guarantees); qui si guarda se le regole dell'etichetta ammetterebbero il servizio.
        HashSet<Control> controls = [.. service.Controls];
        PolicyCandidate candidate = new(service.Id, service.Capability, service.Adapter, service.AuthMode,
            service.InferencePlacement, true, service.Capabilities, controls, service.ProcessingRegions,
            false, AuthStatus.Unknown, QuotaState.Unknown)
        {
            ContextWindowTokens = service.ContextWindowTokens,
        };
        // Un agente riceve un turno solo quando serve almeno leggere il codice (CapabilityRouting.ForRequest): lo si giudica
        // con la richiesta minima che lo raggiunge, cosi' un'etichetta che ammette la sola chat non «accetta» un agente.
        HashSet<Capability> minimum = service.Kind is TargetKind.Agent ? [Capability.CodeRead] : [];
        return [.. Evaluate(snapshot, service, policy, candidate, clock, minimum)
            .Where(reason => reason is not PolicyReason.ProbeStale and not PolicyReason.AuthUnavailable)];
    }

    /// <summary>
    /// Diagnosi per lo scenario dichiarato dal servizio e la classe minima del profilo.
    /// Usa fatti effettivi, non concede grant e non promette ammissibilita' per altre richieste.
    /// </summary>
    public static IReadOnlyList<PolicyReason> ObservedCompatibility(
        ConfigurationSnapshot snapshot, EffectiveService effective, IClock clock)
    {
        ServiceDescriptor service = effective.Service;
        if (!AnswersRequests(service.Capability)) { return []; }
        ProbeResult? probe = effective.Probe;
        CompiledPolicy policy = PolicyCompiler.Compile(snapshot.Profiles[snapshot.Config.DefaultProfileId], snapshot.Config);
        PolicyCandidate candidate = new(service.Id, service.Capability, service.Adapter, service.AuthMode,
            service.InferencePlacement, service.Enabled, effective.Capabilities, effective.Controls,
            service.ProcessingRegions, probe is not null && probe.IsFreshAt(clock.UtcNow),
            probe?.AuthStatus ?? AuthStatus.Unknown, probe?.QuotaState ?? QuotaState.Unknown)
        {
            ContextWindowTokens = effective.ContextWindowTokens,
        };
        return Evaluate(snapshot, service, policy, candidate, clock, service.Capabilities);
    }

    private static IReadOnlyList<PolicyReason> Evaluate(ConfigurationSnapshot snapshot, ServiceDescriptor service,
        CompiledPolicy policy, PolicyCandidate candidate, IClock clock, IReadOnlySet<Capability> capabilities)
    {
        PolicyRequest request = new(policy.Channel, Intent.Chat, service.Capability, capabilities,
            policy.MinimumDataClass, ScanCertainty.Complete, "setup", "setup", 0, 0,
            policy.RequiredObligations, snapshot.Digest);
        PolicyExplain evaluation = new PolicyEvaluator(clock).Evaluate(policy, request, [candidate]);
        // Il tetto di spesa (ADR-032, fase 5) si giudica richiesta per richiesta, non servizio per servizio: la stessa
        // domanda breve ci sta sotto e una lunga no. Qui non c'e' una richiesta, quindi non spegne ne' segna nessuno.
        return [.. evaluation.Decisions[0].Reasons.Where(reason => reason is not PolicyReason.CostLimit)];
    }

    /// <summary>Motivi dichiarativi che impediscono l'abilitazione; nessuna chiamata di rete.</summary>
    public static IReadOnlyList<string> Blockers(ConfigurationSnapshot snapshot, ServiceDescriptor service, IClock clock)
    {
        // Un servizio vale per tutto il router: lo si spegne solo se **nessuna** etichetta lo ammette (24 settembre 2026).
        // Prima si guardava la sola etichetta attiva, e un salvataggio con «Tutto su questo computer» attiva spegneva i
        // servizi remoti che l'etichetta di prova dell'operatore usava.
        List<string> reasons = snapshot.Profiles.Keys.Any(profileId => Compatibility(snapshot, service, clock, profileId).Count == 0)
            ? []
            : [.. Compatibility(snapshot, service, clock).Select(reason => reason.ToWire())];
        if (service.HasPlaceholders || service.Endpoint?.OriginalString.Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal) == true)
        {
            reasons.Add("Segnaposto da completare");
        }

        foreach ((Control control, PropertyAttestation attestation) in service.Attestations)
        {
            try { ServiceAttestations.Validate(attestation, clock); }
            catch (ArgumentException) { reasons.Add("Attestazione incompleta: " + control.ToWire()); }
        }

        return reasons;
    }
}
