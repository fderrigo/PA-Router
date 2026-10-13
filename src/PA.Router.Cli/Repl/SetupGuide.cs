using System.Globalization;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Output;
using PA.Router.Hosting.Runtime;
using PA.Router.Policy;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Repl;

/// <summary>
/// La guida sopra il configuratore (S112–S114): che cosa fare adesso, le conferme in un Invio, il percorso del
/// primo avvio.
///
/// E' un regista, non un secondo motore: ogni passo chiama le stesse schermate che si aprono dalla Home e scrive
/// nella stessa copia di lavoro. Non decide che cosa e' ammesso — lo legge da profilo, registro e valutatore — e
/// non firma nulla al posto dell'operatore: precompila, e la conferma resta sua (S30, S53).
/// </summary>
internal sealed partial class SetupScreen
{
    private const int GuideSteps = 6;

    /// <summary>Una cosa obbligatoria ancora da fare e la schermata che la risolve.</summary>
    private sealed record NextStep(string Label, Action Open);

    private readonly HashSet<string> unverifiable = new(StringComparer.Ordinal);
    /// <summary>Servizi per cui l'operatore ha detto di non avere l'accordo di trattamento, in questa sessione.</summary>
    private readonly HashSet<string> declinedAgreements = new(StringComparer.Ordinal);

    private string Operator() => SafeText.Inline((operatorName ?? (() => Environment.UserName))()).Trim();

    // Il prodotto gira con la globalizzazione invariante: un nome di mese uscirebbe in inglese in ogni lingua.
    private static string Day(DateTimeOffset instant) => instant.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private string ServiceTitle(ServiceDescriptor service)
    {
        string key = "guide.service." + service.Adapter.ToWire() + "." + service.InferencePlacement.ToWire();
        string name = Ui.Contains(key) ? Ui.Text(key) : SetupHelp.Name(service.Adapter.ToWire());
        return IsModel(service) && service.Kind == TargetKind.Llm ? name + " · " + service.Model : name;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Che cosa manca, e dove si risolve
    // ---------------------------------------------------------------------------------------------------------

    private IEnumerable<ServiceDescriptor> InPlay() => draft!.Health.Snapshot!.Registry.Services
        .Where(service => draft.Selected.Contains(service.Id));

    /// <summary>
    /// Controlli che solo l'operatore puo' confermare: richiesti dal profilo, dichiarati dal servizio, non ancora
    /// validi, e che nessuna probe osserverebbe da sola. Non dipende dall'esito di una verifica: che Ollama sia
    /// spento adesso non cambia cio' che l'operatore sa della propria rete.
    /// </summary>
    private Control[] PendingConfirmations(ServiceDescriptor service)
    {
        IReadOnlySet<Control> observable = ServiceSetupCatalog.ObservedByProbe(service.Adapter);
        return [.. CurrentControlGrades(service)
            .Where(pair => pair.Value is "attestazione scaduta" or "ignoto" && !observable.Contains(pair.Key))
            .Select(pair => pair.Key)];
    }

    /// <summary>Vero se resta qualcosa che solo una verifica riuscita puo' dire, e in questa sessione non c'e' stata.</summary>
    private bool NeedsVerification(ServiceDescriptor service)
    {
        if (unverifiable.Contains(service.Id)) { return false; }
        IReadOnlySet<Control> observable = ServiceSetupCatalog.ObservedByProbe(service.Adapter);
        return CurrentControlGrades(service).Any(pair => pair.Value == "ignoto" && observable.Contains(pair.Key));
    }

    private NextStep? NextFor(ServiceDescriptor service)
    {
        string name = ServiceTitle(service);
        string id = service.Id;
        if (service.Executable is not null && service.HasPlaceholders)
        {
            return new(Ui.Format("next.program {0}", name), () =>
            {
                string? program = ChooseExecutable(draft!.Service(id));
                if (program is not null) { ApplyField(id, "executable", program); }
            });
        }
        if (service.Endpoint is not null && service.Endpoint.OriginalString.Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal))
        {
            return new(Ui.Format("next.address {0}", name), () => Connection(id));
        }
        if (service.Kind == TargetKind.Llm && !IsModel(service))
        {
            return new(Ui.Format("next.models {0}", name), () =>
            {
                if (ServiceSetupCatalog.Describe(draft!.Service(id)).DiscoverModels) { ManageModels(id); return; }
                string? model = ChooseModel(draft.Service(id));
                if (model is not null) { ApplyField(id, "model", model); }
            });
        }
        if (FieldVisualState(service, ServiceStatus(id), "secret", false) == SetupVisualState.RequiredMissing && service.AuthMode == AuthMode.ApiKey)
        {
            return new(Ui.Format("next.key {0}", name), () => ConfigureCredential(id));
        }
        if (CurrentControlGrades(service).Values.Any(value => value == "non dichiarato"))
        {
            return new(Ui.Format("next.unusable {0}", name), () => ServiceRequirements(id));
        }
        if (!persistedEnabled.Contains(service.Id) && NeedsVerification(service))
        {
            return new(Ui.Format("next.verify {0}", name), () => { VerifyInstance(id); if (!NeedsVerification(draft!.Service(id))) { Confirmations(id); } });
        }
        if (PendingConfirmations(service).Length > 0)
        {
            return new(Ui.Format("next.confirm {0}", name), () => Confirmations(id));
        }
        return ServiceStatus(id).Severity is SectionSeverity.Missing or SectionSeverity.Error
            ? new(Ui.Format("next.review {0}", name), () => ConfigureService(id))
            : null;
    }

