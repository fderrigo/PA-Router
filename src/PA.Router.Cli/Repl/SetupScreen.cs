using System.Globalization;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Hosting.Runtime;
using PA.Router.Policy;
using PA.Router.Registry.Loading;
using PA.Router.Registry.Probing;

namespace PA.Router.Cli.Repl;

/// <summary>Setup condiviso fra primo avvio e modifica. La TUI non decide le regole del router.</summary>
internal sealed partial class SetupScreen(ISetupInteraction interaction, IReplRenderer renderer, IClock clock, Action? clearScreen = null,
    Func<ServiceDescriptor, IReadOnlyList<string>?>? discoverModels = null, Func<string>? operatorName = null,
    Func<ServiceDescriptor, ProbeResult?>? testService = null,
    Func<IReadOnlyList<ServiceDescriptor>, (long TotalBytes, IReadOnlyList<OllamaModelNeed> Models)>? observeOllama = null,
    Func<int, bool>? applyMaxLoaded = null)
{
    internal SetupScreen(IReplInput input, IReplRenderer renderer, IClock clock, Action? clearScreen = null)
        : this(new SetupInteraction(), renderer, clock, clearScreen) { }
    private SetupWorkingConfiguration? draft;

    /// <summary>La directory dati vera, non la copia di lavoro: le pause per quota sono stato del router, non configurazione.</summary>
    private string? sourceDirectory;
    private string breadcrumb = "Setup";
    private string notice = "";
    private string? guideStep;

    /// <summary>
    /// Servizi che erano accesi nell'ultima configurazione letta o salvata. Nella copia di lavoro `enabled` resta
    /// falso fino al salvataggio — conta la scelta dell'operatore — quindi chiedere alla bozza «era gia' acceso?» da'
    /// sempre no: e' cosi' che un setup gia' funzionante si riapriva con una cosa rossa da fare.
    /// </summary>
    private readonly HashSet<string> persistedEnabled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> modelDescriptions = new(StringComparer.Ordinal);

    /// <summary>
    /// Lunghezza del vettore osservata sulla scheda del modello, per istanza e modello. E' il numero che il gate della
    /// memoria confrontera' a ogni indicizzazione: osservarlo evita di chiederlo a chi non lo puo' sapere.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyDictionary<string, int>> observedDimensions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProbeResult> testResults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> testedDocuments = new(StringComparer.Ordinal);

    internal ConfigurationReport Run(string dataDirectory)
    {
        sourceDirectory = dataDirectory;
        _ = clearScreen;
        ConfigurationReport initial = ConfigurationHealth.Inspect(dataDirectory, clock);
        Ui.SetLanguage(initial.Snapshot?.Config.Language);
        bool recreate = false;
        if (initial.Readiness == ConfigurationReadiness.MigrationRequired)
        {
            MigrationPlan plan = ConfigMigration.Plan(dataDirectory, clock.UtcNow);
            Frame(Ui.Text("Setup > Aggiornamento"), Ui.Text("La migrazione conserva un backup. ") + string.Join("; ", plan.Files.SelectMany(file => file.Changes)));
            if (interaction.Choose(Ui.Text("Configurazione precedente"), [("migrate", Ui.Text("Migra - aggiorna i file con il convertitore esistente.")), ("quit", Ui.Text("Esci - conserva la configurazione precedente."))]) != "migrate") { return initial; }
            ConfigMigration.Apply(dataDirectory, plan);
            initial = ConfigurationHealth.Inspect(dataDirectory, clock);
        }
        if (initial.Readiness == ConfigurationReadiness.Invalid)
        {
            Frame(Ui.Text("Setup > Recupero"), Ui.Text("La configurazione non si carica. Nessun file verra' sostituito prima di Salva configurazione."));
            string? recovery = interaction.Choose(Ui.Text("Configurazione da recuperare"), [("recreate", Ui.Text("Riconfigura con assistenza - prepara una nuova configurazione; conserva gli originali e un backup al salvataggio.")),
                    ("details", Ui.Text("Diagnostica - mostra quali file impediscono il caricamento.")), ("quit", Ui.Text("Esci - conserva tutti i file invariati."))]);
            if (recovery == "details") { renderer.Warn(string.Join("; ", initial.Issues.Select(issue => issue.Source + ": " + issue.Message))); }
            if (recovery != "recreate") { return initial; }
            recreate = true;
        }
        using SetupWorkingConfiguration working = recreate ? SetupWorkingConfiguration.Recreate(dataDirectory, clock) : SetupWorkingConfiguration.Open(dataDirectory, clock);
        draft = working;
        Remember(initial);
        renderer.Info(Ui.Text("Setup avviato."));
        if (initial.Readiness == ConfigurationReadiness.Missing || recreate)
        {
            // Il primo avvio e' la guida: tocca solo cio' che serve a far partire il router. Chi sceglie di fare
            // da se' arriva alla Home, che e' lo stesso configuratore.
            _ = Guide(firstRun: true);
        }
        while (true)
        {
            try
            {
                string? action = interaction.Navigation.Take() ?? Home();
                if (interaction.Navigation.Pending) { continue; }
                if (action is null or "quit" or "no") { if (Exit()) { break; } continue; }
                if (action.StartsWith(GroupPrefix, StringComparison.Ordinal)) { Group(action); continue; }
                Dispatch(action);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
            { Notice(Ui.Text("Operazione non completata: ") + Friendly(exception.Message)); }
        }
        renderer.Info(Ui.Text("Setup terminato. /config mostra la configurazione; /models mostra i target."));
        ConfigurationReport persisted = ConfigurationHealth.Inspect(dataDirectory, clock);
        Ui.SetLanguage(persisted.Snapshot?.Config.Language);
        return persisted;
    }

    private bool Exit()
    {
        if (!draft!.IsDirty) { return true; }
        Frame(Ui.Text("Setup > Uscita"), Ui.Text("Sono presenti modifiche non salvate."));
        string? action = interaction.Choose(Ui.Text("Come vuoi uscire?"), [("save", Ui.Text("save.exit")),
            ("discard", Ui.Text("Scarta modifiche - Esce senza salvare le modifiche fatte dopo l'ultimo salvataggio.")), ("back", Ui.Text("Torna al setup - conserva le modifiche nella sessione."))]);
        bool exit = action == "save" ? Save(confirm: false, verify: false)
            : action == "discard" || (action is null && interaction is not SetupInteraction && !interaction.Navigation.Pending);
        if (!exit) { ReplTerminalScope.Current?.Interruptions.Stay(); }
        return exit;
    }

    /// <summary>
    /// Perche' l'etichetta attiva esclude un servizio, in parole semplici e al massimo due motivi (24 settembre 2026, prova
    /// dell'operatore): «escluso da questa etichetta» da solo non diceva che cosa cambiare. Le garanzie mancanti si
    /// nominano; il resto e' una frase per motivo.
    /// </summary>
    private string ExcludedBecause(ServiceDescriptor service)
    {
        ConfigurationSnapshot snapshot = draft!.Health.Snapshot!;
        List<string> why = [];

        foreach (PolicyReason reason in ServiceSetupCatalog.Compatibility(snapshot, service, clock).Distinct())
        {
            string? text = reason switch
            {
                // Dal 25 settembre 2026 (ADR-032, fase 4) le garanzie vengono dal tipo di servizio, non dall'etichetta.
                PolicyReason.ControlUnavailable => Ui.Format("services.why.controls {0}", string.Join(", ",
                    ServiceSetupCatalog.Guarantees(service).Where(control => !service.Controls.Contains(control))
                        .Select(control => Ui.Contains("field." + control.ToWire() + ".name") ? Ui.Text("field." + control.ToWire() + ".name") : control.ToWire()))),
                PolicyReason.TargetNotAllowed => Ui.Text("services.why.notListed"),
                // Una sola regola decide dove vanno i dati: le zone.
                PolicyReason.ExternalForbidden or PolicyReason.ZoneNotAllowed or PolicyReason.DataClassForbidden => Ui.Text("services.why.zone"),
                PolicyReason.CapabilityMissing or PolicyReason.CapabilityForbidden => Ui.Text("services.why.capability"),
                _ => null,
            };

            if (text is not null && !why.Contains(text)) { why.Add(text); }
        }

        return why.Count == 0
            ? Ui.Text("services.excludedByLabel")
            : Ui.Format("services.excludedBecause {0}", string.Join("; ", why.Take(2)));
    }

    /// <summary>Un'azione del setup, da qualunque livello la si scelga: Home o gruppo.</summary>
    private void Dispatch(string action)
    {
        if (action.StartsWith("dpa:", StringComparison.Ordinal)) { _ = ConfirmAgreement(action["dpa:".Length..]); return; }

        switch (action)
        {
            case "language": Language(); break;
            case "services": case "models": case "attestations": Services(); break;
            case "profile": Profile(); break;
            case "privacy": Form("policy", draft!.ProfileId, Ui.Text("Setup > Privacy e vincoli"), "constraints"); break;
            case "zones": GuideZones(); break;
            case "order": GuideOrder(); break;
            case "analyzer": GuideAnalyzer(); break;
            case "storage": Storage(); break;
            case "routing": Routing(); break;
            case "health": Health(); break;
            case "ollama": OllamaMemory(); break;
            case "advanced": Form("config", null, Ui.Text("Setup > Tutte le impostazioni")); break;
            case "registry": Form("registry", null, Ui.Text("Setup > Dati del registro")); break;
            case "api": Api(); break;
            case "save": Save(); break;
            case "next": Remaining().FirstOrDefault()?.Open(); break;
            case "guide": _ = Guide(firstRun: false); break;
            case "memory":
            case "tools":
            case "limits":
            case "extensions":
            case "telemetry":
            case "retention":
                Form("config", null, Ui.Text("Setup > ") + SetupHelp.Name(action), action); break;
        }
    }

    private const string GroupPrefix = "group:";

    /// <summary>
    /// Le voci di un gruppo della Home (deciso con l'operatore il 23 settembre 2026): al primo livello stanno i gruppi e
    /// «Servizi e modelli», dentro ogni gruppo le voci di prima. Le regole dell'etichetta sono un gruppo come gli altri, ma
    /// valgono solo per lei; tutti gli altri valgono per tutto il router.
    /// </summary>
    private List<(string Id, string Label)> GroupItems(string group, ConfigurationReport health) =>
        [.. Groups.First(entry => entry.Group == group).Items.Select(id => ItemLabel(id, health))];

    /// <summary>
    /// Quale voce sta in quale gruppo, in un punto solo: la Home lo legge per disegnare i gruppi, le prove per sapere dove
    /// si trova una voce.
    /// </summary>
    internal static readonly (string Group, string[] Items)[] Groups =
    [
        ("group:label", ["profile", "zones", "privacy"]),
        ("group:model", ["order", "analyzer", "routing"]),
        ("group:memory", ["storage", "memory", "retention"]),
        ("group:apps", ["api", "tools", "extensions"]),
        ("group:system", ["language", "ollama", "limits", "telemetry", "health"]),
        ("group:advanced", ["advanced", "registry"]),
    ];

    /// <summary>Il gruppo che contiene una voce, se sta in un gruppo.</summary>
    internal static string? GroupOf(string item) => Groups.FirstOrDefault(entry => entry.Items.Contains(item)).Group;

    private (string Id, string Label) ItemLabel(string id, ConfigurationReport health)
    {
        static (string Id, string Label) Optional(string id, string label) => (id, "[opzionale] " + label);

        return id switch
        {
            "profile" => ("profile", "[completo] " + Ui.Text("home.label.change")),
            "zones" => Optional(id, Ui.Text("home.zones")),
            "privacy" => Optional(id, Ui.Text("home.label.rules")),
            "order" => Optional(id, Ui.Text("home.order")),
            "analyzer" => Optional(id, Ui.Text("home.analyzer")),
            "ollama" => Optional(id, Ui.Text("home.ollama")),
            // La conservazione e' rossa quando manca davvero; altrimenti e' una scelta come le altre.
            "storage" => WithStatus(health, [("storage", SetupHelp.Label("contentMode"))])[0] is var storage
                && !storage.Label.StartsWith(SetupVisualText.Symbol(SetupVisualState.Optional), StringComparison.Ordinal)
                    ? storage
                    : Optional(id, SetupHelp.Label("contentMode")),
            "health" => Optional(id, Ui.Text("Stato configurazione - Scopri cosa e' pronto e cosa devi ancora completare.")),
            "advanced" => Optional(id, Ui.Text("Tutte le impostazioni - Accedi a ogni impostazione, comprese quelle avanzate.")),
            "registry" => Optional(id, Ui.Text("Dati del registro - Assegna una revisione all'elenco dei servizi configurati.")),
            _ => Optional(id, SetupHelp.Label(id)),
        };
    }

    /// <summary>Il nome di un gruppo come voce della Home, con la sua spiegazione.</summary>
    private string GroupLabel(string group) => group == "group:label"
        ? Ui.Format("home.g.label {0}", ProfilePresentation.Name(draft!.Health.Snapshot, draft.ProfileId))
        : Ui.Text("home.g." + group[GroupPrefix.Length..]);

    /// <summary>
    /// Il colore di un gruppo e' il peggiore delle sue voci: una cosa da fare dentro un gruppo non deve sparire dalla Home.
    /// </summary>
    private static string GroupState(IEnumerable<(string Id, string Label)> items)
    {
        string[] labels = [.. items.Select(item => item.Label)];
        SetupVisualState state = labels.Any(label => label.StartsWith(SetupVisualText.Symbol(SetupVisualState.RequiredMissing), StringComparison.Ordinal))
            ? SetupVisualState.RequiredMissing
            : labels.Any(label => label.StartsWith(SetupVisualText.Symbol(SetupVisualState.Warning), StringComparison.Ordinal))
                ? SetupVisualState.Warning
                : labels.Any(label => label.StartsWith(SetupVisualText.Symbol(SetupVisualState.RequiredSatisfied), StringComparison.Ordinal))
                    ? SetupVisualState.RequiredSatisfied
                    : SetupVisualState.Optional;
        return SetupVisualText.Symbol(state);
    }

    /// <summary>
    /// Un gruppo aperto dalla Home: si sceglie una voce, e finita quella si torna alla Home, come prima dei gruppi. La Home
    /// mostra subito lo stato aggiornato e «Salva»; restare nel gruppo lo avrebbe nascosto.
    /// </summary>
    private void Group(string group)
    {
        ConfigurationReport health = draft!.Validate(CurrentProbes());
        string name = GroupLabel(group).Split(" - ", 2)[0];
        Frame(Ui.Text("Setup > ") + name, group == "group:label"
            ? Ui.Format("home.g.label.why {0}", ProfilePresentation.Name(draft.Health.Snapshot, draft.ProfileId))
            : Ui.Text("home.g.global.why"));

        List<(string Id, string Label)> items = GroupItems(group, health);
        items.Add(("back", Ui.Text("B Indietro - torna alla home del setup.")));

        string? chosen = interaction.Choose(name, items);
        if (chosen is null or "back") { return; }
        Dispatch(chosen);
    }

    private string? Home()
    {
        ConfigurationReport health = draft!.Validate(CurrentProbes());
        Ui.SetLanguage(draft.Document("config")["language"]?.GetValue<string>());
        List<NextStep> remaining = Remaining();
        Frame("Setup", Ui.Text(remaining.Count == 0 ? "home.ready" : "home.why"), remaining.Count);
        if (interaction is not SetupInteraction) { Show(health); }
        List<(string Id, string Label)> options = [];
        // La guida dentro i livelli: la prima cosa obbligatoria ancora aperta, a qualunque profondita' si trovi.
        if (remaining.Count > 0) { options.Add(("next", "[obbligatorio] " + Ui.Format("home.next {0}", remaining[0].Label) + " - " + Ui.Text("home.nextHint"))); }
        else if (draft.IsDirty) { options.Add(("save", "[warning] " + Ui.Text("home.nextSave"))); }
        foreach (ServiceDescriptor service in AgreementsToConfirm())
        {
            options.Add(("dpa:" + service.Id, "[warning] " + Ui.Format("home.dpa {0} {1}", InstanceName(service), ClassesNeedingAgreement())));
        }

        // Al primo livello i gruppi e «Servizi e modelli», la voce piu' usata (deciso con l'operatore il 23 settembre 2026).
        // Prima c'erano ventinove voci su due pagine. Le regole dell'etichetta sono il primo gruppo: valgono solo per lei.
        (string Id, string Label) Grouped(string group) => (group, GroupState(GroupItems(group, health)) + " " + GroupLabel(group));

        options.Add(Grouped("group:label"));
        options.Add(("#-global", Ui.Text("home.g.globalNote")));
        options.Add(("services", SetupVisualText.Symbol(remaining.Count > 0 ? SetupVisualState.RequiredMissing : ServicesState(health)) + " " + Ui.Text("home.services")));
        options.Add(Grouped("group:model"));
        options.Add(Grouped("group:memory"));
        options.Add(Grouped("group:apps"));
        options.Add(Grouped("group:system"));
        options.Add(Grouped("group:advanced"));

        options.Add(("#actions", Ui.Text("home.actions")));
        options.Add(("save", Ui.Text("S Salva configurazione - Scrive tutte le modifiche e verifica il risultato su disco.")));
        options.Add(("guide", Ui.Text("home.guide")));
        options.Add(("quit", Ui.Text("Q Esci - Chiude il setup; se ci sono modifiche, scegli se salvarle o scartarle.")));
        return interaction.Choose(Ui.Text("home.title"), options);
    }

    /// <summary>Lo stato peggiore fra servizi, modelli e conferme: nella Home sono una voce sola.</summary>
    private static SetupVisualState ServicesState(ConfigurationReport report)
    {
        SetupVisualState[] states = [.. report.Sections
            .Where(section => section.Section is ConfigurationSection.Provider or ConfigurationSection.Models or ConfigurationSection.Privacy)
            .Select(section => ToVisual(section.Severity))];
        return states.Contains(SetupVisualState.RequiredMissing) ? SetupVisualState.RequiredMissing
            : states.Contains(SetupVisualState.Warning) ? SetupVisualState.Warning
            : states.Contains(SetupVisualState.RequiredSatisfied) ? SetupVisualState.RequiredSatisfied : SetupVisualState.Optional;
    }

    private void Language()
    {
        Frame(Ui.Text("Setup > Lingua"), Ui.Text("La lingua cambia subito nella sessione. Salva configurazione per conservarla al prossimo avvio."));
        string? choice = interaction.Choose(SetupHelp.Name("language"), [.. Ui.Languages.Select(language => (language,
            (language == Ui.Language ? "(x) " : "( ) ") + Ui.NativeName(language) + " - " + Ui.Text("Usa questa lingua per menu, aiuto e messaggi."))),
            ("back", Ui.Text("Indietro - conserva la scelta attuale."))]);
        if (choice is null or "back") { return; }
        JsonObject config = draft!.Document("config");
        config["language"] = choice;
        var issues = draft.ApplyDocument("config", null, config);
        if (issues.Count == 0) { Ui.SetLanguage(choice); }
        else { Notice(Friendly(issues[0].Message)); }
    }

    private static IReadOnlyList<(string Id, string Label)> WithStatus(ConfigurationReport report, IReadOnlyList<(string Id, string Label)> options)
    {
        Dictionary<string, ConfigurationSection> sections = new()
        {
            ["services"] = ConfigurationSection.Provider,
            ["models"] = ConfigurationSection.Models,
            ["privacy"] = ConfigurationSection.Privacy,
            ["attestations"] = ConfigurationSection.Privacy,
            ["storage"] = ConfigurationSection.Storage,
        };
        return [.. options.Select(option => sections.TryGetValue(option.Id, out var section)
            && report.Sections.FirstOrDefault(item => item.Section == section) is { } status
            ? (option.Id, SetupVisualText.Symbol(ToVisual(status.Severity)) + " " + option.Label) : option)];
    }

    private void Services()
    {
        while (!interaction.Navigation.Pending)
        {
            Frame(Ui.Text("Setup > Servizi"), Ui.Text("Scegli un'istanza. I modelli dello stesso endpoint sono raggruppati."));
            // Nel setup i servizi stanno nell'ordine del registro: locali prima, poi agenti, poi remoti. E' l'ordine
            // di chi configura, e l'operatore l'ha preferito all'alfabetico. L'alfabetico vale per gli elenchi dei comandi.
            List<(string Id, string Label)> options = [.. draft!.Health.Snapshot!.Registry.Services.GroupBy(GroupKey)
                .Select(group =>
            {
                ServiceDescriptor first = group.First();
                SetupVisualState state = InstanceVisualState(group);
                int chosen = group.Count(service => draft.Selected.Contains(service.Id));
                string summary = first.Kind == TargetKind.Agent || !ServiceSetupCatalog.Describe(first).DiscoverModels
                    ? Ui.Text(chosen > 0 ? "services.on" : first.HasPlaceholders ? "services.toConfigure" : "services.off")
                    : group.Any(IsModel)
                        ? Ui.Format("services.models {0} {1}", string.Join(", ", group.Where(IsModel).Select(item => item.Model)), chosen)
                        : Ui.Text("services.noModels");
                // I servizi valgono per tutto il router; che cosa ne pensa l'etichetta attiva si dice accanto, perche' e' la
                // prima cosa che cambia cambiando etichetta.
                string verdict = !ServiceSetupCatalog.AnswersRequests(first.Capability) ? string.Empty
                    : group.Any(service => ServiceSetupCatalog.Compatibility(draft.Health.Snapshot!, service, clock).Count == 0)
                        ? " · " + Ui.Text("services.admittedByLabel")
                        : " · " + ExcludedBecause(first);
                return (first.Id, SetupVisualText.Symbol(state) + " " + InstanceName(first) + verdict + " - " + summary);
            })];
            options.Add(("add", Ui.Text("A Aggiungi servizio - Collega un altro server Ollama, provider API o agente.")));
            options.Add(("back", Ui.Text("B Indietro - torna alla home del setup.")));
            string? id = interaction.Choose(Ui.Text("Servizi e istanze - Collega server e agenti; apri un'istanza per gestire i suoi modelli e requisiti."), options);
            if (id is null or "back") { return; }
            if (id == "add") { AddService(); continue; }
            ConfigureService(id);
        }
    }

    private void AddService()
    {
        Frame(Ui.Text("Setup > Servizi > Aggiungi"), Ui.Text("Scegli un'integrazione supportata dal runtime installato."));
        string[] supported = ["ollama", "ollama-embed", "openai-compatible", "claude-code-cli", "codex-cli", "anthropic", "opencode-cli", "onnx-local", "deterministic"];
        var adapters = draft!.Health.Snapshot!.Registry.Services.Select(service => service.Adapter).Distinct().Where(adapter => supported.Contains(adapter.ToWire())).ToArray();
        string? choice = interaction.Choose(Ui.Text("Tipo di servizio"), [.. adapters.Select(adapter => (adapter.ToWire(), SetupHelp.Label(adapter.ToWire()))), ("back", Ui.Text("Indietro - annulla l'aggiunta."))]);
        if (choice is null or "back") { return; }
        var adapter = adapters.Single(item => item.ToWire() == choice);
        var existing = draft.Health.Snapshot!.Registry.Services.Where(item => item.Adapter == adapter && item.Endpoint is not null && !item.HasPlaceholders)
            .GroupBy(GroupKey).Select(group => group.First()).ToArray();
        if (existing.Length > 0)
        {
            string? instance = interaction.Choose(Ui.Text("assist.instance"), [.. existing.Select(item => (item.Id,
                InstanceName(item) + " - " + item.Endpoint + "; " + Ui.Text("assist.reuse"))), ("new", Ui.Text("assist.newInstance")), ("back", SetupHelp.Label("back"))]);
            if (instance is null or "back") { return; }
            if (instance != "new") { ConfigureService(instance); return; }
        }
        string? id = interaction.Edit(Ui.Text("Nome del servizio - usa lettere minuscole, numeri e trattini"), "");
        if (string.IsNullOrWhiteSpace(id)) { return; }
        draft.AddServiceFromTemplate(adapter, id);
        ConfigureService(id);
    }

    private void ConfigureService(string id, bool targetView = false)
    {
        while (!interaction.Navigation.Pending)
        {
            ServiceDescriptor service = draft!.Service(id);
            ServiceConfigurationReport status = ServiceStatus(id);
            string path = targetView ? Ui.Text("Setup > Servizi > ") + InstanceName(Instance(service)[0]) + Ui.Text(" > Modelli > ") + SetupHelp.Value(JsonValue.Create(service.Model))
                : Ui.Text("Setup > Servizi > ") + InstanceName(service);
            Frame(path, ServiceSentence(service));
            // Il menu del servizio a gruppi (S116). Erano diciassette voci in fila, con tre modi di dire «modelli», due
            // di dire «dove gira» e tre di dire «conferme»: chi apriva un servizio non sapeva da quale cominciare. In
            // cima sta cio' che manca a **questo** servizio; il resto e' ordinato per quanto spesso serve. Gli
            // identificatori sono quelli di prima: cambiano l'ordine e le parole, non le operazioni.
            string Mark(string field) => SetupVisualText.Symbol(FieldVisualState(service, status, field, targetView)) + " ";
            ServiceSetupDescriptor descriptor = ServiceSetupCatalog.Describe(service);
            List<(string Id, string Label)> options = [];

            if (draft.Selected.Contains(id) && NextFor(service) is { } nextStep)
            {
                options.Add(("service-next", "[obbligatorio] " + Ui.Format("home.next {0}", nextStep.Label) + " - " + Ui.Text("home.nextHint")));
            }

            // In pausa per quota finita (24 settembre 2026): in cima, perche' e' il motivo per cui il router non lo usa.
            QuotaPause? pause = sourceDirectory is null ? null
                : QuotaPauses.Of(PA.Router.Infrastructure.Storage.StorePaths.For(sourceDirectory), id, clock.UtcNow);
            if (pause is not null)
            {
                options.Add(("resume", "[in pausa] " + Output.QuotaText.State(pause.Until) + " - " + Ui.Text("service.resume")));
            }

            options.Add(("#service", Ui.Text("service.group.main")));
            if (!targetView)
            {
                options.Add(("rename", "[opzionale] " + Ui.Format("service.rename {0}", InstanceName(service))));
            }
            if (!targetView && descriptor.DiscoverModels) { options.Add(("models", Mark("models") + Ui.Text("service.models"))); }
            foreach (ServiceSetupField field in descriptor.Fields.Where(field => field.Key is "executable" or "dimension" or "fingerprint"
                || (field.Key == "model" && (targetView || !descriptor.DiscoverModels))))
            {
                options.Add((field.Key, Mark(field.Key) + SetupHelp.Name(field.Key) + ": " + SetupHelp.Value(draft.Document("service", id)[field.Key]) + " - " + SetupHelp.Description(field.Key)));
            }
            if (service.Endpoint is not null) { options.Add(("connection", Mark("connection") + Ui.Format("service.connection {0}", SetupHelp.Value(JsonValue.Create(service.Endpoint.ToString()))))); }
            if (service.AuthMode == AuthMode.ApiKey) { options.Add(("secret", Mark("secret") + Ui.Text("service.secret"))); }
            bool answers = ServiceSetupCatalog.AnswersRequests(service.Capability);
            // La voce sparisce solo se il profilo non richiede alcun controllo: con controlli gia' osservati o
            // confermati resta, perche' vederli e poterne revocare uno e' l'unica prova che quella firma esista.
            if (answers && (CurrentControlGrades(service).Count > 0 || ServiceSetupCatalog.AgreementMatters(service)))
            {
                options.Add(("confirm", Mark("confirm") + Ui.Text("service.confirm")));
            }
            options.Add(("toggle", Mark("toggle") + Ui.Text(draft.Selected.Contains(id) ? "service.toggle.on" : "service.toggle.off")));

            options.Add(("#quality", Ui.Text("service.group.quality")));
            // Contesto e uscita massima sono di chi genera una risposta: a un embedding non si chiedono.
            if (service.Kind == TargetKind.Llm && ServiceSetupCatalog.AnswersRequests(service.Capability)) { options.Add(("limits", "[opzionale] " + Ui.Text("assist2.modelLimits"))); }
            if (!targetView && descriptor.DiscoverModels) { options.Add(("targets", "[opzionale] " + Ui.Text("service.targets"))); }

            options.Add(("#check", Ui.Text("service.group.check")));
            options.Add(("test", Ui.Text("service.test")));
            options.Add(("requirements", Mark("requirements") + (answers
                ? Ui.Text("service.requirements") + " " + SetupHelp.Name(status.State) + "."
                : Ui.Text("service.requirements.aux"))));
            options.Add(("details", Ui.Text("service.details")));

            options.Add(("#advanced", Ui.Text("service.group.advanced")));
            if (service.Endpoint is not null) { options.Add(("endpoint", "[opzionale] " + Ui.Text("service.endpointRaw"))); }
            foreach (ServiceSetupField field in descriptor.Fields.Where(field => field.Key is "processingRegions"
                || (field.Key == "secretRef" && service.AuthMode != AuthMode.ApiKey)))
            {
                options.Add((field.Key, "[opzionale] " + SetupHelp.Name(field.Key) + ": " + SetupHelp.Value(draft.Document("service", id)[field.Key]) + " - " + SetupHelp.Description(field.Key)));
            }
            options.Add(("admit", "[opzionale] " + Ui.Text("service.admit")));
            options.Add(("policy", "[opzionale] " + Ui.Text("service.policy")));
            options.Add(("advanced", "[opzionale] " + Ui.Text("service.allProperties")));
            options.Add(("back", Ui.Text("B Indietro - torna all'elenco delle istanze.")));
            string? choice = interaction.Choose(targetView ? SetupHelp.Value(JsonValue.Create(service.Model)) : InstanceName(service), options);
            if (choice is null or "back") { return; }
            switch (choice)
            {
                case "service-next": NextFor(draft.Service(id))?.Open(); continue;
                case "resume":
                    // Vale subito e non passa da «Salva»: non cambia la configurazione, toglie la pausa.
                    Notice(sourceDirectory is not null && QuotaPauses.Resume(PA.Router.Infrastructure.Storage.StorePaths.For(sourceDirectory), id)
                        ? Ui.Format("service.resumed {0}", InstanceName(service))
                        : Ui.Text("service.notPaused"));
                    continue;
                case "requirements": ServiceRequirements(id); continue;
                case "models": ManageModels(id); continue;
                case "rename": Rename(id); continue;
                case "connection": Connection(id); continue;
                case "confirm":
                    if (NeedsVerification(service) && !Verify(id, ask: AsksBeforeVerifying(service))) { continue; }
                    _ = Confirmations(id);
                    continue;
                case "targets": Targets(id); continue;
                case "limits":
                    if (!targetView && Instance(service).Length > 1) { Targets(id, limits: true); }
                    else { ModelLimits(id); }
                    continue;
                case "advanced": Form("service", id, breadcrumb + Ui.Text(" > Tutte le proprieta'")); continue;
                case "policy": Form("policy", draft.ProfileId, Ui.Text("Setup > Servizi > Vincoli del profilo"), "constraints"); continue;
                case "admit":
                    if (draft.AdmissionRequired([id]).Count == 0) { Notice(Ui.Text("service.alreadyAdmitted")); }
                    else { Admit([id]); }
                    continue;
                case "toggle":
                    draft.Select(id, !draft.Selected.Contains(id));
                    Notice(Ui.Text(draft.Selected.Contains(id) ? "service.switchedOn" : "service.switchedOff"));
                    continue;
                case "details": ServiceDetails(id); continue;
                case "installed":
                case "executable":
                    string? program = ChooseExecutable(service);
                    if (program is not null) { ApplyField(id, "executable", program); }
                    continue;
                case "model":
                    string? chosenModel = ChooseModel(service);
                    if (chosenModel is not null) { ApplyField(id, "model", chosenModel); }
                    continue;
                case "secretRef":
                case "secret": testResults.Clear(); testedDocuments.Clear(); ConfigureCredential(id); continue;
                case "test":
                    Frame(breadcrumb + Ui.Text(" > Verifica"), Ui.Text("Il test puo' contattare il servizio e usare la credenziale configurata; non genera risposte o scarica modelli."));
                    if (interaction.Choose(Ui.Text("Autorizzi la verifica?"), [("yes", Ui.Text("Verifica - Contatta il server o controlla il programma configurato.")), ("back", Ui.Text("Indietro - non contattare il servizio."))]) == "yes")
                    {
                        testResults.Remove(id);
                        testedDocuments.Remove(id);
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try
                        {
                            var probe = Test(id, timeout.Token);
                            if (probe is not null)
                            {
                                testResults[id] = probe;
                                testedDocuments[id] = ProbeKey(id);
                            }
                            Notice(probe is null ? Ui.Text("Questa integrazione non offre una verifica automatica; la disponibilita' resta da verificare.")
                                : probe.FailureCode is null ? ServiceSummary(ServiceStatus(id))
                                : Ui.Text("Verifica non riuscita: il servizio non e' disponibile. Apri I Dettagli per vedere il motivo."));
                        }
                        catch (OperationCanceledException) { Notice(Ui.Text("Verifica interrotta: tempo massimo raggiunto.")); }
                    }
                    continue;
            }
            if (choice is "endpoint")
            {
                Frame(breadcrumb + " > " + SetupHelp.Name(choice), SetupHelp.Description(choice));
                string? value = interaction.Edit(SetupHelp.Label(choice), RawCurrent(draft.Value(id, choice)));
                if (value is not null)
                {
                    if (!ConfirmConnectionChange(id, value, service.InferencePlacement)) { continue; }
                    var issues = choice == "endpoint" ? draft.EditInstanceEndpoint(id, value, service.InferencePlacement)
                        : draft.EditService(id, choice, JsonValue.Create(value));
                    if (issues.Count > 0) { _ = interaction.Navigation.Take(); }
                    Notice(issues.Count == 0 ? Ui.Text("Modifica confermata nella sessione.") : Ui.Text("Modifica non applicata: ") + Friendly(issues[0].Message));
                }
            }
            else { Form("service", id, breadcrumb + " > " + SetupHelp.Name(choice), choice); }
        }
    }

    private void Connection(string id, Placement? preset = null)
    {
        ServiceDescriptor service = draft!.Service(id);
        Frame(Ui.Text("Setup > Servizi > ") + InstanceName(service) + Ui.Text(" > Collegamento"), Ui.Text("Indirizzo e collocazione sono modificati insieme e validati dal router."));
        // Ogni collocazione si puo' scegliere (ADR-032, fase 4): dove vanno i dati lo decidono le zone dell'etichetta.
        string? placement = preset?.ToWire() ?? interaction.Choose(Ui.Text("Dove gira il servizio?"), [.. Enum.GetValues<Placement>().Select(item => (item.ToWire(), (item == service.InferencePlacement ? "(x) " : "( ) ") + SetupHelp.Label(item.ToWire()))), ("back", SetupHelp.Label("back"))]);
        if (placement is null or "back") { return; }
        string current = placement == service.InferencePlacement.ToWire() ? service.Endpoint?.ToString() ?? ""
            : placement == "loopback" && service.Adapter is AdapterKind.Ollama or AdapterKind.OllamaEmbed ? "http://127.0.0.1:11434" : "";
        string? endpoint = interaction.Edit(Ui.Text("Indirizzo del server - in LAN puoi inserire solo l'indirizzo IP"), current);
        if (endpoint is null) { return; }
        endpoint = NormalizeEndpoint(endpoint, placement, service.Adapter is AdapterKind.Ollama or AdapterKind.OllamaEmbed);
        Placement chosenPlacement = Enum.GetValues<Placement>().Single(item => item.ToWire() == placement);
        if (!ConfirmConnectionChange(id, endpoint, chosenPlacement)) { return; }
        var issues = draft.EditInstanceEndpoint(id, endpoint, chosenPlacement);
        if (issues.Count > 0) { _ = interaction.Navigation.Take(); }
        Notice(issues.Count == 0 ? Ui.Text("Collegamento confermato nella sessione. Apri Modelli per verificare l'istanza.") : Ui.Text("Collegamento non modificato: ") + Friendly(issues[0].Message));
    }

    private void ManageModels(string id)
    {
        while (!interaction.Navigation.Pending)
        {
            ServiceDescriptor service = draft!.Service(id);
            Frame(Ui.Text("Setup > Servizi > ") + InstanceName(service) + Ui.Text(" > Modelli"), service.Endpoint + Ui.Text(" - la lettura contatta solo questa istanza, senza scaricare modelli."));
            if (!observations.ContainsKey(GroupKey(service)))
            {
                string? action = interaction.Choose(Ui.Text("Verifica e rileva modelli"), [("discover", Ui.Text("Rileva dal server - Leggi i modelli installati su questa istanza, senza scaricarne altri.")),
                    ("configured", Ui.Text("Gia' configurati - Rivedi i modelli presenti nel router senza contattare il server.")), ("manual", Ui.Text("assist.manualModel")), ("back", Ui.Text("Indietro - torna al servizio."))]);
                if (action is null or "back") { return; }
                if (action == "configured") { Targets(id); return; }
                if (action == "manual")
                {
                    string? model = Manual("model", service.Model);
                    if (model is not null) { ApplyField(id, "model", model); }
                    return;
                }
                var found = Discover(service);
                if (found is null) { continue; }
                observations[GroupKey(service)] = found;
            }
            var instance = Instance(service);
            var discovered = observations[GroupKey(service)];
            var excluded = modelDescriptions.GetValueOrDefault(GroupKey(service))?.Where(pair => !discovered.Contains(pair.Key)).ToArray() ?? [];
            if (discovered.Count == 0 && excluded.Length == 0)
            {
                Notice(service.Adapter is AdapterKind.Ollama or AdapterKind.OllamaEmbed
                ? Ui.Text("Ollama risponde ma non contiene modelli. Puoi installarne uno esternamente con ollama pull <modello>, poi R aggiorna.")
                : service.Adapter == AdapterKind.OnnxLocal ? Ui.Format("assist.emptyLocalModels {0}", draft.LocalModelsDirectory) : Ui.Text("assist.emptyModels"));
            }
            string[] models = [.. discovered.Concat(instance.Where(IsModel).Select(item => item.Model!)).Distinct(StringComparer.Ordinal)];
            var selected = new HashSet<string>(instance.Where(item => draft.Selected.Contains(item.Id) && IsModel(item)).Select(item => item.Model!));
            Frame(Ui.Text("Setup > Servizi > ") + InstanceName(service) + Ui.Text(" > Modelli"), service.Endpoint + " - " + discovered.Count + Ui.Text(" rilevati. La selezione richiede abilitazione; policy e attestazioni restano obbligatorie.") + " " + Ui.Text("assist2.catalogueHelp"));
            if (excluded.Length > 0)
            {
                Frame(breadcrumb, service.Endpoint + " - " + Ui.Text("assist2.catalogueUnsupported") + " " + string.Join(", ", excluded.Select(pair => pair.Key)));
            }
            IReadOnlyList<string>? chosen = interaction.Many(Ui.Text("Modelli rilevati e configurati - Scegli quali modelli di questa istanza rendere disponibili al router."), [.. models.Select(model => (model,
                    model + " - " + (selected.Contains(model) ? Ui.Text("selezionato per il router") : instance.Any(item => item.Model == model) ? Ui.Text("configurato ma non selezionato") : Ui.Text("assist.discoveredModel"))
                    + (!discovered.Contains(model) ? Ui.Text("; non rilevato nell'ultima verifica.")
                        : ModelDescription(service, model) is { Length: > 0 } described ? " " + described : "")))], selected, refresh: true);
            if (chosen is null && interaction.Navigation.Destination == "refresh")
            {
                _ = interaction.Navigation.Take();
                observations.Remove(GroupKey(service));
                continue;
            }
            if (chosen is null) { return; }
            // Confirm the models before performing the navigation requested in the picker.
            // Suspend it while asking whether newly created targets belong to the profile.
            string? destination = interaction.Navigation.Take();
            IReadOnlyDictionary<string, int>? dimensions = ReadEmbeddingDimensions(service, chosen);
            if (service.Capability == ServiceCapability.Embedding && dimensions is null) { return; }
            destination = interaction.Navigation.Take() ?? destination;
            var outputs = ReadOutputLimits(service, chosen);
            if (service.Adapter == AdapterKind.Anthropic && outputs is null) { return; }
            destination = interaction.Navigation.Take() ?? destination;
            IReadOnlyList<string> targetIds = draft.ConfigureModels(id, chosen, dimensions, outputs);
            ApproveEmbeddingFingerprints(targetIds);
            Admit(targetIds);
            AskThinking(targetIds);
            Notice(chosen.Count + Ui.Text(" modelli selezionati nella sessione. Completa le attestazioni, poi S Salva configurazione."));
            if (!interaction.Navigation.Pending && destination == "refresh")
            {
                observations.Remove(GroupKey(service));
                continue;
            }
            if (!interaction.Navigation.Pending && destination is not null) { interaction.Navigation.Request(destination); }
            return;
        }
    }

    /// <summary>
    /// Per ogni modello di chat appena scelto che ragiona prima di rispondere, una domanda sola: lo spengo? (23 settembre
    /// 2026). Il router osserva da solo se il modello ragiona e se si puo' spegnere; all'operatore resta la scelta, senza
    /// parametri tecnici. Un modello che ragiona sempre non si spegne: lo si dice, perche' su un computer senza scheda
    /// grafica ogni risposta costa minuti.
    /// </summary>
    private void AskThinking(IReadOnlyList<string> targetIds)
    {
        foreach (string target in targetIds)
        {
            ServiceDescriptor service = draft!.Service(target);
            if (service.Adapter is not AdapterKind.Ollama || service.Capability is not ServiceCapability.Chat || service.Thinking is not null) { continue; }

            // Nelle prove il catalogo e' finto e non c'e' un modello da interrogare.
            ThinkingSupport support = discoverModels is not null
                ? ThinkingSupport.None
                : SetupDiscovery.ThinkingAsync(service, CancellationToken.None).GetAwaiter().GetResult();

            if (support is ThinkingSupport.Always)
            {
                Notice(Ui.Format("setup.thinking.always {0}", service.Model!));
                continue;
            }

            if (support is not ThinkingSupport.Switchable) { continue; }

            Frame(Ui.Text("Setup > Servizi > ") + InstanceName(service) + " > " + service.Model, Ui.Text("setup.thinking.why"));
            string? choice = interaction.Choose(Ui.Format("setup.thinking.question {0}", service.Model!),
            [
                ("off", Ui.Text("setup.thinking.off")),
                ("on", Ui.Text("setup.thinking.on")),
            ]);

            if (choice is null) { return; }

            IReadOnlyList<ValidationIssue> issues = draft.EditService(target, "thinking", JsonValue.Create(choice == "on"));
            if (issues.Count > 0) { Notice(Friendly(issues[0].Message)); }
        }
    }

    /// <summary>
    /// La memoria di Ollama (23 settembre 2026): il router guarda il computer e i modelli scelti, propone quanti tenerne
    /// caricati insieme e per quanto, e l'operatore accetta o cambia il numero. Nessun parametro da conoscere: i numeri
    /// servono a capire la proposta, non a compilarla. Il calcolo e' <see cref="OllamaMemoryPlan"/>.
    /// </summary>
    private void OllamaMemory()
    {
        ServiceDescriptor[] services = [.. draft!.Health.Snapshot!.Registry.Services
            .Where(service => draft.Selected.Contains(service.Id) || persistedEnabled.Contains(service.Id))];
        string path = Ui.Text("Setup > ") + Ui.Text("ollama.title");

        Frame(path, Ui.Text("ollama.why"));
        (long total, IReadOnlyList<OllamaModelNeed> models) = observeOllama?.Invoke(services)
            ?? (OllamaMemoryPlan.TotalMemoryBytes(), OllamaMemoryPlan.ObserveAsync(services, clock, CancellationToken.None).GetAwaiter().GetResult());

        if (models.Count == 0)
        {
            Notice(Ui.Text("ollama.none"));
            return;
        }

        OllamaMemoryProposal proposal = OllamaMemoryPlan.Propose(total, models);
        string[] auxiliary = [.. models.Where(model => model.Auxiliary).Select(model => model.Model).Distinct(StringComparer.Ordinal)];
        List<string> lines =
        [
            Ui.Format("ollama.total {0} {1}", Gigabytes(proposal.TotalBytes), Gigabytes(proposal.BudgetBytes)),
        ];

        if (auxiliary.Length > 0) { lines.Add(Ui.Format("ollama.aux {0} {1}", string.Join(", ", auxiliary), Gigabytes(proposal.AuxiliaryBytes))); }
        if (proposal.LargestChatBytes > 0)
        {
            lines.Add(proposal.ChatSlots == 1
                ? Ui.Format("ollama.chat.one {0}", Gigabytes(proposal.LargestChatBytes))
                : Ui.Format("ollama.chat.many {0} {1}", Gigabytes(proposal.LargestChatBytes), proposal.ChatSlots));
            lines.Add(Ui.Format("ollama.keep {0}", OllamaMemoryPlan.ChatKeepAliveMinutes));
        }

        if (proposal.Tight) { lines.Add(Ui.Text("ollama.tight")); }
        if (proposal.Unmeasured.Count > 0) { lines.Add(Ui.Format("ollama.unmeasured {0}", string.Join(", ", proposal.Unmeasured))); }
        if ((observeOllama is null ? OllamaMemoryPlan.CurrentMaxLoaded() : null) is int current) { lines.Add(Ui.Format("ollama.current {0}", current)); }

        Frame(path, string.Join(" ", lines));
        string? choice = interaction.Choose(Ui.Format("ollama.question {0}", proposal.MaxLoadedModels),
        [
            ("accept", Ui.Text("ollama.accept")),
            ("change", Ui.Text("ollama.change")),
            ("back", Ui.Text("ollama.back")),
        ]);

        if (choice is null or "back") { return; }

        int maximum = proposal.MaxLoadedModels;

        if (choice == "change")
        {
            string? typed = interaction.Edit(Ui.Text("ollama.edit"), maximum.ToString(CultureInfo.InvariantCulture));
            if (typed is null) { return; }

            if (!int.TryParse(typed.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out maximum) || maximum is < 1 or > 20)
            {
                Notice(Ui.Text("ollama.invalid"));
                return;
            }
        }

        foreach ((string serviceId, int minutes) in proposal.KeepAliveMinutes)
        {
            IReadOnlyList<ValidationIssue> issues = draft.EditService(serviceId, "keepAliveMinutes", JsonValue.Create(minutes));
            if (issues.Count > 0)
            {
                Notice(Friendly(issues[0].Message));
                return;
            }
        }

        bool written = applyMaxLoaded?.Invoke(maximum) ?? OllamaMemoryPlan.ApplyMaxLoaded(maximum);
        Notice(written
            ? Ui.Format("ollama.done {0}", maximum)
            : Ui.Format("ollama.manual {0} {1}", OllamaMemoryPlan.MaxLoadedVariable, maximum));

        static string Gigabytes(long bytes) => (bytes / 1024d / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture);
    }

    private void Targets(string id, bool limits = false)
    {
        while (!interaction.Navigation.Pending)
        {
            ServiceDescriptor service = draft!.Service(id);
            Frame(Ui.Text("Setup > Servizi > ") + InstanceName(service) + Ui.Text(" > Target"), Ui.Text("Ogni modello conserva le proprie dichiarazioni e abilitazioni."));
            string? target = interaction.Choose(Ui.Text("Modelli configurati"), [.. Instance(service).Select(item => (item.Id,
                SetupHelp.Value(JsonValue.Create(item.Model)) + " - " + (draft.Selected.Contains(item.Id) ? Ui.Text("selezionato; da validare") : Ui.Text("disabilitato")) + "; " + item.Id)),
                .. (limits ? Array.Empty<(string, string)>() : [("add", Ui.Text("Aggiungi modello - Usa un altro modello dello stesso server, mantenendo indirizzo e credenziale."))]),
                ("back", Ui.Text("Indietro - torna all'istanza."))]);
            if (target is null or "back") { return; }
            if (target == "add")
            {
                string? model = ChooseModel(service, adding: true);
                if (string.IsNullOrWhiteSpace(model)) { continue; }
                string? destination = interaction.Navigation.Take();
                string[] models = [.. Instance(service).Where(item => draft.Selected.Contains(item.Id) && IsModel(item)).Select(item => item.Model!), model];
                var dimensions = ReadEmbeddingDimensions(service, models);
                if (service.Capability == ServiceCapability.Embedding && dimensions is null) { continue; }
                destination = interaction.Navigation.Take() ?? destination;
                var outputs = ReadOutputLimits(service, models);
                if (service.Adapter == AdapterKind.Anthropic && outputs is null) { continue; }
                destination = interaction.Navigation.Take() ?? destination;
                var ids = draft.ConfigureModels(id, models, dimensions, outputs);
                ApproveEmbeddingFingerprints(ids);
                Admit(ids);
                if (!interaction.Navigation.Pending && destination is not null) { interaction.Navigation.Request(destination); continue; }
                ConfigureService(ids.Last(), targetView: true);
                continue;
            }
            else if (limits) { ModelLimits(target); }
            else { ConfigureService(target, targetView: true); }
        }
    }

    private void Attest(string id)
    {
        ServiceDescriptor service = draft!.Service(id);
        Frame(Ui.Text("Setup > Servizi > ") + InstanceName(service) + Ui.Text(" > Attestazioni"), Ui.Text("Dichiara solo condizioni che hai verificato. Il router non le certifica autonomamente."));
        var missing = CurrentControlGrades(ServiceStatus(id).Effective?.Service ?? draft.Service(id))
            .Where(pair => pair.Value is not "osservato" and not "attestato")
            .Select(pair => pair.Key).ToArray();
        string? selected = interaction.Choose(Ui.Text("Requisito da attestare"), [.. missing.Select(control => (control.ToWire(), SetupHelp.Label(control.ToWire()))),
            ("all", Ui.Text("assist2.allControls")), ("back", Ui.Text("Indietro - conserva le attestazioni."))]);
        if (selected == "all")
        {
            selected = interaction.Choose(Ui.Text("assist2.allControls"), [.. Enum.GetValues<Control>().Select(control => (control.ToWire(), SetupHelp.Label(control.ToWire()))),
                ("back", SetupHelp.Label("back"))]);
        }
        if (selected is null or "back") { return; }
        Control control = Enum.GetValues<Control>().Single(item => item.ToWire() == selected);
        if (interaction.Choose(Ui.Text("Confermi questo requisito?"), [("yes", Ui.Text("Confermo - ho verificato la condizione descritta.")), ("no", Ui.Text("Non posso confermare - nessuna dichiarazione viene registrata.")), ("back", Ui.Text("Indietro - torna al servizio."))]) != "yes") { return; }
        service.Attestations.TryGetValue(control, out PropertyAttestation? previous);
        PropertyAttestation? attestation = ReadAttestation(previous);
        if (attestation is null) { return; }
        string? destination = interaction.Navigation.Take();
        try
        {
            var scope = DeclarationScope(service);
            if (scope is null || scope.Count == 0) { return; }
            draft.AttestServices(scope, control, attestation);
            Notice(Ui.Text("Attestazione confermata nella sessione."));
        }
        finally { if (!interaction.Navigation.Pending && destination is not null) { interaction.Navigation.Request(destination); } }
    }

    private PropertyAttestation? ReadAttestation(PropertyAttestation? previous)
    {
        string? destination = null;
        string? Required(string field, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) { return value; }
            Notice(Ui.Text("Attestazione non confermata: il campo ") + Ui.Text(field) + Ui.Text(" e' obbligatorio."));
            return null;
        }

        string? Read(string title, string current)
        {
            string? value = interaction.Edit(title, current);
            // A confirmed field must not cause the remaining mandatory attributes to be skipped.
            if (value is not null) { destination = interaction.Navigation.Take() ?? destination; }
            return value;
        }
        string? by = Required(SetupHelp.Name("by"), Read(SetupHelp.Label("by"), RawCurrent(previous?.By ?? "")));
        if (by is null) { return null; }
        string? observed = Required(SetupHelp.Name("observedAt"), Read(SetupHelp.Label("observedAt") + Ui.Text(" - data ISO con fuso orario"), previous?.ObservedAt?.ToString("O") ?? clock.UtcNow.ToString("O")));
        if (observed is null) { return null; }
        if (!DateTimeOffset.TryParse(observed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        {
            Notice(Ui.Text("Attestazione non confermata: indica una data di osservazione valida con ora e fuso orario."));
            return null;
        }
        // Una conferma non scade (ADR-032, fase 4): nome, data e una nota facoltativa. Ogni anno si ricorda di ricontrollarla.
        string? note = Read(SetupHelp.Label("note") + Ui.Text(" - facoltativa"), previous?.Note ?? "");
        if (note is null) { return null; }
        var attestation = new PropertyAttestation(by, start, null, string.IsNullOrWhiteSpace(note) ? null : note);
        try
        {
            ServiceAttestations.Validate(attestation, clock);
            if (destination is not null) { interaction.Navigation.Request(destination); }
            return attestation;
        }
        catch (ArgumentException exception) { Notice(exception.Message); return null; }
    }

    private void Profile()
    {
        while (!interaction.Navigation.Pending)
        {
            Frame(Ui.Text("Setup > Etichetta"), Ui.Text("label.why"));
            ConfigurationSnapshot labels = draft!.Health.Snapshot!;
            interaction.Details(labels.Profiles.Keys.ToDictionary(id => id,
                id => id + "\n" + draft.Document("policy", id).ToJsonString(new() { WriteIndented = true })));
            // Ogni etichetta con la sua descrizione calcolata dalle regole: il nome da solo non dice che cosa si sceglie.
            string? choice = interaction.Choose(Ui.Text("label.choose"),
                [.. labels.Profiles.Keys.Select(id => (id, (id == draft.ProfileId ? "(x) " : "( ) ") + ProfilePresentation.Label(labels, id, clock))),
                    ("rename", Ui.Format("label.rename {0}", ProfilePresentation.Name(labels, draft.ProfileId))),
                    ("edit", Ui.Text("home.label.rules")),
                    ("add", Ui.Text("label.add")), ("back", Ui.Text("Indietro - torna alla home."))]);
            if (choice is null or "back") { return; }
            if (choice == "rename") { NameProfile(labels); }
            else if (choice == "edit") { Form("policy", draft.ProfileId, Ui.Text("Setup > Profilo > ") + draft.ProfileId); }
            else if (choice == "add")
            {
                string? name = interaction.Edit(Ui.Text("Nome del profilo - lettere minuscole, numeri e trattini"), "");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var issues = draft.DuplicateProfile(name);
                    if (issues.Count > 0) { Notice(Friendly(issues[0].Message)); }
                    else { draft.ChooseProfile(name); }
                }
            }
            else if (choice != draft.ProfileId) { ConfirmProfile(choice); }
        }
    }

    /// <summary>
    /// Il nome della modalita' attiva e' un dato dell'operatore (24 settembre 2026): si scrive qui, e vuoto torna a mostrare
    /// l'identificatore. Il codice non conosce nessun nome di modalita'.
    /// </summary>
    private void NameProfile(ConfigurationSnapshot labels)
    {
        string? name = interaction.Edit(Ui.Text("label.rename.prompt"), labels.Profiles[draft!.ProfileId].Name ?? "");
        if (name is null) { return; }
        try
        {
            IReadOnlyList<ValidationIssue> issues = draft.NameProfile(name);
            Notice(issues.Count > 0 ? Friendly(issues[0].Message)
                : Ui.Format("label.renamed {0}", ProfilePresentation.Name(draft.Health.Snapshot, draft.ProfileId)));
        }
        catch (ArgumentException) { Notice(Ui.Text("label.rename.taken")); }
    }

    private void ConfirmProfile(string id)
    {
        // A presentation-only snapshot: never dispatched, persisted or used to mint a grant.
        ConfigurationSnapshot current = draft!.Health.Snapshot!;
        ConfigurationSnapshot preview = current with { Config = current.Config with { DefaultProfileId = id } };
        string[] affected = [.. current.Registry.Services.Where(service => !service.HasPlaceholders)
            .Select(service => (service, reasons: ServiceSetupCatalog.Compatibility(preview, service, clock)))
            .Where(item => item.reasons.Count > 0)
            .Select(item => item.service.Id + ": " + Friendly(string.Join("; ", item.reasons.Select(reason => reason.ToWire()))))];
        Frame(Ui.Text("Setup > Etichetta") + " > " + ProfilePresentation.Name(current, id),
            Ui.Format("label.consequence {0} {1}", ProfilePresentation.Name(current, id), ProfilePresentation.Summary(ProfileFacts.Describe(current, id, clock)))
            + " " + (affected.Length == 0 ? Ui.Text("profile.noConflict") : Ui.Text("profile.consequence") + " " + string.Join("; ", affected)));
        interaction.Details(new Dictionary<string, string>
        {
            ["confirm"] = id + "\n" + draft.Document("policy", id).ToJsonString(new() { WriteIndented = true }),
        });
        if (interaction.Choose(ProfilePresentation.Label(current, id, clock), [("confirm", Ui.Text("profile.confirm")),
            ("back", Ui.Text("profile.cancel"))]) == "confirm") { draft.ChooseProfile(id); }
    }

    private void Storage()
    {
        Frame(Ui.Text("Setup > Conservazione"), SetupHelp.Description("contentMode"));
        bool encrypted = draft!.Health.Snapshot!.Config.ContentMode == ContentMode.Encrypted;
        string? choice = interaction.Choose(Ui.Text("Come conservare i contenuti?"), [("encrypted", (encrypted ? "(x) " : "( ) ") + SetupHelp.Label("encrypted")),
            ("memory", (!encrypted ? "(x) " : "( ) ") + SetupHelp.Label("memory_only")), ("back", Ui.Text("Indietro - conserva la scelta corrente."))]);
        if (choice is "encrypted" or "memory") { draft.SetStorage(choice == "encrypted"); }
    }

    private void Api()
    {
        Frame(Ui.Text("Setup > Accesso tramite API"), Ui.Text("Configura l'accesso delle applicazioni; l'ascolto resta locale e autenticato."));
        string? choice = interaction.Choose(Ui.Text("Configurazione API"), [("edit", Ui.Text("Impostazioni - Configura il collegamento e le regole di accesso per le applicazioni.")),
            ("token", Ui.Text("Prepara token di accesso - Crea la credenziale per le applicazioni al salvataggio, se manca.")), ("back", Ui.Text("Indietro - torna alla home."))]);
        if (choice == "edit") { Form("config", null, Ui.Text("Setup > Accesso tramite API > Impostazioni"), "api"); }
        if (choice == "token") { draft!.RequestApiToken(); Notice(Ui.Text("Creazione del token richiesta per il prossimo salvataggio. Il valore non viene mostrato.")); }
    }

    private void Routing()
    {
        Frame(Ui.Text("Setup > Scelta dei servizi"), Ui.Text("Preferenza locale non disponibile. La collocazione limita i servizi ammessi, senza dare priorita' al locale."));
        bool prefer = draft!.Health.Snapshot!.Config.PreferSubscription;
        string? choice = interaction.Choose(Ui.Text("Scelta dei servizi - Decidi come scegliere fra i servizi che hanno superato privacy e policy."), [("subscription", (prefer ? "(x) " : "( ) ") + Ui.Text("Preferisci abbonamento - Dai precedenza ai servizi in abbonamento ammessi dal profilo.")),
            ("automatic", (!prefer ? "(x) " : "( ) ") + Ui.Text("Automatico - Lascia scegliere il router senza dare precedenza all'abbonamento.")),
            ("back", Ui.Text("Indietro - torna alla home."))]);
        if (choice is "subscription" or "automatic") { draft.PreferSubscription(choice == "subscription"); }
    }

    private void Health()
    {
        ConfigurationReport health = draft!.Validate(CurrentProbes());
        Frame(Ui.Text("Setup > Stato configurazione"), Ui.Text("Stato dichiarativo; la disponibilita' reale richiede una verifica esplicita."));
        _ = interaction.Choose(Ui.Text("Requisiti"), [.. health.Sections.Select(section => (section.Section.ToString(),
            SetupVisualText.Symbol(ToVisual(section.Severity)) + " " + Named(Friendly(section.Summary)) + " - " + Named(Friendly(section.Hint ?? Ui.Text("Nessun intervento richiesto."))))), ("back", Ui.Text("Indietro - torna alla home."))]);
    }

    /// <summary>
    /// Gli identificatori che il setup ha generato per i modelli aggiunti (`model-…`) non dicono nulla a chi legge: al
    /// loro posto si scrive il nome del modello. L'identificatore resta nei dettagli tecnici.
    /// </summary>
    private string Named(string text)
    {
        foreach (ServiceDescriptor service in draft!.Health.Snapshot!.Registry.Services.Where(item => item.Id.StartsWith("model-", StringComparison.Ordinal) && IsModel(item)))
        {
            text = text.Replace(service.Id, service.Model, StringComparison.Ordinal);
        }
        return text;
    }

    private void ServiceDetails(string id)
    {
        var status = ServiceStatus(id);
        IReadOnlyDictionary<Control, string> grades = CurrentControlGrades(status.Effective?.Service ?? draft!.Service(id));
        // Il percorso dice il nome; l'identificatore resta qui sotto, fra i dettagli tecnici, perche' e' a lui che sono
        // legate conferme e permessi e chi diagnostica lo deve poter leggere.
        Frame(Ui.Text("Setup > Servizi > ") + ServiceNames.Display(draft!.Service(id)) + Ui.Text(" > Dettagli"),
            Ui.Text("Stato: ") + SetupHelp.Name(status?.State ?? Ui.Text("da validare")) + " - id: " + id);
        _ = interaction.Choose(Ui.Text("Motivi e diagnostica"), [.. (status?.Reasons ?? []).Select((reason, index) => (index.ToString(CultureInfo.InvariantCulture), Friendly(reason) + Ui.Text(" - codice: ") + reason)),
            .. (CurrentProbes().TryGetValue(id, out var probe) ? new[] { ("probe", Ui.Text("Ultima verifica - stato osservato: ") + probe.Health + Ui.Text("; codice: ") + (probe.FailureCode ?? Ui.Text("nessun errore"))) } : Array.Empty<(string, string)>()),
            .. grades.Select(pair => ("control-" + pair.Key.ToWire(), ControlDescription(pair) + Ui.Text(" - codice: ") + pair.Key.ToWire())),
            ("back", Ui.Text("Indietro - torna al servizio."))]);
    }

    private IReadOnlyDictionary<string, ProbeResult> CurrentProbes() => testResults
        .Where(pair => draft!.Health.Snapshot!.Registry.Services.Any(service => service.Id == pair.Key)
            && testedDocuments.GetValueOrDefault(pair.Key) == ProbeKey(pair.Key))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>
    /// Che cosa e' stato osservato: il servizio senza il suo interruttore. Abilitare non cambia indirizzo, modello o
    /// programma, quindi non invalida l'osservazione — prima un salvataggio la buttava via e tutto tornava rosso.
    /// </summary>
    private string ProbeKey(string id)
    {
        // Solo cio' che una verifica guarda davvero. Una conferma dell'operatore, un voto o un limite non
        // cambiano che cosa risponde a quell'indirizzo: prima li contava, e confermare buttava via la verifica.
        JsonObject document = draft!.Document("service", id);
        string[] observed = ["adapter", "endpoint", "model", "executable", "arguments", "authMode", "secretRef", "inferencePlacement"];
        return string.Join("|", observed.Select(field => field + "=" + (document[field]?.ToJsonString() ?? "")));
    }

    private ProbeResult? Test(string id, CancellationToken cancellationToken) => testService is not null
        ? testService(draft!.Service(id))
        : draft!.TestServiceAsync(id, cancellationToken).GetAwaiter().GetResult();

    private ServiceConfigurationReport ServiceStatus(string id) => ConfigurationHealth.DescribeService(
        draft!.Health.Snapshot!, draft.Service(id) with { Enabled = draft.Selected.Contains(id) }, clock, CurrentProbes().GetValueOrDefault(id));

    private SetupVisualState InstanceVisualState(IEnumerable<ServiceDescriptor> services)
    {
        ServiceDescriptor[] instance = [.. services];
        ServiceConfigurationReport[] reports = [.. instance.Select(service => ServiceStatus(service.Id))];
        if (reports.Any(report => draft!.Selected.Contains(report.Id) && report.Severity is (SectionSeverity.Missing or SectionSeverity.Error))) { return SetupVisualState.RequiredMissing; }
        if (reports.Any(report => draft!.Selected.Contains(report.Id) && report.Severity == SectionSeverity.Warning)) { return SetupVisualState.Warning; }
        if (reports.Any(report => draft!.Selected.Contains(report.Id))) { return SetupVisualState.RequiredSatisfied; }
        if (!HasSelectedAllowedService() && instance.Any(IsAllowedCandidate)) { return SetupVisualState.RequiredMissing; }
        return SetupVisualState.Optional;
    }

    private SetupVisualState FieldVisualState(ServiceDescriptor service, ServiceConfigurationReport status, string field, bool targetView)
    {
        bool required = draft!.Selected.Contains(service.Id) || service.Enabled || RequiresServiceChoice(service);
        if (!required) { return SetupVisualState.Optional; }
        if (field is "requirements" or "attest" or "confirm")
        {
            // Rosso e' cio' che tocca all'operatore: una conferma mancante o scaduta, o un requisito che questo
            // servizio non puo' soddisfare. Un controllo mai interrogato non e' un obbligo dell'operatore: si
            // verifica, e fino ad allora e' un avviso.
            var grades = CurrentControlGrades(service).Values;
            if (grades.Any(value => value is "non dichiarato" or "attestazione scaduta") || PendingConfirmations(service).Length > 0) { return SetupVisualState.RequiredMissing; }
            return NeedsVerification(service) ? SetupVisualState.Warning : SetupVisualState.RequiredSatisfied;
        }
        if (field is "model" or "models" or "targets")
        {
            return IsModel(service) ? SetupVisualState.RequiredSatisfied : SetupVisualState.RequiredMissing;
        }
        if (field is "endpoint" or "connection")
        {
            return service.Endpoint is null || service.Endpoint.OriginalString.Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal)
                ? SetupVisualState.RequiredMissing
                : SetupVisualState.RequiredSatisfied;
        }
        if (field is "secret" or "secretRef")
        {
            bool missing = status.Reasons.Any(reason => reason.Contains("AUTH_UNAVAILABLE", StringComparison.Ordinal)
                || reason.Contains("credenziale", StringComparison.OrdinalIgnoreCase));
            return missing ? SetupVisualState.RequiredMissing
                : status.Severity == SectionSeverity.Warning ? SetupVisualState.Warning : SetupVisualState.RequiredSatisfied;
        }
        if (field is "toggle")
        {
            return status.Severity is SectionSeverity.Error or SectionSeverity.Missing
                ? SetupVisualState.RequiredMissing
                : status.Severity == SectionSeverity.Warning ? SetupVisualState.Warning : SetupVisualState.RequiredSatisfied;
        }
        return status.Severity is SectionSeverity.Error or SectionSeverity.Missing
            ? SetupVisualState.RequiredMissing
            : status.Severity == SectionSeverity.Warning ? SetupVisualState.Warning : SetupVisualState.RequiredSatisfied;
    }

    private bool RequiresServiceChoice(ServiceDescriptor service) => !HasSelectedAllowedService() && IsAllowedCandidate(service);

    private bool HasSelectedAllowedService()
    {
        ConfigurationSnapshot snapshot = draft!.Health.Snapshot!;
        PolicyConstraints constraints = snapshot.Profiles[draft.ProfileId].Constraints;
        // Conta chi risponde alle richieste: scegliere il solo embedding non e' «aver scelto un servizio».
        return snapshot.Registry.Services.Any(service => draft.Selected.Contains(service.Id) && constraints.AllowsService(service.Id)
            && ServiceSetupCatalog.AnswersRequests(service.Capability));
    }

    private bool IsAllowedCandidate(ServiceDescriptor service)
    {
        ConfigurationSnapshot snapshot = draft!.Health.Snapshot!;
        // «Devi scegliere un servizio» riguarda chi risponde alle richieste. Un embedding sta nell'elenco dei servizi
        // ammessi, ma sceglierlo non fa partire il router: segnarlo rosso e obbligatorio mandava a configurare la cosa
        // sbagliata.
        return ServiceSetupCatalog.AnswersRequests(service.Capability)
            && snapshot.Profiles[draft.ProfileId].Constraints.AllowsService(service.Id);
    }

    private IReadOnlyDictionary<Control, string> CurrentControlGrades(ServiceDescriptor service)
    {
        // Le garanzie si chiedono a chi risponde a una richiesta (S11), e vengono dal tipo di servizio (ADR-032, fase 4):
        // a un servizio ausiliario no.
        if (!ServiceSetupCatalog.AnswersRequests(service.Capability)) { return new Dictionary<Control, string>(); }
        ProbeResult? probe = CurrentProbes().GetValueOrDefault(service.Id);
        return ServiceSetupCatalog.Guarantees(service)
            .OrderBy(control => control.ToWire(), StringComparer.Ordinal)
            .ToDictionary(control => control, control => !ServiceSetupCatalog.Declares(service, control)
                ? "non dichiarato"
                : PrivacyReadiness.GradeOf(service, probe, control, clock.UtcNow));
    }

    internal static string ServiceSummary(ServiceConfigurationReport status)
    {
        string summary = SetupHelp.Label(status.State);
        if (status.State == "Disabled") { return summary; }
        var missing = status.ControlGrades.FirstOrDefault(pair => pair.Value is "ignoto" or "attestazione scaduta" or "non dichiarato");
        if (missing.Value is not null) { return summary + " " + ControlDescription(missing); }
        return status.State == "ScenarioBlocked" && status.ScenarioReasons.Count > 0
            ? summary + " " + SetupHelp.Label(status.ScenarioReasons[0].ToWire()) : summary;
    }

    internal static string ControlDescription(KeyValuePair<Control, string> evidence) =>
        SetupHelp.Name(evidence.Key.ToWire()) + " - " + Ui.Text("evidence.grade." + evidence.Value);

    private void ServiceRequirements(string id)
    {
        if (!ServiceSetupCatalog.AnswersRequests(draft!.Service(id).Capability))
        {
            AuxiliaryRequirements(id);
            return;
        }

        while (!interaction.Navigation.Pending)
        {
            ServiceConfigurationReport status = ServiceStatus(id);
            IReadOnlyDictionary<Control, string> grades = CurrentControlGrades(status.Effective?.Service ?? draft!.Service(id));
            bool chosen = draft!.Selected.Contains(id) || persistedEnabled.Contains(id);
            string path = Ui.Text("Setup > Servizi > ") + InstanceName(draft.Service(id)) + " > " + Ui.Text("evidence.requirements");
            Frame(path, grades.Count == 0 ? Ui.Text("evidence.nothingRequired") : SetupHelp.Description(status.State));
            interaction.Details(grades.ToDictionary(pair => pair.Key.ToWire(), pair =>
                SetupHelp.Description(pair.Key.ToWire()) + "\n" + draft.ProfileId + ": " + pair.Key.ToWire()
                + "\n" + Ui.Text("evidence.scenario")));
            string? choice = interaction.Choose(Ui.Text("evidence.requiredBy") + " " + ProfilePresentation.Name(draft.Health.Snapshot, draft.ProfileId),
                [.. grades.OrderBy(pair => pair.Value is "osservato" or "attestato" ? 1 : 0)
                    .Select(pair => (pair.Key.ToWire(), SetupVisualText.Symbol(pair.Value is "osservato" or "attestato" ? SetupVisualState.RequiredSatisfied
                        // Rosso e' un obbligo di adesso: su un servizio che l'operatore non ha scelto una garanzia
                        // mancante non impedisce niente, ed e' un avviso. Dal momento in cui lo sceglie, torna rossa.
                        : !chosen ? SetupVisualState.Warning
                        : pair.Value == "ignoto" && ServiceSetupCatalog.ObservedByProbe(draft.Service(id).Adapter).Contains(pair.Key) ? SetupVisualState.Warning
                        : SetupVisualState.RequiredMissing) + " " + ControlDescription(pair))),
                    ("back", Ui.Text("Indietro - torna al servizio."))]);
            if (choice is null or "back") { return; }

            // Nessuna riga e' un vicolo cieco: cio' che il router osserva da solo si verifica, cio' che sa solo
            // l'operatore si conferma, e cio' che questo servizio non puo' dare porta a cambiare uso.
            Control chosenControl = Enum.GetValues<Control>().Single(control => control.ToWire() == choice);
            string grade = grades.GetValueOrDefault(chosenControl, "");
            if (grade is "ignoto" or "attestazione scaduta")
            {
                // Si verifica **questo** servizio, scelto o no: chi apre la riga vuole sapere se risponde, e l'esito si
                // legge in ogni caso. Verso un servizio di terzi si chiede prima, come sempre.
                if (ServiceSetupCatalog.ObservedByProbe(draft.Service(id).Adapter).Contains(chosenControl)) { _ = Verify(id, ask: AsksBeforeVerifying(draft.Service(id))); }
                else { _ = Confirmations(id); }
                continue;
            }

            Frame(path + " > " + SetupHelp.Name(choice), SetupHelp.Description(choice));
            if (grade == "non dichiarato")
            {
                if (interaction.Choose(Ui.Text("evidence.cannot.title"), [("profile", Ui.Text("evidence.cannot.change")), ("back", SetupHelp.Label("back"))]) == "profile") { Profile(); }
                continue;
            }
            _ = interaction.Choose(Ui.Text("evidence.ok.title"), [("back", SetupHelp.Label("back"))]);
        }
    }

    /// <summary>
    /// Che cosa serve a un servizio che lavora **per il router** e non per la richiesta: un embedding per la memoria,
    /// un rilevatore per la privacy. Il profilo non gli chiede garanzie (S11); gli servono un modello, e qualcuno che
    /// lo usi. Ogni riga dice come sta e porta dove si sistema.
    /// </summary>
    private void AuxiliaryRequirements(string id)
    {
        while (!interaction.Navigation.Pending)
        {
            ServiceDescriptor service = draft!.Service(id);
            ConfigurationSnapshot snapshot = draft.Health.Snapshot!;
            bool embedding = service.Capability == ServiceCapability.Embedding;
            string[] instance = [.. Instance(service).Select(item => item.Id)];
            bool modelChosen = Instance(service).Any(item => IsModel(item) && draft.Selected.Contains(item.Id));
            bool usedByMemory = snapshot.Config.Memory.EmbeddingServiceId is string used && instance.Contains(used);
            bool allowed = snapshot.Profiles[draft.ProfileId].Constraints.AllowLocalMemoryProcessing;
            bool stored = snapshot.Config.ContentMode == ContentMode.Encrypted;
            string Mark(bool ok) => SetupVisualText.Symbol(ok ? SetupVisualState.RequiredSatisfied : SetupVisualState.Warning) + " ";

            Frame(Ui.Text("Setup > Servizi > ") + InstanceName(service) + " > " + Ui.Text("evidence.requirements"),
                Ui.Text(embedding ? "aux.embedding.why" : "aux.other.why"));
            // Identificatori propri: «allowed» e «model» sono anche nomi di campi, e la resa ne prenderebbe la descrizione.
            List<(string Id, string Label)> options = [("aux-model", Mark(modelChosen) + Ui.Text(modelChosen ? "aux.model.ok" : "aux.model.missing"))];
            if (embedding)
            {
                options.Add(("aux-memory", Mark(usedByMemory) + Ui.Text(usedByMemory ? "aux.memory.ok" : "aux.memory.missing")));
                options.Add(("aux-allowed", Mark(allowed) + Ui.Text(allowed ? "aux.allowed.ok" : "aux.allowed.missing")));
                options.Add(("aux-stored", Mark(stored) + Ui.Text(stored ? "aux.stored.ok" : "aux.stored.missing")));
            }
            options.Add(("back", Ui.Text("Indietro - torna al servizio.")));

            string? choice = interaction.Choose(Ui.Text(embedding ? "aux.embedding.title" : "aux.other.title"), options);
            if (choice is null or "back") { return; }
            switch (choice)
            {
                case "aux-model":
                    if (ServiceSetupCatalog.Describe(service).DiscoverModels) { ManageModels(id); }
                    else if (ChooseModel(service) is string model) { ApplyField(id, "model", model); }
                    break;
                case "aux-memory":
                    if (usedByMemory) { Notice(Ui.Text("aux.memory.already")); break; }
                    string target = Instance(service).FirstOrDefault(item => IsModel(item) && draft.Selected.Contains(item.Id))?.Id ?? id;
                    // Il campo nasce `null`, e `EditConfig` rifiuta cio' che non ha gia' un valore: si passa dal documento
                    // intero, che il caricatore valida allo stesso modo.
                    JsonObject config = draft.Document("config");
                    (config["memory"] as JsonObject ?? throw new InvalidOperationException("memory"))["embeddingServiceId"] = target;
                    var issues = draft.ApplyDocument("config", null, config);
                    Notice(issues.Count == 0 ? Ui.Text("aux.memory.done") : Friendly(issues[0].Message));
                    break;
                case "aux-allowed": Form("policy", draft.ProfileId, Ui.Text("Setup > Privacy e vincoli"), "constraints"); break;
                case "aux-stored": Storage(); break;
            }
        }
    }

    private bool Save(bool confirm = true, bool verify = true)
    {
        Frame(Ui.Text("Setup > Salva configurazione"), Ui.Text("Salva tutte le modifiche confermate. Le schede non confermate non vengono persistite."));
        if (confirm && interaction.Choose(Ui.Text("Conferma persistenza su disco"), [("commit", Ui.Text("Salva configurazione - Scrive tutte le modifiche e controlla che i dati riletti coincidano.")), ("back", Ui.Text("Annulla - conserva le modifiche solo nella sessione."))]) != "commit") { return false; }
        string[] disabled = [];
        bool committed = false;
        try
        {
            // Una configurazione scritta prima che il setup compilasse l'impronta ha il modello scelto e l'impronta
            // vuota: si compila qui, per i soli servizi che l'operatore ha scelto e che non ne dichiarano gia' una.
            ApproveEmbeddingFingerprints([.. draft!.Selected.Where(id => string.IsNullOrEmpty(draft.Service(id).Fingerprint))]);
            var blocked = draft!.EnablementIssues();
            if (blocked.Count > 0)
            {
                Frame(Ui.Text("Setup > Salva configurazione"), Ui.Text("save.blockedExplanation"));
                string summary = string.Join("; ", blocked.Select(pair =>
                    SetupHelp.Value(JsonValue.Create(draft.Service(pair.Key).Model ?? pair.Key)) + ": " + Friendly(pair.Value[0])));
                interaction.Details(new Dictionary<string, string>
                {
                    ["save-disabled"] = string.Join("; ", blocked.Select(pair => pair.Key + ": " + string.Join("; ", pair.Value))),
                });
                if (interaction.Choose(Ui.Text("save.blockedTitle") + " - " + summary,
                    [("save-disabled", Ui.Text("save.disabledAction")), ("back", Ui.Text("save.completeFirst"))]) != "save-disabled") { return false; }
                disabled = [.. blocked.Keys];
                foreach (string id in disabled) { draft.Select(id, false); }
            }
            var result = draft!.Commit();
            if (result.Snapshot is null) { Notice(Ui.Text("Configurazione non salvata: ") + Friendly(result.Issues.FirstOrDefault()?.Message ?? Ui.Text("completa i requisiti indicati"))); return false; }
            committed = true;
            Remember(result);
            Ui.SetLanguage(result.Snapshot.Config.Language);
            renderer.Info(Ui.Text("Configurazione salvata e verificata."));
            if (disabled.Length > 0)
            {
                renderer.Warn(Ui.Text("save.disabledResult"));

                // Salvati spenti, ma **ancora scelti**: prima qui perdevano la spunta, e l'operatore ritrovava «2
                // configurati, 0 selezionati» senza una parola sul perche'. La scelta resta nella sessione, le voci
                // restano rosse con il loro motivo, e «Prossima cosa da fare» porta a cio' che manca.
                foreach (string id in disabled) { draft.Select(id, true); }
            }
            if (result.Readiness != ConfigurationReadiness.Valid) { renderer.Warn(Ui.Text("save.incomplete")); }
            notice = disabled.Length > 0
                ? Ui.Format("save.disabledNotice {0}", string.Join(", ", disabled.Select(id => draft.Service(id).Model ?? InstanceName(draft.Service(id)))))
                : Ui.Text("Configurazione salvata e verificata. Puoi continuare a modificare.");
            if (!verify) { return true; }
            Frame(Ui.Text("Setup > Verifica"), Ui.Text("Persistenza verificata. Il test Ollama genera una richiesta alla sua istanza."));
            if (interaction.Choose(Ui.Text("Vuoi verificare ora le istanze Ollama?"), [("yes", Ui.Text("Si - rileva i modelli dagli endpoint configurati.")), ("no", Ui.Text("No - torna al setup senza traffico aggiuntivo."))]) == "yes")
            {
                foreach (var service in result.Snapshot.Registry.Services.Where(item => item.Enabled && item.Adapter is AdapterKind.Ollama or AdapterKind.OllamaEmbed).GroupBy(GroupKey).Select(group => group.First()))
                {
                    var found = Discover(service);
                    renderer.Info(InstanceName(service) + ": " + (found is null ? Ui.Text("nessuna risposta valida") : found.Count + Ui.Text(" modelli rilevati")));
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            Notice(Ui.Text("Configurazione non salvata: ") + Friendly(exception.Message));
            return false;
        }
        finally
        {
            if (!committed) { foreach (string id in disabled) { draft!.Select(id, true); } }
        }
    }

    internal void Show(ConfigurationReport report)
    {
        renderer.Header([(Ui.Text("Stato:"), SetupHelp.Name(report.Readiness.ToString())), (Ui.Text("Avvio"), report.CanStart ? Ui.Text("si") : Ui.Text("no"))]);
        foreach (var section in report.Sections) { renderer.SetupStatus(ToVisual(section.Severity), SectionName(section.Section), Friendly(section.Summary)); }
    }
    /// <summary>Che cos'e' questo servizio adesso, in una frase: dove sta, che cosa usa, se il router lo usa.</summary>
    private string ServiceSentence(ServiceDescriptor service)
    {
        ServiceDescriptor[] instance = Instance(service);
        int chosen = instance.Count(item => draft!.Selected.Contains(item.Id));
        string where = service.Endpoint is not null
            ? SetupHelp.Value(JsonValue.Create(service.Endpoint.ToString()))
            : service.HasPlaceholders ? Ui.Text("service.sentence.noProgram") : service.Executable ?? "";
        string what = service.Kind == TargetKind.Agent || !ServiceSetupCatalog.Describe(service).DiscoverModels
            ? Ui.Text(chosen > 0 ? "services.on" : service.HasPlaceholders ? "services.toConfigure" : "services.off")
            : instance.Any(IsModel)
                ? Ui.Format("services.models {0} {1}", string.Join(", ", instance.Where(IsModel).Select(item => item.Model)), chosen)
                : Ui.Text("services.noModels");
        // La zona di fiducia (ADR-029) si ricava da dove lavora il servizio e dai controlli confermati: si mostra qui,
        // e non e' una domanda. Per i servizi ausiliari non vale: non ricevono i dati di una richiesta.
        string zone = ServiceSetupCatalog.AnswersRequests(service.Capability)
            ? " " + Ui.Format("service.sentence.zone {0}", ReplConfirmation.Zone(ServiceSetupCatalog.ExpectedZone(service, clock.UtcNow).ToWire()))
            : string.Empty;

        return where + " - " + what + "." + zone + " " + Ui.Text("service.sentence.hint");
    }

    /// <summary>
    /// Scegliere un modello di embedding con la sua dimensione **e'** approvarlo: l'impronta che il gate pretende e'
    /// derivata da quei due valori, e la compila il setup. Senza, il salvataggio spegneva il servizio dicendo «deve
    /// dichiarare l'impronta osservata», e la Home riproponeva «Salva» all'infinito.
    /// </summary>
    private void ApproveEmbeddingFingerprints(IEnumerable<string> ids)
    {
        foreach (string id in ids)
        {
            ServiceDescriptor service = draft!.Service(id);
            if (service.Capability != ServiceCapability.Embedding || service.Adapter != AdapterKind.OllamaEmbed
                || !IsModel(service) || service.Dimension is not int dimension) { continue; }
            var issues = draft.EditService(id, "fingerprint", JsonValue.Create(ServiceSetupCatalog.EmbeddingFingerprint(service.Model!, dimension)));
            if (issues.Count > 0) { Notice(Friendly(issues[0].Message)); }
        }
    }

    private void Remember(ConfigurationReport report)
    {
        persistedEnabled.Clear();
        if (report.Snapshot is { } snapshot) { persistedEnabled.UnionWith(snapshot.Registry.Services.Where(service => service.Enabled).Select(service => service.Id)); }
    }

    private void Form(string kind, string? id, string path, string? section = null) => new SetupForm(interaction, draft!, Notice, persistedEnabled).Open(kind, id, path, section);
    private void Frame(string path, string context, int? missing = null)
    {
        breadcrumb = path;
        ShowLabel();
        interaction.Page(new SetupPage(path, context) { Notice = notice, Dirty = draft?.IsDirty ?? false, Step = guideStep, Missing = missing }.Checked());
        if (interaction is not SetupInteraction) { renderer.Info(path); renderer.Info(context); if (draft?.IsDirty == true) { renderer.Info(Ui.Text("Modifiche non salvate.")); } }
        notice = "";
    }
    private void Notice(string message) { notice = Friendly(message); if (interaction is not SetupInteraction) { renderer.Info(notice); } }

    /// <summary>
    /// L'etichetta attiva e la sua descrizione calcolata, in cima a ogni pagina (<see cref="ProfileFacts"/>). Prima del
    /// recupero o della migrazione non c'e' una bozza leggibile, e non si mostra niente.
    /// </summary>
    private void ShowLabel()
    {
        if (draft?.Health.Snapshot is not { } snapshot || !snapshot.Profiles.ContainsKey(draft.ProfileId)) { return; }

        interaction.ActiveLabel(ProfilePresentation.Name(draft.Health.Snapshot, draft.ProfileId), ProfilePresentation.Summary(ProfileFacts.Describe(snapshot, draft.ProfileId, clock)));
    }
    private IReadOnlyList<string>? Discover(ServiceDescriptor service)
    {
        try
        {
            IReadOnlyList<string>? found;
            if (discoverModels is not null) { found = discoverModels(service); }
            else if (service.Adapter is AdapterKind.Ollama or AdapterKind.OllamaEmbed)
            {
                var catalogue = draft!.DiscoverModelCatalogueAsync(service.Id, CancellationToken.None).GetAwaiter().GetResult();
                found = catalogue?.Where(pair => pair.Value.Supports(service.Capability) != false).Select(pair => pair.Key).ToArray();
                observedDimensions[GroupKey(service)] = catalogue?
                    .Where(pair => pair.Value.EmbeddingLength is int)
                    .ToDictionary(pair => pair.Key, pair => pair.Value.EmbeddingLength!.Value, StringComparer.Ordinal)
                    ?? new Dictionary<string, int>();
                modelDescriptions[GroupKey(service)] = catalogue?.ToDictionary(pair => pair.Key, pair =>
                    (pair.Value.SizeBytes is long size ? (size / 1_000_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " GB. " : "")
                    + Ui.Text(pair.Value.Supports(service.Capability) switch { true => "assist2.catalogueObserved", false => "assist2.catalogueUnsupported", _ => "assist2.catalogueUnknown" }), StringComparer.Ordinal)
                    ?? new Dictionary<string, string>();
            }
            else { found = draft!.DiscoverModelsAsync(service.Id, CancellationToken.None).GetAwaiter().GetResult(); }
            if (found is null) { Notice(Ui.Text("assist.discoveryUnavailable")); }
            return found;
        }
        catch (PaRouterException exception)
        {
            string reason = exception.Metadata.GetValueOrDefault("reason") ?? "";
            Notice(Ui.Text(reason is "secret_not_stored" or "credentials_refused" ? "assist.discoveryAuth"
                : reason == "endpoint_invalid" ? "assist.discoveryEndpoint" : "assist.discoveryUnavailable"));
            return null;
        }
        catch (OperationCanceledException) { Notice(Ui.Text("assist.discoveryTimeout")); return null; }
    }
    private string ModelDescription(ServiceDescriptor service, string model) => modelDescriptions.GetValueOrDefault(GroupKey(service))?.GetValueOrDefault(model) ?? "";

    /// <summary>Lunghezza del vettore osservata per questo modello, quando la scheda l'ha dichiarata.</summary>
    private int? ObservedDimension(ServiceDescriptor service, string model)
        => observedDimensions.GetValueOrDefault(GroupKey(service)) is { } observed && observed.TryGetValue(model, out int length) ? length : null;

    private void ModelLimits(string id)
    {
        string path = Ui.Text("Setup > Servizi > ") + InstanceName(draft!.Service(id)) + " > " + Ui.Text("assist2.modelLimits").Split(" - ", StringSplitOptions.None)[0];
        while (!interaction.Navigation.Pending)
        {
            Frame(path, Ui.Text("assist2.modelLimits"));
            string? field = interaction.Choose(Ui.Text("assist2.modelLimits"), [
                ("contextWindowTokens", SetupHelp.Label("contextWindowTokens")),
                ("maxOutputTokens", SetupHelp.Label("maxOutputTokens")), ("back", SetupHelp.Label("back"))]);
            if (field is null or "back") { return; }
            Form("service", id, path + " > " + SetupHelp.Name(field), field);
        }
    }
    private ServiceDescriptor[] Instance(ServiceDescriptor service) => [.. draft!.Health.Snapshot!.Registry.Services.Where(item => GroupKey(item) == GroupKey(service))];
    private static string GroupKey(ServiceDescriptor service) => ServiceSetupCatalog.InstanceKey(service);
    private static bool IsModel(ServiceDescriptor service) => service.Model is not null && !service.Model.Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal);
    // Il nome che si legge nel percorso e' quello della guida: l'identificatore del registro sta nei dettagli.
    private string InstanceName(ServiceDescriptor service) => ServiceNames.Instance(service);

    /// <summary>
    /// Chiede il nome dell'istanza. Vale per tutti i suoi modelli, e una riga vuota lo toglie facendo
    /// tornare il nome predefinito. Non tocca l'identificatore (S117).
    /// </summary>
    private void Rename(string id)
    {
        ServiceDescriptor service = draft!.Service(id);
        Frame(Ui.Text("Setup > Servizi > ") + InstanceName(service) + " > " + Ui.Text("service.renameTitle"), Ui.Text("service.renameWhy"));

        string? entered = interaction.Edit(Ui.Text("service.renameTitle"), service.Name ?? string.Empty);

        if (entered is null) { return; }

        var issues = draft.NameInstance(id, entered);
        Notice(issues.Count == 0
            ? Ui.Format("service.renamed {0}", InstanceName(draft.Service(id)))
            : Friendly(issues[0].Message));
    }
    private static string RawCurrent(string value) => value.Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal) ? "" : value;
    private static SetupVisualState ToVisual(SectionSeverity severity) => severity switch
    {
        SectionSeverity.Missing or SectionSeverity.Error => SetupVisualState.RequiredMissing,
        SectionSeverity.Warning => SetupVisualState.Warning,
        SectionSeverity.Ok => SetupVisualState.RequiredSatisfied,
        _ => SetupVisualState.Optional,
    };
    private static string SectionName(ConfigurationSection section) => section switch
    {
        ConfigurationSection.Provider => Ui.Text("Servizi"),
        ConfigurationSection.Models => Ui.Text("Modelli"),
        ConfigurationSection.Routing => Ui.Text("Profilo e routing"),
        ConfigurationSection.Privacy => Ui.Text("Privacy e attestazioni"),
        ConfigurationSection.Storage => Ui.Text("Conservazione"),
        _ => section.ToString(),
    };
    internal static string Friendly(string message)
    {
        message = Ui.Diagnostic(message);
        foreach (PolicyReason reason in Enum.GetValues<PolicyReason>())
        {
            string code = reason.ToWire();
            message = message.Replace(code, SetupHelp.Name(code), StringComparison.Ordinal);
        }
        return System.Text.RegularExpressions.Regex.Replace(message, "REPLACE_WITH_[A-Z0-9_]+", Ui.Text("valore da configurare"));
    }
}
