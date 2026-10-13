using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Repl;

/// <summary>Editor dei contratti esistenti: controlli tipizzati, nessun YAML o JSON richiesto.</summary>
internal sealed class SetupForm(
    ISetupInteraction interaction,
    SetupWorkingConfiguration draft,
    Action<string> notify,
    IReadOnlySet<string>? enabledOnDisk = null)
{
    private string message = "";
    private Func<bool> hasPendingChanges = () => false;
    private JsonObject? workingDocument;
    private JsonObject? fieldOwner;

    /// <summary>
    /// Vero se il campo che si sta modificando e' facoltativo: per un numero, una riga vuota vuol dire «assente», cioe' il
    /// comportamento previsto quando il campo non c'e' (per la spesa massima per richiesta: nessun tetto).
    /// </summary>
    private bool fieldOptional;
    private string? formKind;
    private string? formId;
    private readonly Dictionary<string, string> excluded = new(StringComparer.Ordinal);

    internal void Open(string kind, string? id, string path, string? section = null)
    {
        formKind = kind;
        formId = id;
        JsonObject document = draft.Document(kind, id);
        JsonObject schema = SetupContract.Schema(kind);
        JsonObject fields = schema["properties"]!.AsObject();
        foreach (string key in Managed(kind)) { fields.Remove(key); }
        if (kind == "service" && document["executable"] is not null)
        {
            foreach (string key in new[] { "endpoint", "model", "dimension", "fingerprint" }) { fields.Remove(key); }
        }
        if (kind == "service" && id is not null)
        {
            // La scheda di un servizio mostra i campi che **quel** servizio legge. A un Ollama non servono un programma
            // da avviare ne' una sessione da agente, e offrirglieli invita a scrivere una configurazione che la lettura
            // rifiuta (`promptCaching` e `nativeSession` fuori dal loro adapter) o che nessuno usera' mai.
            ServiceDescriptor described = draft.Service(id);
            bool program = described.Executable is not null || described.Kind == TargetKind.Agent;
            List<string> irrelevant = ["environment"];
            if (!program) { irrelevant.AddRange(["executable", "arguments", "nativeSession"]); }
            if (described.Adapter is not AdapterKind.ClaudeCodeCli) { irrelevant.Add("nativeSession"); }
            if (described.Adapter is not AdapterKind.Anthropic) { irrelevant.Add("promptCaching"); }
            if (described.Adapter is not AdapterKind.Ollama || described.Capability is not ServiceCapability.Chat) { irrelevant.Add("thinking"); }
            if (described.Adapter is not (AdapterKind.Ollama or AdapterKind.OllamaEmbed)) { irrelevant.Add("keepAliveMinutes"); }
            if (described.Adapter is not (AdapterKind.OpenAiCompatible or AdapterKind.Anthropic)) { irrelevant.Add("toolCalling"); }
            if (described.Capability is not ServiceCapability.Embedding) { irrelevant.AddRange(["dimension", "fingerprint"]); }
            if (described.AuthMode is AuthMode.None && document["secretRef"] is null) { irrelevant.Add("secretRef"); }
            foreach (string key in irrelevant) { fields.Remove(key); }
        }
        if (kind == "config" && fields["extensions"]?["properties"] is JsonObject extensions)
        {
            // Campo rimasto nello schema per i file gia' scritti: non concede nulla (S81) e non si offre da modificare.
            extensions.Remove("allowExecutableTools");
        }
        if (section is not null)
        {
            foreach (string key in fields.Select(pair => pair.Key).Where(key => key != section).ToArray()) { fields.Remove(key); }
        }
        JsonObject edited = (JsonObject)document.DeepClone();
        workingDocument = edited;
        hasPendingChanges = () => !JsonNode.DeepEquals(document, edited);
        while (true)
        {
            bool confirmed;
            if (section is not null && fields[section]?["type"]?.ToString() == "object")
            {
                edited[section] ??= new JsonObject();
                confirmed = Object(fields[section]!.AsObject(), edited[section]!.AsObject(), path, SetupHelp.Description(section));
            }
            else
            {
                confirmed = Object(schema, edited, path, kind switch
                {
                    "config" => Ui.Text("Gestisci le impostazioni generali con cui PA Router conserva i dati e svolge le richieste."),
                    "policy" => Ui.Text("Definisci quali dati, servizi e azioni ammettere con questo profilo."),
                    "service" => Ui.Text("Configura il collegamento e i requisiti dichiarati per questo servizio."),
                    _ => Ui.Text("Gestisci le informazioni dell'elenco dei servizi configurati."),
                });
            }
            if (interaction.Navigation.Pending)
            {
                string destination = interaction.Navigation.Take()!;
                if (JsonNode.DeepEquals(document, edited))
                {
                    interaction.Navigation.Request(destination);
                    return;
                }
                interaction.Frame(path, true, Ui.Text("Questa scheda contiene modifiche non ancora confermate."));
                string? action;
                do
                {
                    _ = interaction.Navigation.Take();
                    action = interaction.Choose(Ui.Text("Prima di lasciare la scheda"), [
                        ("confirm", Ui.Text("Conferma - applica questa scheda alla sessione.")),
                        ("discard", Ui.Text("Scarta scheda - conserva le altre modifiche della sessione.")),
                        ("back", Ui.Text("Torna alla scheda - continua a completarla."))]);
                    // S is advertised here too: confirm this sheet and continue to the real save,
                    // instead of consuming S and displaying the same confirmation forever.
                    if (interaction.Navigation.Destination == "save")
                    {
                        destination = interaction.Navigation.Take()!;
                        action = "confirm";
                    }
                } while (interaction.Navigation.Pending);
                if (action == "discard" || (action == "confirm" && Apply()))
                {
                    interaction.Navigation.Request(destination);
                    return;
                }
                continue;
            }
            if (!confirmed || Apply()) { return; }
        }

        bool Apply()
        {
            try
            {
                if (kind == "config" && !JsonNode.DeepEquals(document["tools"]?["attestations"], edited["tools"]?["attestations"]))
                {
                    if (!ConfirmAction("assistDeps.toolDeclaration", "assistDeps.toolDeclarationConfirm")) { return false; }
                }
                if (kind == "service" && draft.IdentityChanged(id!, edited))
                {
                    string warning = draft.ModelIdentityChanged(id!, edited) ? "assist2.modelReview" : "assist2.identityReview";
                    if (!ConfirmAction(warning, "assist2.confirm")) { return false; }
                }
                var issues = draft.ApplyDocument(kind, id, edited);
                if (issues.Count > 0) { Report(Ui.Text("Scheda non confermata: ") + string.Join("; ", issues.Select(issue => Ui.Diagnostic(issue.Message)))); return false; }
                Report(Ui.Text("Scheda confermata nella sessione. S Salva configurazione per persistere."));
                return true;
            }
            catch (ArgumentException exception) { Report(Ui.Diagnostic(exception.Message)); return false; }
        }
    }

    internal static IReadOnlySet<string> Managed(string kind) => kind switch
    {
        "config" => new HashSet<string>(["schemaVersion", "registryFile", "policyFiles", "defaultProfileId"]),
        "policy" => new HashSet<string>(["schemaVersion", "profileId"]),
        "registry" => new HashSet<string>(["schemaVersion", "services"]),
        _ => new HashSet<string>(["id", "adapter", "enabled", "attestations"]),
    };

    /// <summary>
    /// Un profilo dichiara le zone oppure la forma precedente, mai entrambe (ADR-029): la scheda mostra i campi della
    /// forma che il profilo usa. Offrire l'altra invitava a scrivere un documento che la lettura rifiuta; la forma
    /// precedente si converte con <c>pa config migrate --apply</c>, non aggiungendo campi a mano.
    /// </summary>
    private static bool RelevantProfileField(JsonObject value, string key) =>
        !value.ContainsKey("minimumDataClass")
        || key switch
        {
            "externalProcessing" or "maxCloudDataClass" => !value.ContainsKey("zones"),
            "zones" => !value.ContainsKey("externalProcessing") && !value.ContainsKey("maxCloudDataClass"),
            _ => true,
        };

    private bool Object(JsonObject schema, JsonObject value, string path, string description)
    {
        JsonObject initial = (JsonObject)value.DeepClone();
        while (!interaction.Navigation.Pending)
        {
            var properties = schema["properties"]?.AsObject() ?? new JsonObject();
            JsonObject? dynamicSchema = schema["additionalProperties"] as JsonObject
                ?? schema["patternProperties"]?.AsObject().FirstOrDefault().Value as JsonObject;
            HashSet<string> requiredKeys = schema["required"]?.AsArray()
                .Select(item => item?.ToString())
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal) ?? [];
            List<(string Id, string Label)> choices = [];
            foreach ((string key, JsonNode? field) in properties)
            {
                if (!RelevantToolField(value, key)) { continue; }
                if (!RelevantProfileField(value, key)) { continue; }
                // Un elenco che lo schema vuole sempre vuoto non ha nulla da modificare: mostrarlo portava a una scheda
                // senza tipo degli elementi, e aprirla faceva cadere il setup (`allowedExtensionPermissions`, S85).
                if (field?["type"]?.ToString() == "array" && field["maxItems"]?.GetValue<int>() == 0) { continue; }
                SetupVisualState state = FieldState(key, path, field!.AsObject(), value[key], requiredKeys.Contains(key));
                string help = key == "limits" && properties.ContainsKey("sources") ? "toolCallLimits" : key;
                // Un elenco dei servizi vuoto vuol dire tutti (ADR-032, fase 4): «nessuno» direbbe il contrario.
                string shown = key == "allowedServiceIds" && value[key] is JsonArray { Count: 0 } or null && key == "allowedServiceIds"
                    ? Ui.Text("value.allServices")
                    : SetupHelp.Value(value[key]);
                choices.Add((key, SetupVisualText.Symbol(state) + " " + SetupHelp.Name(help) + ": " + shown + " - " + SetupHelp.Description(help)));
            }
            foreach (string key in value.Select(pair => pair.Key).Where(key => !properties.ContainsKey(key)))
            {
                if (dynamicSchema is not null) { choices.Add((key, key + Ui.Text(" - elemento configurato; apri per modificarlo."))); }
            }
            if (dynamicSchema is not null) { choices.Add(("add", Ui.Text("Aggiungi elemento - crea una nuova voce di questa raccolta."))); }
            bool removable = value.Select(pair => pair.Key).Any(key => (properties.ContainsKey(key) || dynamicSchema is not null) && !requiredKeys.Contains(key));
            if (removable) { choices.Add(("remove", Ui.Text("Rimuovi campo opzionale - ripristina il comportamento previsto quando assente."))); }
            choices.Add(("confirm", Ui.Text("C Conferma scheda - Conserva questi valori nella sessione, senza scrivere su disco.")));
            choices.Add(("back", Ui.Text("B Indietro - torna al livello precedente.")));
            // Il titolo dice gia' a che cosa serve la scheda: qui si dice come si usa, senza ripeterlo.
            Frame(path, Ui.Text("Modifica i valori, poi C Conferma. Per scrivere su disco usa S Salva configurazione."));
            interaction.Details(properties.ToDictionary(pair => pair.Key,
                pair => SetupHelp.Details(pair.Key, SetupHelp.Description(pair.Key))
                    + " " + pair.Value?["description"] + Ui.Text(" Tipo: ") + pair.Value?["type"]
                    + (pair.Value?["minimum"] is { } min ? Ui.Text("; minimo: ") + min : "")
                    + (pair.Value?["maximum"] is { } max ? Ui.Text("; massimo: ") + max : "")
                    + (pair.Value?["pattern"] is { } pattern ? Ui.Text("; formato tecnico: ") + pattern : "")));
            string? keyChoice = interaction.Choose(path.Split(" > ", StringSplitOptions.None)[^1] + " - " + description, choices);
            if (interaction.Navigation.Pending) { return false; }
            if (keyChoice is null or "back")
            {
                if (JsonNode.DeepEquals(initial, value)) { return false; }
                string? leave = interaction.Choose(Ui.Text("Modifiche in questa scheda"), [("confirm", Ui.Text("Conferma - conserva le modifiche nella scheda superiore.")),
                    ("discard", Ui.Text("Scarta - annulla le modifiche di questa scheda.")), ("back", Ui.Text("Torna alla scheda - continua a modificare."))]);
                if (interaction.Navigation.Pending) { return false; }
                if (leave == "confirm") { return true; }
                if (leave == "discard" || (leave is null && interaction is not SetupInteraction)) { return false; }
                continue;
            }
            if (keyChoice == "confirm") { return true; }
            if (keyChoice == "remove")
            {
                string[] optional = [.. value.Select(pair => pair.Key).Where(key => (properties.ContainsKey(key) || dynamicSchema is not null)
                    && schema["required"]?.AsArray().Any(item => item?.ToString() == key) != true)];
                string? remove = interaction.Choose(Ui.Text("Campo da rimuovere"), [.. optional.Select(key => (key, SetupHelp.Label(key))), ("back", Ui.Text("Indietro - conserva i campi."))]);
                if (remove is not null and not "back") { value.Remove(remove); }
                continue;
            }
            if (keyChoice == "add")
            {
                if (schema["propertyNames"]?["enum"] is JsonArray names)
                {
                    keyChoice = interaction.Choose(Ui.Text("Tipo di elemento"), [.. names.Select(name => (name!.ToString(), SetupHelp.Label(name.ToString())))]);
                }
                else { keyChoice = interaction.Edit(Ui.Text("Nome dell'elemento - identificatore della nuova voce"), ""); }
                if (string.IsNullOrWhiteSpace(keyChoice)) { continue; }
            }
            JsonObject fieldSchema = (properties[keyChoice] as JsonObject ?? dynamicSchema)!;
            if (fieldSchema is null) { continue; }
            string fieldPath = path + " > " + SetupHelp.Name(keyChoice);
            bool existed = value.ContainsKey(keyChoice);
            JsonNode? before = value[keyChoice]?.DeepClone();
            Frame(fieldPath, SetupHelp.Description(keyChoice));
            string? fieldType = fieldSchema["type"] as JsonValue is { } typeValue ? typeValue.ToString() : null;
            // Le schede annidate condividono solo la copia locale: Home/Save possono vederne le modifiche pendenti.
            if (value[keyChoice] is null && fieldType == "object") { value[keyChoice] = new JsonObject(); }
            if (value[keyChoice] is null && fieldType == "array") { value[keyChoice] = new JsonArray(); }
            fieldOwner = value;
            fieldOptional = !requiredKeys.Contains(keyChoice);
            if (Edit(fieldSchema, value[keyChoice], keyChoice, fieldPath, out JsonNode? changed))
            {
                if (changed is null && fieldType is "number" or "integer" && fieldOptional)
                {
                    value.Remove(keyChoice);
                    continue;
                }
                if (keyChoice == "kind" && properties.ContainsKey("command") && changed?.ToString() != before?.ToString())
                {
                    string[] incompatible = changed?.ToString() == "mcp-stdio" ? ["url"] : ["command", "arguments"];
                    if (incompatible.Any(value.ContainsKey))
                    {
                        if (!ConfirmAction("assistDeps.toolSwitch", "assistDeps.toolSwitchConfirm")) { continue; }
                        foreach (string field in incompatible) { value.Remove(field); }
                    }
                }
                if (!ReferenceEquals(value[keyChoice], changed)) { value[keyChoice] = changed; }
            }
            else if (interaction.Navigation.Pending) { return false; }
            else if (existed) { value[keyChoice] = before; }
            else { value.Remove(keyChoice); }
        }
        return false;
    }

    private SetupVisualState FieldState(string key, string path, JsonObject schema, JsonNode? value, bool schemaRequired)
    {
        // Solo i problemi di **questa** scheda. Prima bastava che un campo con lo stesso nome avesse un problema in un
        // altro servizio — il programma mancante di un agente spento — perche' qui diventasse rosso e «obbligatorio».
        bool hasIssue = ScopedIssues().Any(issue =>
            issue.Path.TrimEnd('/').EndsWith("/" + key, StringComparison.Ordinal)
            || issue.Path.Contains("/" + key + "/", StringComparison.Ordinal));
        bool required = schemaRequired
            || hasIssue;

        // **Rosso e' un obbligo di adesso.** Un servizio che l'operatore non ha scelto non deve
        // niente a nessuno: il suo programma mancante, la sua valuta non impostata, la sua impronta
        // vuota non impediscono al router di funzionare. Segnarli obbligatori riempiva di rosso
        // l'intero setup di un router gia' funzionante — settantuno righe trovate dall'esploratore —
        // e toglieva ogni significato al colore proprio dove serviva. Restano grigi finche' non lo
        // si sceglie; dal momento in cui lo si sceglie, tornano rossi.
        if (formKind == "service" && formId is not null && !draft.Selected.Contains(formId) && !Enabled(formId))
        {
            required = false;
        }
        if (formKind == "service" && formId is not null && draft.Selected.Contains(formId) && IsMissing(value))
        {
            // Obbligatorio e' cio' che **questo** servizio legge: un programma per un agente, indirizzo e modello per
            // un server, la credenziale solo se si autentica con una chiave. Prima valevano tutti e quattro per
            // chiunque, e un Ollama mostrava rossi «Programma installato» e «Riferimento alla credenziale».
            ServiceDescriptor described = draft.Service(formId);
            bool program = described.Executable is not null || described.Kind == TargetKind.Agent;
            required |= key switch
            {
                "executable" => program,
                "endpoint" => !program,
                "model" => !program && described.Kind == TargetKind.Llm,
                "secretRef" => described.AuthMode == AuthMode.ApiKey,
                _ => false,
            };
        }
        if (!required) { return SetupVisualState.Optional; }
        // Un elenco vuoto e' un valore, non un vuoto da riempire, quando lo schema lo ammette: «nessuna regione
        // richiesta» e «nessun pacchetto ammesso» sono scelte valide, e segnarle rosse mandava a «completare» cio'
        // che era gia' completo. Rosso resta cio' che il caricatore rifiuta o che manca davvero.
        bool emptyListAllowed = value is JsonArray { Count: 0 } && (schema["minItems"]?.GetValue<int>() ?? 0) == 0;
        return hasIssue || (IsMissing(value) && !emptyListAllowed) ? SetupVisualState.RequiredMissing : SetupVisualState.RequiredSatisfied;
    }

    /// <summary>
    /// Vero se il servizio e' acceso nella configurazione salvata. Nella copia di lavoro `enabled` e'
    /// sempre falso fino al salvataggio (S116), quindi chiederlo alla bozza direbbe sempre no.
    /// </summary>
    private bool Enabled(string id) => enabledOnDisk?.Contains(id) == true;

    /// <summary>I problemi che il caricatore attribuisce al documento aperto in questa scheda, e a nessun altro.</summary>
    private IEnumerable<ValidationIssue> ScopedIssues()
    {
        IReadOnlyList<ValidationIssue> issues = draft.Health.Issues;
        if (formKind == "service" && formId is not null && draft.Health.Snapshot is { } snapshot)
        {
            int index = snapshot.Registry.Services.Select((service, position) => (service.Id, position))
                .Where(item => item.Id == formId).Select(item => item.position).DefaultIfEmpty(-1).First();
            string prefix = "/services/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";
            return issues.Where(issue => issue.Path.StartsWith(prefix, StringComparison.Ordinal));
        }
        if (formKind == "policy" && formId is not null)
        {
            return issues.Where(issue => issue.Source.Contains(formId, StringComparison.Ordinal));
        }
        return issues.Where(issue => !issue.Path.StartsWith("/services/", StringComparison.Ordinal)
            && !issue.Source.Contains("polic", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMissing(JsonNode? value) => value is null
        || string.IsNullOrWhiteSpace(value.ToString())
        || value.ToString().Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal)
        || value is JsonArray array && array.Count == 0;

    private bool Edit(JsonObject schema, JsonNode? current, string key, string path, out JsonNode? value)
    {
        value = current;
        Frame(path, SetupHelp.Description(key));
        if (schema.ContainsKey("const")) { Report(Ui.Text("Questo valore e' fissato dal contratto e non e' modificabile.")); return false; }
        if (schema["type"] is JsonArray nullableTypes && nullableTypes.Any(item => item!.ToString() == "null"))
        {
            string? nullableChoice = interaction.Choose(SetupHelp.Name(key), [("edit", Ui.Text("Imposta valore - configura un valore esplicito.")),
                ("null", Ui.Text("Nessun valore - Lascia questa impostazione senza un valore esplicito.")), ("back", Ui.Text("Indietro - conserva il valore attuale."))]);
            if (nullableChoice is null or "back") { return false; }
            if (nullableChoice == "null") { value = null; return true; }
        }
        string type = schema["type"] is JsonArray types ? types.First(item => item!.ToString() != "null")!.ToString() : schema["type"]?.ToString() ?? "string";
        if (type == "object")
        {
            JsonObject obj = current?.AsObject() ?? new JsonObject();
            if (!Object(schema, obj, path, SetupHelp.Description(key))) { return false; }
            value = obj;
            return true;
        }
        if (type == "array")
        {
            if (schema["items"] is not JsonObject itemSchema)
            {
                Report(Ui.Text("form.arrayWithoutItems"));
                return false;
            }
            JsonArray array = current?.AsArray() ?? new JsonArray();
            var options = Options(itemSchema, key);
            if (options is not null)
            {
                var selected = interaction.Many(SetupHelp.Name(key), options, new HashSet<string>(array.Select(item => item!.ToString())));
                if (selected is null) { return false; }
                value = new JsonArray([.. selected.Select(item => (JsonNode?)JsonValue.Create(item))]);
                return true;
            }
            while (!interaction.Navigation.Pending)
            {
                // Un elemento di un elenco non e' un campo facoltativo: una riga vuota non lo toglie.
                fieldOptional = false;
                Frame(path, SetupHelp.Description(key));
                List<(string Id, string Label)> choices = [.. array.Select((item, index) => (index.ToString(CultureInfo.InvariantCulture), ItemLabel(item, index)))];
                choices.Add(("add", Ui.Text("Aggiungi - inserisci un elemento della lista.")));
                choices.Add(("confirm", Ui.Text("C Conferma elenco - Conserva questa lista nella scheda, senza scrivere su disco.")));
                choices.Add(("back", Ui.Text("B Indietro - annulla le modifiche a questa lista.")));
                string? action = interaction.Choose(SetupHelp.Name(key), choices);
                if (action is null or "back") { return false; }
                if (action == "confirm") { value = array; return true; }
                if (action == "add")
                {
                    // Un nuovo oggetto appartiene alla scheda locale anche durante Home/Save:
                    // la conferma della scheda deve includere i campi appena compilati.
                    if (itemSchema["type"]?.ToString() == "object")
                    {
                        JsonObject pendingItem = new();
                        array.Add(pendingItem);
                        if (!Edit(itemSchema, pendingItem, key, path + Ui.Text(" > Nuovo elemento"), out _) && !interaction.Navigation.Pending) { array.Remove(pendingItem); }
                    }
                    else if (Edit(itemSchema, null, key, path + Ui.Text(" > Nuovo elemento"), out JsonNode? item)) { array.Add(item); }
                    continue;
                }
                int index = int.Parse(action, CultureInfo.InvariantCulture);
                string? operation = interaction.Choose(Ui.Text("Elemento ") + (index + 1), [("edit", Ui.Text("Modifica - cambia i dati dell'elemento.")), ("remove", Ui.Text("Rimuovi - elimina l'elemento da questa lista.")), ("back", Ui.Text("Indietro - conserva l'elemento."))]);
                if (operation == "remove") { array.RemoveAt(index); }
                else if (operation == "edit" && Edit(itemSchema, array[index], key, path + Ui.Text(" > Elemento ") + (index + 1), out JsonNode? replacement)
                    && !ReferenceEquals(array[index], replacement)) { array[index] = replacement; }
            }
            return false;
        }
        var optionsForValue = Options(schema, key);
        if (type == "string" && key is "regions" or "processingRegions")
        {
            optionsForValue = [.. Regions.GroupNames.Select(group => (group, SetupHelp.Name(group) + " - " + Ui.Text("assistDeps.group"))),
                ("manual", Ui.Text("assistDeps.regionManual"))];
        }
        if (type == "boolean") { optionsForValue = [("true", Ui.Text("Si - ") + SetupHelp.BooleanDescription(key, true)), ("false", Ui.Text("No - ") + SetupHelp.BooleanDescription(key, false))]; }
        if (optionsForValue is not null)
        {
            var options = optionsForValue.Select(option => (option.Id, (current?.ToString().Equals(option.Id, StringComparison.OrdinalIgnoreCase) == true ? "(x) " : "( ) ") + option.Label)).ToList();
            if (schema["type"] is JsonArray nullable && nullable.Any(item => item!.ToString() == "null")) { options.Add(("null", Ui.Text("Nessuno - Non associare un servizio a questa impostazione."))); }
            options.Add(("back", Ui.Text("Indietro - conserva la scelta attuale.")));
            string? choice;
            do
            {
                interaction.Details(excluded);
                choice = interaction.Choose(SetupHelp.Name(key), options);
                if (choice is not null && excluded.TryGetValue(choice, out string? reason))
                {
                    Report(reason);
                    Frame(path, reason);
                }
            } while (choice is not null && excluded.ContainsKey(choice) && !interaction.Navigation.Pending);
            if (choice is null or "back") { return false; }
            if (choice == "manual") { return ManualReference(key, current, path, out value); }
            value = choice == "null" ? null : type == "boolean" ? JsonValue.Create(choice == "true") : JsonValue.Create(choice);
            return true;
        }
        while (!interaction.Navigation.Pending)
        {
            // La spesa massima per richiesta (ADR-032, fase 5) si scrive nella valuta del registro; se nessun servizio ha un
            // prezzo si dice a chi vale, invece di nascondere il campo.
            string? currency = key == "maxCostPerRequest" ? draft.Health.Snapshot?.Registry.Currency : null;
            Frame(path, SetupHelp.Description(key)
                + (key == "maxCostPerRequest" && currency is null ? " " + Ui.Text("limits.cost.noPrice") : string.Empty));
            string hint = schema["format"]?.ToString() == "date-time" ? Ui.Text(" - data ISO, esempio 2026-12-31T23:59:00+01:00") : "";
            if (schema["minimum"] is not null) { hint += Ui.Text(" - minimo ") + schema["minimum"]; }
            if (schema["maximum"] is not null) { hint += Ui.Text(", massimo ") + schema["maximum"]; }
            if (key == "maxCostPerRequest")
            {
                hint += currency is null ? Ui.Text("limits.cost.noCurrency") : Ui.Format("limits.cost.currency {0}", currency);
            }
            string? entered = interaction.Edit(SetupHelp.Label(key) + hint, current?.ToString() ?? "");
            if (entered is null) { return false; }
            if (type is "integer" or "number" && string.IsNullOrWhiteSpace(entered) && fieldOptional)
            {
                // Vuoto: il campo torna assente, e con lui il comportamento di quando non c'e'.
                value = null;
                return true;
            }
            if (type is "integer" or "number")
            {
                bool parsed = double.TryParse(entered, NumberStyles.Float, CultureInfo.InvariantCulture, out double number);
                if (!parsed || !double.IsFinite(number)
                    || (type == "integer" && number != Math.Truncate(number))
                    || (schema["exclusiveMinimum"] is JsonNode exclusive && number <= exclusive.GetValue<double>())
                    || (schema["minimum"] is JsonNode minimum && number < minimum.GetValue<double>())
                    || (schema["maximum"] is JsonNode maximum && number > maximum.GetValue<double>()))
                { _ = interaction.Navigation.Take(); Report(Ui.Text("Numero non valido: rispetta unita' e limiti indicati.")); continue; }
                value = type == "integer" ? JsonValue.Create((long)number) : JsonValue.Create(number);
            }
            else
            {
                if (schema["pattern"] is JsonNode pattern && !Regex.IsMatch(entered, pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                { _ = interaction.Navigation.Take(); Report(Ui.Text("Formato non valido. Apri I Dettagli per il vincolo richiesto.")); continue; }
                if (schema["format"]?.ToString() == "date-time" && (!Regex.IsMatch(entered, @"T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                    || !DateTimeOffset.TryParse(entered, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
                { _ = interaction.Navigation.Take(); Report(Ui.Text("Data non valida: includi data, ora e fuso orario.")); continue; }
                value = JsonValue.Create(entered);
            }
            return true;
        }
        return false;
    }

    private List<(string Id, string Label)>? Options(JsonObject schema, string key)
    {
        excluded.Clear();
        if (schema["enum"] is JsonArray entries) { return [.. entries.Select(entry => (entry!.ToString(), SetupHelp.Label(entry.ToString())))]; }
        if (key is "allowedServiceIds" or "embeddingServiceId")
        {
            List<(string Id, string Label)> services = [];
            foreach (ServiceDescriptor service in draft.Health.Snapshot!.Registry.Services)
            {
                if (key != "allowedServiceIds" && service.Capability != ServiceCapability.Embedding) { continue; }
                string label = service.Id + " - " + SetupHelp.Value(JsonValue.Create(service.Model)) + "; " + service.Endpoint;
                string? reason = key == "allowedServiceIds" ? null : ReferenceIssue(key, service.Id);
                AddReference(services, service.Id, label, reason);
            }
            if (key != "allowedServiceIds") { services.Add(("manual", Ui.Text("assistDeps.manualReference"))); }
            return services;
        }
        if (key == "serviceProfileId")
        {
            List<(string Id, string Label)> profiles = [];
            foreach (string id in draft.Health.Snapshot!.Profiles.Keys)
            {
                AddReference(profiles, id, ProfilePresentation.Label(draft.Health.Snapshot!, id, SystemClock.Instance), ReferenceIssue(key, id));
            }
            profiles.Add(("manual", Ui.Text("assistDeps.manualReference")));
            return profiles;
        }
        if (key == "source")
        {
            JsonArray sources = workingDocument?["tools"]?["sources"] as JsonArray
                ?? draft.Document("config")["tools"]?["sources"] as JsonArray ?? [];
            return [.. sources.OfType<JsonObject>().Where(source => source["id"] is not null)
                .Select(source => (source["id"]!.ToString(), source["id"] + " - " + SetupHelp.Description("sources"))),
                ("manual", Ui.Text("assistDeps.manualReference"))];
        }
        return null;
    }

    private void AddReference(List<(string Id, string Label)> choices, string id, string label, string? reason)
    {
        if (reason is null) { choices.Add((id, label)); return; }
        string excludedId = "excluded:" + id;
        excluded[excludedId] = reason;
        choices.Add((excludedId, label + " - " + Ui.Text("assistDeps.excluded")));
    }

    private string? ReferenceIssue(string key, string id)
    {
        var snapshot = draft.Health.Snapshot!;
        ConfigDocument config = snapshot.Config;
        string pointer;
        if (key == "serviceProfileId")
        {
            config = config with { Api = config.Api with { ServiceProfileId = id } };
            pointer = "/api/serviceProfileId";
        }
        else
        {
            string? local = workingDocument?["memory"] is JsonObject memory && memory.ContainsKey("embeddingServiceId")
                ? memory["embeddingServiceId"]?.GetValue<string>() : config.Memory.EmbeddingServiceId;
            config = config with { Memory = config.Memory with { EmbeddingServiceId = key == "embeddingServiceId" ? id : local } };
            pointer = "/memory/" + key;
        }
        List<ValidationIssue> issues = [];
        SemanticValidator.Validate(config, snapshot.Registry, [.. snapshot.Profiles.Values], "registry.yaml",
            snapshot.Profiles.Keys.ToDictionary(profile => profile, profile => profile), SystemClock.Instance, issues);
        string[] reasons = [.. issues.Where(issue => issue.Path == pointer).Select(issue => Ui.Diagnostic(issue.Message))];
        return reasons.Length == 0 ? null : string.Join("; ", reasons);
    }

    private bool ManualReference(string key, JsonNode? current, string path, out JsonNode? value)
    {
        value = current;
        while (!interaction.Navigation.Pending)
        {
            Frame(path, SetupHelp.Description(key));
            string? entered = interaction.Edit(SetupHelp.Label(key), current?.ToString() ?? "");
            if (entered is null) { return false; }
            if (key is "regions" or "processingRegions")
            {
                ProcessingRegion region = Regions.Parse(entered);
                if (!Regions.IsWellFormed(region)) { Report(Ui.Text("assistDeps.regionInvalid")); continue; }
                entered = region.Code;
            }
            value = JsonValue.Create(entered);
            return true;
        }
        return false;
    }

    private static bool RelevantToolField(JsonObject value, string key) => value["kind"]?.ToString() switch
    {
        "mcp-stdio" => key != "url",
        "mcp" or "openapi" => key is not "command" and not "arguments",
        _ => true,
    };

    private bool ConfirmAction(string title, string confirmation)
    {
        string? destination = null;
        while (true)
        {
            string? choice = interaction.Choose(Ui.Text(title), [("confirm", Ui.Text(confirmation)), ("back", Ui.Text("Indietro - conserva la scelta attuale."))]);
            if (interaction.Navigation.Pending) { destination = interaction.Navigation.Take(); continue; }
            if (choice != "confirm") { return false; }
            if (destination is not null) { interaction.Navigation.Request(destination); }
            return true;
        }
    }

    private static string ItemLabel(JsonNode? item, int index) => item is JsonObject obj
        ? (obj["id"] ?? obj["tool"] ?? obj["source"])?.ToString() + Ui.Text(" - scheda elemento ") + (index + 1)
        : SetupHelp.Value(item) + Ui.Text(" - elemento ") + (index + 1) + Ui.Text("; apri per modificare o rimuovere.");
    private void Report(string text) { message = text; notify(text); }
    private void Frame(string path, string description)
    {
        interaction.Page(new SetupPage(path, description) { Notice = message, Dirty = draft.IsDirty || hasPendingChanges() }.Checked());
        message = "";
    }
}