    /// <summary>Tutte le cose obbligatorie ancora aperte, nell'ordine in cui conviene farle.</summary>
    private List<NextStep> Remaining()
    {
        List<NextStep> steps = [];

        // Un profilo ancora nella forma precedente alle zone (ADR-029) funziona, ma non si puo' modificare dal setup:
        // la scheda mostra i campi della forma che il profilo usa, e le domande scrivono le zone. Si dice dove si chiude.
        if (draft!.Health.Snapshot?.Profiles.TryGetValue(draft.ProfileId, out PolicyDocument? current) == true
            && current.Constraints.Zones is null)
        {
            steps.Add(new(Ui.Format("next.zones {0}", ProfilePresentation.Name(draft.Health.Snapshot, draft.ProfileId)), () => GuideZones()));
        }

        // L'accordo di trattamento NON e' fra le cose obbligatorie (24 settembre 2026, prova dell'operatore): senza, il
        // servizio riceve solo le classi che le regole gli lasciano come terzo generico, e il router funziona lo stesso.
        // Un «no» e' una risposta: prima restava rosso per sempre e bloccava il setup. Ora e' un avviso nella Home
        // (`AgreementsToConfirm`).

        if (!HasSelectedAllowedService())
        {
            steps.Add(new(Ui.Text("next.choose"), () => { guideStep = Step(2); try { GuideServices(); } finally { guideStep = null; } }));
        }
        // Una voce per istanza: tre modelli dello stesso Ollama chiedono la stessa conferma una volta sola.
        HashSet<string> covered = new(StringComparer.Ordinal);
        foreach (var instance in InPlay().GroupBy(GroupKey))
        {
            NextStep? step = instance.Select(NextFor).FirstOrDefault(item => item is not null);
            if (step is null) { continue; }
            steps.Add(step);
            covered.UnionWith(instance.Select(item => item.Id));
        }

        // Cio' che il salvataggio spegnerebbe e' una cosa da fare, con il suo motivo. Senza questa riga un servizio
        // scelto ma non accendibile restava «scelto e non salvato»: la Home proponeva «Salva», il salvataggio lo spegneva
        // di nuovo, e il motivo non compariva mai.
        foreach ((string id, IReadOnlyList<string> reasons) in draft!.EnablementIssues().Where(pair => !covered.Contains(pair.Key)))
        {
            string blockedId = id;
            string reason = Friendly(reasons[0]);
            steps.Add(new(Ui.Format("next.blocked {0} {1}", ServiceTitle(draft.Service(id)), reason), () =>
            {
                Notice(reason);
                ConfigureService(blockedId);
            }));
        }
        return steps;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Verifica e conferme
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Verifica ogni modello scelto di un'istanza, non solo il primo: ciascuno ha la propria osservazione — che il
    /// server risponda non dice che quel modello ci sia. Prima se ne verificava uno, e subito dopo la guida chiedeva di
    /// verificare l'altro come se nulla fosse stato fatto.
    /// </summary>
    private void VerifyInstance(string id)
    {
        if (!draft!.Health.Snapshot!.Registry.Services.Any(service => service.Id == id)) { return; }
        ServiceDescriptor head = draft.Service(id);
        ServiceDescriptor[] models = [.. Instance(head).Where(item => draft.Selected.Contains(item.Id) && NeedsVerification(item))];
        bool ask = models.Any(AsksBeforeVerifying);
        for (int index = 0; index < models.Length && !interaction.Navigation.Pending; index++)
        {
            // Si chiede una volta sola per istanza: e' lo stesso server e la stessa chiave.
            if (!Verify(models[index].Id, ask: ask && index == 0)) { return; }
        }
    }

    /// <summary>
    /// Solo una verifica che esce dalla macchina chiede prima: interrogare un servizio di terzi usa la chiave
    /// dell'operatore. Guardare un programma installato o un Ollama locale non manda niente a nessuno.
    /// </summary>
    private static bool AsksBeforeVerifying(ServiceDescriptor service) => service.Endpoint is not null && ServiceDescriptor.LeavesMachine(service.InferencePlacement);

    /// <summary>Chiede al servizio se risponde. Nessuna generazione, nessun download; verso terzi solo dopo un si'.</summary>
    private bool Verify(string id, bool ask)
    {
        ServiceDescriptor service = draft!.Service(id);
        if (ask)
        {
            Frame(breadcrumb + " > " + Ui.Text("verify.title"), Ui.Text("verify.why"));
            if (interaction.Choose(Ui.Format("verify.question {0}", ServiceTitle(service)),
                [("yes", Ui.Text("verify.yes")), ("back", Ui.Text("verify.no"))]) != "yes") { return false; }
        }
        testResults.Remove(id);
        testedDocuments.Remove(id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            ProbeResult? probe = Test(id, timeout.Token);
            if (probe is null) { unverifiable.Add(id); Notice(Ui.Text("verify.unavailable")); return true; }
            // La stessa osservazione vale per i modelli della stessa istanza solo se l'ha fatta la loro probe:
            // qui si registra quella del servizio interrogato e di nessun altro.
            testResults[id] = probe;
            testedDocuments[id] = ProbeKey(id);
            Notice(probe.FailureCode is null ? Ui.Format("verify.ok {0}", ServiceTitle(service)) : Ui.Format("verify.failed {0}", ServiceTitle(service)));
            return probe.FailureCode is null;
        }
        catch (OperationCanceledException) { Notice(Ui.Text("Verifica interrotta: tempo massimo raggiunto.")); return false; }
        catch (PaRouterException) { Notice(Ui.Format("verify.failed {0}", ServiceTitle(service))); return false; }
    }

    /// <summary>
    /// Le conferme che il profilo pretende e che solo l'operatore puo' dare, in una schermata: una domanda, un Invio.
    /// Nome, data e scadenza sono proposti; chi conferma e' l'operatore, e puo' cambiarli o rifiutare.
    /// </summary>
    private bool Confirmations(string id)
    {
        ServiceDescriptor service = draft!.Service(id);
        string name = InstanceName(service);
        string path = Ui.Text("Setup > Servizi > ") + name + " > " + Ui.Text("confirm.title");
        Control[] pending = PendingConfirmations(service);
        if (pending.Length == 0) { return ControlsOverview(id); }

        string[] scope = [.. Instance(service).Where(item => draft.Selected.Contains(item.Id) || item.Id == id).Select(item => item.Id)];
        DateTimeOffset now = clock.UtcNow;
        string who = Operator();
        string[] covers = [.. scope.Select(item => draft.Service(item)).Where(IsModel).Select(item => item.Model!)];
        string covered = covers.Length > 0 && service.Kind == TargetKind.Llm ? " " + Ui.Format("confirm.scope {0}", string.Join(", ", covers)) : "";
        Frame(path, (pending.Length == 1 ? ControlText(pending[0], "why") : Ui.Format("confirm.many.why {0}", name))
            + " " + Ui.Format("confirm.signature {0} {1}", who, Day(now)) + covered);

        List<(string Id, string Label)> options = [];
        if (pending.Length > 1)
        {
            options.AddRange(pending.Select(control => ("#-" + control.ToWire(), "• " + ControlText(control, "statement"))));
        }
        options.Add(("yes", pending.Length == 1 ? Ui.Text("confirm.yes") : Ui.Format("confirm.yesAll {0}", pending.Length)));
        options.Add(("no", Ui.Format("confirm.no {0}", name)));
        options.Add(("edit", Ui.Text("confirm.edit")));
        if (pending.Length > 1) { options.Add(("each", Ui.Text("confirm.each"))); }

        // L'accordo di trattamento sta fra le conferme del servizio (ADR-032, fase 4), anche mentre altre aspettano.
        if (ServiceSetupCatalog.AgreementMatters(service)
            && !(service.Attestations.TryGetValue(Control.ProcessingAgreement, out PropertyAttestation? agreed) && agreed.IsValidAt(now)))
        {
            options.Add(("agreement", "[opzionale] " + Ui.Text("service.agreement")));
        }

        string? choice = interaction.Choose(pending.Length == 1 ? ControlText(pending[0], "question", name) : Ui.Format("confirm.many.question {0} {1}", name, pending.Length), options);
        if (choice == "agreement") { return ConfirmAgreement(id); }
        if (choice == "each") { Attest(id); return PendingConfirmations(draft.Service(id)).Length == 0; }
        if (choice is not ("yes" or "edit")) { return false; }

        // Una conferma non scade (ADR-032, fase 4): nome e data, e ogni anno un promemoria.
        PropertyAttestation? attestation = choice == "yes"
            ? new PropertyAttestation(who, now, null, null)
            : ReadAttestation(new PropertyAttestation(who, now, null, null));
        if (attestation is null) { return false; }
        try
        {
            ServiceAttestations.Validate(attestation, clock);
            foreach (Control control in pending) { draft.AttestServices(scope, control, attestation); }
        }
        catch (ArgumentException exception) { Notice(exception.Message); return false; }
        Notice(Ui.Format("confirm.done {0} {1}", attestation.By, Day(attestation.ObservedAt!.Value)));
        return true;
    }

    /// <summary>
    /// I controlli del servizio quando non resta niente da confermare: che cosa richiede il profilo, che cosa ha
    /// osservato il router, che cosa hai confermato tu — e la revoca di una conferma gia' data.
    ///
    /// Prima questa schermata diceva soltanto «niente da fare»: una conferma data un anno fa non si poteva piu'
    /// vedere ne' togliere, e chi la leggeva non sapeva se il controllo valesse per osservazione o per firma.
    /// Sono due cose diverse — la prima la rifa' il router a ogni verifica, la seconda la regge solo l'operatore —
    /// e una schermata che non le distingue nasconde proprio cio' che l'operatore ha promesso.
    ///
    /// La voce sparisce dal menu del servizio solo quando il profilo **non richiede alcun controllo**: li' non
    /// c'e' niente da mostrare, e quello e' l'unico caso in cui «non serve» e' vero.
    /// </summary>
    private bool ControlsOverview(string id)
    {
        ServiceDescriptor service = draft!.Service(id);
        string name = InstanceName(service);
        IReadOnlyDictionary<Control, string> grades = CurrentControlGrades(service);

        // L'accordo di trattamento sta fra le conferme del servizio (ADR-032, fase 4): non e' una garanzia che serve, e'
        // cio' che porta un servizio di terzi a «fornitore con accordo». Dato, si mostra e si revoca come le altre.
        bool agreement = ServiceSetupCatalog.AgreementMatters(service);
        bool agreed = service.Attestations.TryGetValue(Control.ProcessingAgreement, out PropertyAttestation? signed) && signed.IsValidAt(clock.UtcNow);
        if (agreement && agreed) { grades = new Dictionary<Control, string>(grades) { [Control.ProcessingAgreement] = "attestato" }; }
        if (grades.Count == 0 && !agreement) { Notice(Ui.Format("confirm.none {0}", name)); return true; }

        Frame(Ui.Text("Setup > Servizi > ") + name + " > " + Ui.Text("confirm.title"), Ui.Text("controls.why"));

        List<(string Id, string Label)> options = [.. grades.Select(pair => (pair.Key.ToWire(), ControlDescription(pair)))];
        if (agreement && !agreed) { options.Add(("agreement", "[opzionale] " + Ui.Text("service.agreement"))); }
        options.Add(("back", SetupHelp.Label("back")));

        string? choice = interaction.Choose(Ui.Format("controls.question {0}", name), options);
        if (choice is null or "back") { return true; }
        if (choice == "agreement") { _ = ConfirmAgreement(id); return true; }

        Control control = Enum.GetValues<Control>().Single(item => item.ToWire() == choice);
        string label = SetupHelp.Name(choice);
        switch (grades[control])
        {
            case "non dichiarato": Notice(Ui.Format("controls.undeclared {0}", label)); return true;
            case "osservato": Notice(Ui.Format("controls.observedOnly {0}", label)); return true;
            case not "attestato": Notice(Ui.Format("confirm.verifyFirst {0}", name)); return true;
        }

        string by = service.Attestations.GetValueOrDefault(control)?.By ?? Operator();
        if (interaction.Choose(Ui.Format("controls.revokeConfirm {0} {1}", label, by),
            [("yes", Ui.Text("controls.revokeYes")), ("no", Ui.Text("controls.revokeNo"))]) != "yes") { return true; }

        string[] scope = [.. Instance(service).Where(item => draft.Selected.Contains(item.Id) || item.Id == id).Select(item => item.Id)];
        draft.RevokeServices(scope, control);
        Notice(Ui.Format("controls.revoked {0}", label));
        return false;
    }

    private static string ControlText(Control control, string part, params object?[] arguments)
    {
        string key = "confirm." + control.ToWire() + "." + part + (part == "question" ? " {0}" : "");
        return Ui.Contains(key) ? Ui.Format(key, arguments)
            : part == "question" ? Ui.Format("confirm.generic.question {0}", SetupHelp.Name(control.ToWire()))
            : SetupHelp.Description(control.ToWire());
    }

    // ---------------------------------------------------------------------------------------------------------
    // La guida del primo avvio
    // ---------------------------------------------------------------------------------------------------------

    private static string Step(int number) => Ui.Format("guide.step {0} {1}", number, GuideSteps);

    /// <summary>Vero se la guida e' stata percorsa fino in fondo; falso se l'operatore ha scelto di fare da se' o e' uscito.</summary>
    private bool Guide(bool firstRun)
    {
        try
        {
            guideStep = Step(1);
            if (!GuideUse(firstRun) || interaction.Navigation.Pending) { return false; }
            GuideZones(offerKeep: true);
            if (interaction.Navigation.Pending) { return false; }
            guideStep = Step(2);
            if (!GuideServices() || interaction.Navigation.Pending) { return false; }
            guideStep = Step(3);
            foreach (string id in InstanceHeads())
            {
                // Programma, indirizzo, modelli, chiave: solo cio' che a questo servizio manca davvero. Un passo
                // lasciato a meta' non viene riproposto all'infinito: resta rosso, e lo ritrova «Prossima cosa da fare».
                string? previous = null;
                while (!interaction.Navigation.Pending && ConnectionStep(id) is { } step && step.Label != previous)
                {
                    previous = step.Label;
                    step.Open();
                }
            }
            if (interaction.Navigation.Pending) { return false; }
            guideStep = Step(4);
            foreach (string id in InstanceHeads())
            {
                if (interaction.Navigation.Pending) { return false; }
                ServiceDescriptor service = draft!.Service(id);
                // Una verifica non riuscita non impedisce le conferme che hanno gia' il loro posto nel registro:
                // che Ollama sia spento adesso non cambia cio' che l'operatore sa della propria rete.
                VerifyInstance(id);
                if (interaction.Navigation.Pending) { return false; }
                _ = Confirmations(id);
            }
            if (interaction.Navigation.Pending) { return false; }
            GuideOrder();
            if (interaction.Navigation.Pending) { return false; }
            GuideAnalyzer();
            if (interaction.Navigation.Pending) { return false; }
            guideStep = Step(5);
            GuideStorage();
            if (interaction.Navigation.Pending) { return false; }
            guideStep = Step(6);
            return GuideFinish();
        }
        finally { guideStep = null; }
    }

    private string[] InstanceHeads() => [.. InPlay().GroupBy(GroupKey).Select(group => group.First().Id)];

    /// <summary>Il prossimo passo di collegamento di un servizio; verifiche e conferme appartengono al passo dopo.</summary>
    private NextStep? ConnectionStep(string id)
    {
        if (!draft!.Health.Snapshot!.Registry.Services.Any(service => service.Id == id)) { return null; }
        ServiceDescriptor service = draft.Service(id);
        bool connected = !(service.Executable is not null && service.HasPlaceholders)
            && !(service.Endpoint is not null && service.Endpoint.OriginalString.Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal))
            && !(service.Kind == TargetKind.Llm && !IsModel(service))
            && !(service.AuthMode == AuthMode.ApiKey && FieldVisualState(service, ServiceStatus(id), "secret", false) == SetupVisualState.RequiredMissing);
        return connected ? null : NextFor(service);
    }

    private bool GuideUse(bool firstRun)
    {
        while (!interaction.Navigation.Pending)
        {
            Frame(Ui.Text("Setup > Uso principale"), Ui.Text("guide.use.why"));
            // Ogni modalita' della configurazione, nel suo ordine, con la descrizione calcolata dalle regole: il codice non
            // conosce nessuna modalita' (24 settembre 2026), e una modalita' nuova compare qui senza scrivere niente.
            ConfigurationSnapshot labels = draft!.Health.Snapshot!;
            List<(string Id, string Label)> options = [.. labels.Profiles.Keys.Select(id => (id, ProfilePresentation.Label(labels, id, clock)))];
            options.Add(("custom", Ui.Text("guide.use.custom")));
            options.Add(("guide-language", Ui.Text("guide.use.language")));
            string? use = interaction.Choose(Ui.Text("Come userai principalmente PA Router?"), options);
            if (use is null or "custom") { return false; }
            if (use == "guide-language") { Language(); continue; }
            if (use == draft.ProfileId) { return true; }
            if (firstRun) { draft.ChooseProfile(use); return true; }
            ConfirmProfile(use);
            if (draft.ProfileId == use) { return true; }
        }
        return false;
    }

    /// <summary>
    /// Che cosa collegare. Tutto cio' che il registro prevede compare: cio' che l'uso scelto non ammette o che su
    /// questa macchina non c'e' resta visibile, con il motivo, e non si puo' spuntare.
    /// </summary>
    private bool GuideServices()
    {
        // La ricerca parte da un Invio, non dal disegno della schermata: contatta Ollama su questa macchina e guarda
        // quali programmi esistono. Non avvia turni, non scarica modelli, non esce dalla macchina.
        Frame(Ui.Text("Setup > Servizi"), Ui.Text("guide.scan.why"));
        string? scan = interaction.Choose(Ui.Text("guide.scan.title"), [("scan", Ui.Text("guide.scan.yes")), ("skip", Ui.Text("guide.scan.skip"))]);
        if (scan is null) { return false; }
        bool look = scan == "scan";
        ConfigurationSnapshot snapshot = draft!.Health.Snapshot!;
        List<(string Id, string Label)> options = [];
        List<(string Id, string Label)> notes = [];
        HashSet<string> preselected = new(StringComparer.Ordinal);
        foreach (var group in snapshot.Registry.Services.Where(service => service.Capability is ServiceCapability.Chat or ServiceCapability.CodeAgent).GroupBy(GroupKey))
        {
            ServiceDescriptor service = group.First();
            string name = ServiceTitle(service with { Model = null });
            var reasons = ServiceSetupCatalog.Compatibility(snapshot, service, clock);
            if (!IsAllowedCandidate(service) || reasons.Count > 0)
            {
                string reason = !IsAllowedCandidate(service) ? Ui.Text("guide.services.notAllowed")
                    : Ui.Format("guide.services.unusable {0}", string.Join("; ", reasons.Select(item =>
                        Ui.Contains("guide.reason." + item.ToWire()) ? Ui.Text("guide.reason." + item.ToWire()) : Friendly(item.ToWire()))));
                notes.Add(("#-" + service.Id, "░ " + name + " — " + reason));
                continue;
            }
            string found;
            if (service.Executable is not null)
            {
                IReadOnlyList<string> programs = look ? SetupDiscovery.Executables(service) : [];
                if (look && programs.Count == 0 && service.HasPlaceholders)
                {
                    notes.Add(("#-" + service.Id, "░ " + name + " — " + Ui.Text("guide.services.notInstalled")));
                    continue;
                }
                found = Ui.Text(!look ? "guide.services.unchecked" : programs.Count > 0 ? "guide.services.installed" : "guide.services.configured");
            }
            else if (look && service.InferencePlacement == Placement.Loopback && ServiceSetupCatalog.Describe(service).DiscoverModels)
            {
                IReadOnlyList<string>? models = Discover(service);
                notice = "";
                if (models is not null) { observations[GroupKey(service)] = models; }
                found = models is null ? Ui.Text("guide.services.silent") : Ui.Format("guide.services.models {0}", models.Count);
            }
            else if (!look && service.InferencePlacement == Placement.Loopback) { found = Ui.Text("guide.services.unchecked"); }
            else { found = Ui.Text("guide.services.remote"); }
            options.Add((service.Id, name + " - " + found));
            if (group.Any(item => draft.Selected.Contains(item.Id))) { preselected.Add(service.Id); }
        }
        // La rete di casa si propone se le zone dell'etichetta ci lasciano arrivare almeno una classe (ADR-032, fase 4).
        PolicyZones labelZones = PolicyCompiler.Compile(snapshot.Profiles[draft.ProfileId], snapshot.Config).Zones;
        if (PolicyZones.DeclaredClasses.Any(dataClass => labelZones.For(dataClass) >= TrustZone.Lan)
            && snapshot.Registry.Services.Any(service => service.Adapter == AdapterKind.Ollama && IsAllowedCandidate(service)
                && ServiceSetupCatalog.Compatibility(snapshot, service, clock).Count == 0)
            && !snapshot.Registry.Services.Any(service => service.Adapter == AdapterKind.Ollama && service.InferencePlacement == Placement.Lan))
        {
            options.Add(("ollama-lan", Ui.Text("guide.service.ollama.lan") + " - " + Ui.Text("guide.services.lan")));
        }

        Frame(Ui.Text("Setup > Servizi"), Ui.Format("guide.services.why {0}", ProfilePresentation.Name(draft.Health.Snapshot, draft.ProfileId)));
        if (options.Count == 0)
        {
            _ = interaction.Choose(Ui.Text("guide.services.none"), [.. notes, ("back", SetupHelp.Label("back"))]);
            return false;
        }
        IReadOnlyList<string>? chosen = interaction.Many(Ui.Text("guide.services.title"), [.. options, .. notes], preselected);
        if (chosen is null) { return false; }
        chosen = [.. chosen.Where(id => !ISetupInteraction.IsHeading(id))];
        foreach ((string id, _) in options.Where(option => option.Id != "ollama-lan"))
        {
            bool wanted = chosen.Contains(id);
            ServiceDescriptor[] instance = Instance(draft.Service(id));
            if (wanted && !instance.Any(item => draft.Selected.Contains(item.Id))) { draft.Select(id, true); }
            if (!wanted) { foreach (ServiceDescriptor item in instance) { draft.Select(item.Id, false); } }
        }
        if (chosen.Contains("ollama-lan"))
        {
            string lan = draft.AddServiceFromTemplate(AdapterKind.Ollama, "ollama-rete");
            Connection(lan, Placement.Lan);
            if (draft.Service(lan).InferencePlacement == Placement.Lan) { draft.Select(lan, true); Admit([lan]); }
        }
        if (!InPlay().Any()) { Notice(Ui.Text("guide.services.nothing")); return false; }
        return true;
    }

    /// <summary>
    /// Ricordare la conversazione. Senza, ogni riga e' una richiesta a se': il router non puo' rimandare al servizio
    /// cio' che non ha conservato, e chi chiede «e poi?» riceve una risposta che non sa di che cosa si parla. E' una
    /// scelta di riservatezza dell'operatore, quindi si chiede: non si accende da sola.
    /// </summary>
    /// <summary>
    /// Dove possono andare i dati di questo uso (ADR-030, passo 1; ADR-029 per le regole).
    ///
    /// Si chiede cio' che solo l'operatore sa — fin dove i dati possono andare, se un nome da solo e' un dato stretto,
    /// se vuole autorizzare caso per caso, se gli allegati possono uscire, che cosa fare quando dentro il perimetro c'e'
    /// solo un modello debole — e si **ricava** il resto: il perimetro sono le zone fino a un proprio server in UE, e la
    /// zona di un servizio viene da collocazione e controlli, non da una domanda.
    ///
    /// Le risposte scrivono `zones` e `personalDataGrading` del profilo scelto: le stesse forme che la lettura verifica,
    /// nessuna forma intermedia.
    /// </summary>
    /// <param name="offerKeep">
    /// Vero nel primo avvio (24 settembre 2026): l'etichetta appena scelta ha gia' le sue regole, e le sette domande che
    /// seguono le ripetevano. Prima si mostrano le regole in chiaro e si chiede se vanno bene; si personalizza solo se lo
    /// si sceglie. Dalla Home si arriva qui per cambiarle, e le domande partono subito.
    /// </param>
    private void GuideZones(bool offerKeep = false)
    {
        if (offerKeep && draft!.Document("policy", draft.ProfileId)["constraints"]?["zones"] is JsonObject)
        {
            Frame(Ui.Text("Setup > Dove vanno i tuoi dati"), Ui.Format("guide.zones.keepWhy {0} {1}",
                ProfilePresentation.Name(draft.Health.Snapshot, draft.ProfileId),
                ProfilePresentation.Summary(ProfileFacts.Describe(draft.Health.Snapshot!, draft.ProfileId, clock))));

            string? keep = interaction.Choose(Ui.Text("guide.zones.keepTitle"),
            [
                ("keep", Ui.Text("guide.zones.keep")),
                ("custom", Ui.Text("guide.zones.custom")),
            ]);

            if (keep is null or "keep") { return; }
        }

        JsonObject constraints = draft!.Document("policy", draft.ProfileId)["constraints"]!.AsObject();
        JsonObject zones = constraints["zones"]?.AsObject().DeepClone().AsObject() ?? new JsonObject();
        // La modalita' delle applicazioni e' quella che l'host API usa (ADR-032, fase 2: il canale non e' piu' un campo del
        // profilo, lo da' il front end).
        bool chatbot = string.Equals(draft.ProfileId, draft.Health.Snapshot!.Config.Api.ServiceProfileId, StringComparison.Ordinal);

        // Una domanda per classe, con il nome della classe: pubblici, interni e personali, sensibili. Nessuna parola
        // nuova, e ogni risposta non puo' superare la precedente — le classi salgono, le zone scendono.
        string open = Zone(zones, DataClass.Public);
        string personal = Zone(zones, DataClass.Personal);
        string sensitive = Zone(zones, DataClass.Sensitive);

        Frame(Ui.Text("Setup > Dove vanno i tuoi dati"), Ui.Text("guide.zones.why"));
        string? answer = interaction.Choose(Ui.Text("guide.zones.public"), ZoneOptions(open));
        if (answer is null) { return; }
        open = answer;

        Frame(Ui.Text("Setup > Dove vanno i tuoi dati"), Ui.Text("guide.zones.personalWhy"));
        answer = interaction.Choose(Ui.Text("guide.zones.personal"), ZoneOptions(personal, open));
        if (answer is null) { return; }
        personal = answer;

        Frame(Ui.Text("Setup > Dove vanno i tuoi dati"), Ui.Text("guide.zones.sensitiveWhy"));
        answer = interaction.Choose(Ui.Text("guide.zones.sensitive"), ZoneOptions(sensitive, personal));
        if (answer is null) { return; }
        sensitive = answer;

        Frame(Ui.Text("Setup > Dove vanno i tuoi dati"), Ui.Text("guide.grading.why"));
        string? grading = interaction.Choose(Ui.Text("guide.grading.title"), [
            ("graded", Mark(constraints["personalDataGrading"]?.GetValue<string>() is not "strict") + Ui.Text("guide.grading.graded")),
            ("strict", Mark(constraints["personalDataGrading"]?.GetValue<string>() is "strict") + Ui.Text("guide.grading.strict"))]);
        if (grading is null) { return; }

        bool confirmed = zones["confirmOutside"] is JsonArray existing && existing.Count > 0;
        Frame(Ui.Text("Setup > Dove vanno i tuoi dati"), Ui.Text("guide.confirm.why"));
        string? confirm = interaction.Choose(Ui.Text("guide.confirm.title"), [
            ("no", Mark(!confirmed) + Ui.Text("guide.confirm.no")),
            ("yes", Mark(confirmed) + Ui.Text("guide.confirm.yes"))]);
        if (confirm is null) { return; }

        bool attachmentsOut = zones["attachmentsOutsidePerimeter"]?.GetValue<bool>() == true;
        Frame(Ui.Text("Setup > Dove vanno i tuoi dati"), Ui.Text("guide.attach.why"));
        string? attach = interaction.Choose(Ui.Text("guide.attach.title"), [
            ("no", Mark(!attachmentsOut) + Ui.Text("guide.attach.no")),
            ("yes", Mark(attachmentsOut) + Ui.Text("guide.attach.yes"))]);
        if (attach is null) { return; }

        bool declares = zones["weakPerimeter"]?.GetValue<string>() is "declare";
        Frame(Ui.Text("Setup > Dove vanno i tuoi dati"), Ui.Text(chatbot ? "guide.weak.apiWhy" : "guide.weak.why"));
        string? weak = interaction.Choose(Ui.Text(chatbot ? "guide.weak.apiTitle" : "guide.weak.title"), [
            ("declare", Mark(declares) + Ui.Text(chatbot ? "guide.weak.apiDeclare" : "guide.weak.declare")),
            ("deny", Mark(!declares) + Ui.Text(chatbot ? "guide.weak.apiDeny" : "guide.weak.deny"))]);
        if (weak is null) { return; }

        // Il perimetro non si chiede: sono le zone fino a un proprio server in UE (ADR-030, «ricavata»).
        JsonObject maxZone = new()
        {
            [DataClass.Public.ToWire()] = open,
            [DataClass.Internal.ToWire()] = personal,
            [DataClass.Personal.ToWire()] = personal,
            [DataClass.Sensitive.ToWire()] = sensitive,
        };

        JsonObject written = new()
        {
            ["maxZone"] = maxZone,
            ["confirmOutside"] = confirm == "yes" ? new JsonArray(DataClass.Sensitive.ToWire()) : new JsonArray(),
            ["attachmentsOutsidePerimeter"] = attach == "yes",
            ["weakPerimeter"] = weak,
        };

        // Gradazione e zone in una scrittura sola. Il perimetro e' fisso (ADR-032, fase 4): non si scrive.
        foreach (ValidationIssue issue in draft.EditPolicyFields(new Dictionary<string, JsonNode?>
        {
            ["personalDataGrading"] = JsonValue.Create(grading),
            ["zones"] = written,
        }))
        {
            Notice(issue.Message);
            return;
        }

        Notice(Ui.Format("guide.zones.done {0} {1} {2}", ZoneName(open), ZoneName(personal), ZoneName(sensitive)));
    }

    /// <summary>Le cinque risposte, dalla piu' vicina alla piu' lontana; la seconda domanda non supera la prima.</summary>
    private static List<(string Id, string Label)> ZoneOptions(string current, string? ceiling = null) =>
    [
        .. Enum.GetValues<TrustZone>()
            .Where(zone => ceiling is null || zone <= WireEnum.Parse<TrustZone>(ceiling))
            .Select(zone => (zone.ToWire(), Mark(zone.ToWire() == current) + Ui.Text("guide.zones." + zone.ToWire()))),
    ];

    private static string Mark(bool chosen) => chosen ? "(x) " : "( ) ";

    private static string Zone(JsonObject zones, DataClass dataClass) =>
        zones["maxZone"]?[dataClass.ToWire()]?.GetValue<string>() ?? TrustZone.SameHost.ToWire();

    private static string ZoneName(string wire) => ReplConfirmation.Zone(wire);

    /// <summary>
    /// «Ordina i tuoi modelli dal piu' capace al meno capace» (ADR-027 J, ADR-030 §2).
    ///
    /// Nessun punteggio per dimensione: quello nessuno lo sa dare. L'ordine e' cio' che l'operatore conosce davvero, e
    /// da li' il router ricava i livelli (<c>MinimumSufficient</c>). Con un solo servizio che risponde non si chiede:
    /// non c'e' niente da ordinare. Dal 24 settembre 2026 (ADR-032, fase 3) e' l'unica scala della qualita': i voti per
    /// abilita' non esistono piu', e senza analizzatore la qualita' del punteggio si ricava dal posto (<c>ServiceOrder</c>).
    /// </summary>
    private void GuideOrder()
    {
        List<ServiceDescriptor> answering =
        [
            .. InPlay()
                .Where(service => draft!.Selected.Contains(service.Id) && ServiceSetupCatalog.AnswersRequests(service.Capability))
                .OrderBy(service => service.Rank ?? int.MaxValue)
                .ThenBy(service => service.Id, StringComparer.Ordinal),
        ];

        // Con meno di due modelli non c'e' niente da ordinare; dalla Home la voce non deve tacere (trovato dall'esploratore).
        if (answering.Count < 2) { Notice(Ui.Format("guide.order.tooFew {0}", answering.Count.ToString(CultureInfo.InvariantCulture))); return; }

        List<ServiceDescriptor> left = [.. answering];

        for (int position = 1; position <= answering.Count && left.Count > 1; position++)
        {
            if (interaction.Navigation.Pending) { return; }

            Frame(Ui.Text("Setup > Ordina i tuoi modelli"), Ui.Format("guide.order.why {0} {1}",
                position.ToString(CultureInfo.InvariantCulture), answering.Count.ToString(CultureInfo.InvariantCulture)));

            // Ogni voce porta la sua spiegazione, come ogni altra schermata del setup: qui i due fatti che aiutano a
            // ordinare — il posto che ha oggi e la finestra di contesto dichiarata.
            string? chosen = interaction.Choose(
                Ui.Format("guide.order.title {0}", position.ToString(CultureInfo.InvariantCulture)),
                [.. left.Select(service => (service.Id, ServiceTitle(service) + " - " + OrderHint(service)))]);

            if (chosen is null) { return; }

            ServiceDescriptor picked = left.Single(service => service.Id == chosen);
            left.Remove(picked);
            Rank(picked.Id, position);
        }

        if (left.Count == 1) { Rank(left[0].Id, answering.Count); }

        Notice(Ui.Text("guide.order.done"));
    }

    /// <summary>
    /// Vero se il profilo ammette questo servizio solo con l'accordo di trattamento: una classe che il profilo lascia
    /// arrivare fino a <c>third_party_dpa</c> e non oltre, e un servizio di terzi che quel controllo non ce l'ha
    /// ancora — ne' osservato ne' attestato (ADR-029).
    /// </summary>
    /// <summary>
    /// Servizi scelti a cui manca **solo** l'accordo di trattamento per ricevere qualche classe in piu': avvisi, non
    /// obblighi. Un servizio a cui l'operatore ha risposto «no» in questa sessione non si ripropone.
    /// </summary>
    private IEnumerable<ServiceDescriptor> AgreementsToConfirm() =>
        InPlay().Where(service => draft!.Selected.Contains(service.Id) && !declinedAgreements.Contains(service.Id) && NeedsAgreement(service));

    /// <summary>Vero se il motivo riguarda solo fin dove arrivano i dati: la regola delle zone (ADR-032, fase 4).</summary>
    private static bool ZoneOnly(PolicyReason reason) => reason is PolicyReason.ExternalForbidden or PolicyReason.ZoneNotAllowed
        or PolicyReason.DataClassForbidden or PolicyReason.ExternalConfirmationRequired;

    /// <summary>Le classi che un servizio riceverebbe solo con l'accordo, nella lingua di chi legge.</summary>
    private string ClassesNeedingAgreement()
    {
        PolicyZones zones = PolicyCompiler.Compile(draft!.Health.Snapshot!.Profiles[draft.ProfileId], draft.Health.Snapshot.Config).Zones;
        return string.Join(", ", PolicyZones.DeclaredClasses
            .Where(dataClass => zones.For(dataClass) is TrustZone.ThirdPartyDpa)
            .Select(dataClass => Ui.Text("class." + dataClass.ToWire())));
    }

    private bool NeedsAgreement(ServiceDescriptor service)
    {
        if (!ServiceSetupCatalog.AnswersRequests(service.Capability)
            || !ServiceDescriptor.IsThirdParty(service.InferencePlacement)
            || ServiceSetupCatalog.ExpectedZone(service, clock.UtcNow) is not TrustZone.ThirdParty)
        {
            return false;
        }

        PolicyZones zones = PolicyCompiler.Compile(draft!.Health.Snapshot!.Profiles[draft.ProfileId], draft.Health.Snapshot.Config).Zones;

        // Se altre regole lo escludono comunque — garanzie che non da', fuori dall'elenco — l'accordo non cambierebbe
        // niente, e chiederlo manda l'operatore a rispondere a vuoto. Dal 25 settembre 2026 (ADR-032, fase 4) le zone sono
        // l'unica regola su dove vanno i dati, e l'accordo e' cio' che le avvicina.
        bool onlyZones = ServiceSetupCatalog.Compatibility(draft.Health.Snapshot!, service, clock).All(ZoneOnly);

        return onlyZones && PolicyZones.DeclaredClasses.Any(dataClass => zones.For(dataClass) is TrustZone.ThirdPartyDpa);
    }

    /// <summary>
    /// L'accordo di trattamento con il fornitore (ADR-029, ADR-030 §2): una domanda, un Invio, precompilata con il tuo
    /// nome e un anno di validita', come ogni altra conferma (S30). Non e' fra i controlli che il profilo **richiede**:
    /// e' cio' che fa di un servizio di terzi un fornitore con accordo, e quindi lo avvicina di una zona.
    /// </summary>
    private bool ConfirmAgreement(string id)
    {
        ServiceDescriptor service = draft!.Service(id);
        string name = InstanceName(service);
        DateTimeOffset now = clock.UtcNow;
        string who = Operator();

        Frame(Ui.Text("Setup > Servizi > ") + name + " > " + Ui.Text("confirm.title"),
            Ui.Text("confirm.processing_agreement.why") + " " + Ui.Format("confirm.signature {0} {1}", who, Day(now)));

        string? choice = interaction.Choose(
            Ui.Format("confirm.processing_agreement.question {0}", ServiceTitle(service)),
            [("yes", Ui.Text("confirm.yes")), ("no", Ui.Format("confirm.no {0}", name))]);

        if (choice != "yes")
        {
            // Un «no» e' una risposta: se ne prende atto e non si richiede in questa sessione.
            if (choice == "no")
            {
                declinedAgreements.Add(id);
                Notice(Ui.Format("confirm.agreementDeclined {0} {1}", name, ClassesNeedingAgreement()));
            }

            return false;
        }

        // Il controllo si dichiara e si attesta insieme: dichiararlo senza firma non lo renderebbe effettivo, e
        // firmarlo senza dichiararlo sarebbe un'attestazione su un controllo che il servizio non dice di avere (S30).
        string[] controls = [.. service.Controls.Select(control => control.ToWire()).Append(Control.ProcessingAgreement.ToWire()).Distinct().Order(StringComparer.Ordinal)];
        JsonObject document = draft.Document("service", id);
        document["controls"] = new JsonArray([.. controls.Select(control => (JsonNode)JsonValue.Create(control)!)]);
        IReadOnlyList<ValidationIssue> issues = draft.ApplyDocument("service", id, document);

        if (issues.Count > 0) { Notice(Friendly(issues[0].Message)); return false; }

        PropertyAttestation attestation = new(who, now, null, null);

        try
        {
            ServiceAttestations.Validate(attestation, clock);
            draft.Attest(id, Control.ProcessingAgreement, attestation);
        }
        catch (ArgumentException exception) { Notice(exception.Message); return false; }

        Notice(Ui.Format("confirm.done {0} {1}", attestation.By, Day(now)));
        return true;
    }

    /// <summary>
    /// Che cosa si sa di un modello mentre lo si ordina: il posto che ha oggi nell'ordine e la finestra dichiarata. Dal 24
    /// settembre 2026 (ADR-032, fase 3) l'ordine e' l'unica scala della qualita': non c'e' piu' un voto da mostrare.
    /// </summary>
    private static string OrderHint(ServiceDescriptor service)
    {
        string place = service.Rank is int rank
            ? Ui.Format("guide.order.current {0}", rank.ToString(CultureInfo.InvariantCulture))
            : Ui.Text("guide.order.unranked");

        return service.ContextWindowTokens is int window and > 0
            ? place + " " + Ui.Format("guide.order.window {0}", window.ToString(CultureInfo.InvariantCulture))
            : place;
    }

    private void Rank(string id, int position)
    {
        IReadOnlyList<ValidationIssue> issues = draft!.EditService(id, "rank", JsonValue.Create(position));
        if (issues.Count > 0) { Notice(Friendly(issues[0].Message)); }
    }

    /// <summary>
    /// «Quale modello analizza le richieste?» (ADR-026 E, ADR-030 §3): si sceglie fra i modelli installati su un Ollama di
    /// questa macchina **che hanno un esito misurato** (`analyzer-benchmarks.json`), e decide subito (24 settembre 2026,
    /// decisione dell'operatore: niente funzioni in ombra — un analizzatore in ombra costava due secondi a turno senza
    /// decidere niente). Facoltativo: senza, decidono le regole e nel dubbio si resta in locale (demand-v3).
    /// </summary>
    private void GuideAnalyzer()
    {
        ConfigurationSnapshot snapshot = draft!.Health.Snapshot!;
        ServiceDescriptor? host = snapshot.Registry.Services.FirstOrDefault(service =>
            service.Adapter is AdapterKind.Ollama && service.InferencePlacement is Placement.Loopback && service.Endpoint is not null);

        if (host is null) { return; }

        string? current = snapshot.Config.Routing.Analyzer?.ServiceId;
        IReadOnlyList<string>? installed = Discover(host);

        Frame(Ui.Text("Setup > Analizzatore delle richieste"), Ui.Text("guide.analyzer.why"));

        List<(string Id, string Label)> options = [("none", Mark(current is null) + Ui.Text("guide.analyzer.none"))];
        options.AddRange((installed ?? [])
            .Where(model => RequestAnalyzerModel.MeasuredFor(model) is not null)
            .Select(model => ("model:" + model, Mark(current is not null && snapshot.Config.Routing.Analyzer?.ServiceId is string wired
                && snapshot.FindService(wired)?.Model == model) + Ui.Format("guide.analyzer.model {0}", model))));

        // Nessun modello misurato installato: non c'e' niente da scegliere, e lo si dice invece di proporre un elenco vuoto.
        if (options.Count == 1)
        {
            Notice(Ui.Text("guide.analyzer.noneMeasured"));
            return;
        }

        string? choice = interaction.Choose(Ui.Text("guide.analyzer.title"), options);

        if (choice is null or "none") { return; }

        string model = choice["model:".Length..];
        string id = ServiceIds.Readable("analisi", model, [.. snapshot.Registry.Services.Select(service => service.Id)]);

        if (!snapshot.Registry.Services.Any(service => service.Id == id))
        {
            draft.AddServiceFromTemplate(AdapterKind.Ollama, id);
        }

        // La capacita' non e' un campo che il setup cambia a mano (ServiceSetupCatalog.EditableFields): qui il servizio
        // **nasce** analizzatore, quindi si scrive il documento intero e lo si rilegge come ogni altra modifica.
        JsonObject document = draft.Document("service", id);
        document["capability"] = ServiceCapability.RequestAnalysis.ToWire();
        document["model"] = model;
        document["endpoint"] = host.Endpoint!.ToString();
        document["inferencePlacement"] = Placement.Loopback.ToWire();
        document["authMode"] = AuthMode.None.ToWire();
        document["capabilities"] = new JsonArray();
        document["controls"] = new JsonArray();
        // Il nome dell'istanza da cui nasce non e' il suo: «Ollama per embedding · qwen3:4b» diceva una cosa falsa.
        document.Remove("name");
        // Un analizzatore non risponde alle richieste: un posto nell'ordine o dei livelli copiati da una chat non si caricano.
        document.Remove("rank");
        document.Remove("levels");

        IReadOnlyList<ValidationIssue> issues = draft.ApplyDocument("service", id, document);

        if (issues.Count > 0) { Notice(Friendly(issues[0].Message)); return; }

        draft.Select(id, true);

        // Solo modelli misurati arrivano qui (ADR-031 B, INV-21): decidono subito, senza un modo da scrivere (ADR-032, fase 2).
        IReadOnlyList<ValidationIssue> wired = draft.SetAnalyzer(id);

        if (wired.Count > 0) { Notice(Friendly(wired[0].Message)); return; }

        Notice(Ui.Format("guide.analyzer.doneDecisive {0}", model));
    }

    private void GuideStorage()
    {
        bool encrypted = draft!.Health.Snapshot!.Config.ContentMode == ContentMode.Encrypted;
        Frame(Ui.Text("Setup > Conservazione"), Ui.Text("guide.storage.why"));
        string? choice = interaction.Choose(Ui.Text("guide.storage.title"), [
            ("encrypted", (encrypted ? "(x) " : "( ) ") + Ui.Text("guide.storage.yes")),
            ("memory", (!encrypted ? "(x) " : "( ) ") + Ui.Text("guide.storage.no"))]);
        if (choice is "encrypted" or "memory") { draft.SetStorage(choice == "encrypted"); }
    }

    private bool GuideFinish()
    {
        while (!interaction.Navigation.Pending)
        {
            List<NextStep> remaining = Remaining();
            string services = string.Join("; ", InPlay().GroupBy(GroupKey).Select(group => ServiceTitle(group.First() with { Model = null })
                + (group.Any(IsModel) && group.First().Kind == TargetKind.Llm ? " (" + string.Join(", ", group.Where(IsModel).Select(item => item.Model)) + ")" : "")));
            Frame(Ui.Text("Setup > Salva configurazione"), Ui.Format("guide.finish.why {0} {1}", ProfilePresentation.Name(draft!.Health.Snapshot, draft.ProfileId), services), remaining.Count);
            List<(string Id, string Label)> options = [.. remaining.Select((step, index) => ("todo-" + index.ToString(CultureInfo.InvariantCulture), "[obbligatorio] " + step.Label))];
            // «Salvato spento» si dice solo se qualcosa verra' davvero salvato spento: lo decide il controllo che fa il
            // salvataggio, non l'elenco delle cose da fare, che comprende anche verifiche che non spengono nulla.
            options.Add(("commit", Ui.Text(draft.EnablementIssues().Count == 0 ? "guide.finish.save" : "guide.finish.saveAnyway")));
            options.Add(("home", Ui.Text("guide.finish.home")));
            string? choice = interaction.Choose(Ui.Text(remaining.Count == 0 ? "guide.finish.ready" : "guide.finish.notReady"), options);
            if (choice is null or "home") { return true; }
            if (choice.StartsWith("todo-", StringComparison.Ordinal))
            {
                remaining[int.Parse(choice["todo-".Length..], CultureInfo.InvariantCulture)].Open();
                continue;
            }
            if (!Save(confirm: false, verify: false)) { continue; }

            // «Pronto» e' un fatto, non un saluto: se dopo il salvataggio resta qualcosa di obbligatorio il router
            // neghera' le richieste, e dirlo pronto manderebbe l'operatore a scoprirlo al primo turno.
            List<NextStep> after = Remaining();
            if (after.Count > 0)
            {
                Frame(Ui.Text("Setup > Salva configurazione"), Ui.Text("guide.saved.notReady.why"), after.Count);
                string? next = interaction.Choose(Ui.Text("guide.saved.notReady.title"),
                    [.. after.Select((step, index) => ("todo-" + index.ToString(CultureInfo.InvariantCulture), "[obbligatorio] " + step.Label)),
                        ("home", Ui.Text("guide.done.home"))]);
                if (next is not null && next.StartsWith("todo-", StringComparison.Ordinal))
                {
                    after[int.Parse(next["todo-".Length..], CultureInfo.InvariantCulture)].Open();
                    continue;
                }
                return true;
            }

            Frame(Ui.Text("Setup > Salva configurazione"), Ui.Text("guide.done.why"));
            if (interaction.Choose(Ui.Text("guide.done.title"), [("start", Ui.Text("guide.done.start")), ("home", Ui.Text("guide.done.home"))]) == "start")
            {
                interaction.Navigation.Request("quit");
            }
            return true;
        }
        return false;
    }
}
