using System.Globalization;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Hosting.Runtime;

namespace PA.Router.Cli.Repl;

internal sealed partial class SetupScreen
{
    private void ApplyField(string id, string key, string value)
    {
        string? destination = interaction.Navigation.Take();
        bool applied = false;
        try
        {
            var service = draft!.Service(id);
            JsonObject edited = draft.Document("service", id);
            edited[key] = value;
            if (draft.IdentityChanged(id, edited) && (key != "model" || IsModel(service))
                && !ConfirmIdentityChange(key == "model")) { return; }
            destination = interaction.Navigation.Take() ?? destination;
            int? dimension = key == "model" && service.Capability == ServiceCapability.Embedding && service.Model != value ? ReadEmbeddingDimension(service, value) : null;
            if (key == "model" && service.Capability == ServiceCapability.Embedding && service.Model != value && dimension is null) { return; }
            int? output = key == "model" && service.Adapter == AdapterKind.Anthropic && service.Model != value ? ReadOutputLimit(value) : null;
            if (key == "model" && service.Adapter == AdapterKind.Anthropic && service.Model != value && output is null) { return; }
            var issues = key == "model" ? draft.ChangeModel(id, value, dimension, output) : draft.EditService(id, key, JsonValue.Create(value));
            applied = issues.Count == 0;
            if (issues.Count > 0) { _ = interaction.Navigation.Take(); }
            Notice(issues.Count == 0 ? Ui.Text("Modifica confermata nella sessione.") : Friendly(issues[0].Message));
        }
        finally { if (applied && !interaction.Navigation.Pending && destination is not null) { interaction.Navigation.Request(destination); } }
    }

    private bool ConfirmIdentityChange(bool model)
    {
        string? destination = interaction.Navigation.Take();
        string? choice;
        do
        {
            choice = interaction.Choose(Ui.Text(model ? "assist2.modelReview" : "assist2.identityReview"),
                [("yes", Ui.Text("assist2.confirm")), ("back", SetupHelp.Label("back"))]);
            if (interaction.Navigation.Take() is string requested) { destination = requested; }
            else { break; }
        } while (true);
        bool confirmed = choice == "yes";
        if (confirmed && !interaction.Navigation.Pending && destination is not null) { interaction.Navigation.Request(destination); }
        return confirmed;
    }

    private bool ConfirmConnectionChange(string id, string endpoint, Placement placement)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var address) || !PlacementRules.IsAcceptable(placement, address))
        { return true; } // Let the existing loader produce the precise inline error, without a pointless confirmation.
        JsonObject edited = draft!.Document("service", id);
        edited["endpoint"] = endpoint;
        edited["inferencePlacement"] = placement.ToWire();
        return !draft.IdentityChanged(id, edited) || ConfirmIdentityChange(false);
    }

    internal static string NormalizeEndpoint(string endpoint, string placement, bool ollama = true)
    {
        if (endpoint.Contains("://", StringComparison.Ordinal)) { return endpoint; }
        string scheme = placement is "loopback" or "lan" ? "http://" : "https://";
        if (!endpoint.StartsWith('[') && System.Net.IPAddress.TryParse(endpoint, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        { endpoint = "[" + endpoint + "]"; }
        if (ollama && placement is "loopback" or "lan" && Uri.TryCreate(scheme + endpoint, UriKind.Absolute, out var parsed) && parsed.IsDefaultPort
            && (endpoint.EndsWith(']') || !endpoint.Contains(':', StringComparison.Ordinal)))
        { endpoint += ":11434"; }
        return scheme + endpoint;
    }

    /// <summary>
    /// La lunghezza del vettore: **osservata** sulla scheda del modello quando c'e', chiesta solo quando il modello non
    /// la dichiara. Chiederla sempre significava accettare un numero che l'operatore non puo' conoscere, e un valore
    /// sbagliato fa rifiutare ogni vettore: la memoria resta lessicale per sempre, senza un errore visibile.
    /// </summary>
    private int? ReadEmbeddingDimension(ServiceDescriptor service, string model)
    {
        if (ObservedDimension(service, model) is int observed)
        {
            Notice(Ui.Format("assist2.dimensionObserved {0} {1}", model, observed.ToString(CultureInfo.InvariantCulture)));
            return observed;
        }

        Frame(breadcrumb + " > " + SetupHelp.Name("dimension"), model + " - " + Ui.Text("assist2.embeddingDimension"));
        string? entered = interaction.Edit(SetupHelp.Label("dimension"), "");
        if (entered is null) { return null; }
        if (!int.TryParse(entered, out int dimension) || dimension <= 0)
        { Notice(Ui.Text("assist2.dimensionInvalid")); return null; }
        return dimension;
    }

    private IReadOnlyDictionary<string, int>? ReadEmbeddingDimensions(ServiceDescriptor service, IReadOnlyList<string> models)
    {
        Dictionary<string, int> dimensions = new(StringComparer.Ordinal);
        if (service.Capability != ServiceCapability.Embedding) { return dimensions; }
        string parent = breadcrumb;
        string? destination = interaction.Navigation.Take();
        foreach (string model in models.Distinct(StringComparer.Ordinal).Where(model => !Instance(service).Any(item => item.Model == model)))
        {
            breadcrumb = parent;
            int? dimension = ReadEmbeddingDimension(service, model);
            if (dimension is null) { return null; }
            destination = interaction.Navigation.Take() ?? destination;
            dimensions[model] = dimension.Value;
        }
        if (destination is not null) { interaction.Navigation.Request(destination); }
        return dimensions;
    }

    private int? ReadOutputLimit(string model)
    {
        Frame(breadcrumb + " > " + SetupHelp.Name("maxOutputTokens"), model + " - " + SetupHelp.Description("maxOutputTokens"));
        string? entered = interaction.Edit(SetupHelp.Label("maxOutputTokens"), "");
        return int.TryParse(entered, out int limit) && limit > 0 ? limit : null;
    }

    private IReadOnlyDictionary<string, int>? ReadOutputLimits(ServiceDescriptor service, IReadOnlyList<string> models)
    {
        Dictionary<string, int> limits = new(StringComparer.Ordinal);
        if (service.Adapter != AdapterKind.Anthropic) { return limits; }
        string parent = breadcrumb;
        string? destination = interaction.Navigation.Take();
        foreach (string model in models.Distinct(StringComparer.Ordinal).Where(model => !Instance(service).Any(item => item.Model == model)))
        {
            breadcrumb = parent;
            int? limit = ReadOutputLimit(model);
            if (limit is null) { return null; }
            destination = interaction.Navigation.Take() ?? destination;
            limits[model] = limit.Value;
        }
        if (destination is not null) { interaction.Navigation.Request(destination); }
        return limits;
    }

    private void Admit(IReadOnlyList<string> ids)
    {
        var missing = draft!.AdmissionRequired(ids);
        if (missing.Count == 0) { return; }
        Frame(breadcrumb + " > " + Ui.Text("assist2.admissionTitle"), Ui.Text("assist2.admissionHelp") + " "
            + ProfilePresentation.Name(draft.Health.Snapshot, draft.ProfileId) + ": " + string.Join(", ", missing.Select(id =>
                draft.Service(id) is { } item && IsModel(item) ? item.Model! : InstanceName(draft.Service(id)))));
        if (interaction.Choose(Ui.Text("Ammettere i nuovi modelli nel profilo corrente?"),
            [("yes", Ui.Text("Si - Ammetti i nuovi modelli; privacy e altri requisiti continuano ad applicarsi.")),
                ("no", Ui.Text("No - Conserva i nuovi modelli nella configurazione, ma escludili da questo profilo."))]) != "yes") { return; }
        var issues = draft.AdmitServices(missing);
        Notice(issues.Count == 0 ? Ui.Text("Target ammesso nel profilo; gli altri vincoli restano attivi.") : Friendly(issues[0].Message));
    }

    private IReadOnlyList<string>? DeclarationScope(ServiceDescriptor service)
    {
        ServiceDescriptor[] models = Instance(service).Where(item => IsModel(item) || item.Id == service.Id).ToArray();
        if (models.Length == 1) { return [service.Id]; }
        Frame(breadcrumb + " > " + Ui.Text("assist2.scopeTitle"), Ui.Text("assist2.scopeHelp"));
        return interaction.Many(Ui.Text("assist2.scopeTitle"), [.. models.Select(model => (model.Id,
            (model.Model ?? model.Id) + " - " + Ui.Text("assist2.scopeModel")))], new HashSet<string> { service.Id });
    }

    private string? Manual(string key, string? current)
    {
        Frame(breadcrumb + " > " + SetupHelp.Name(key), SetupHelp.Description(key));
        return interaction.Edit(SetupHelp.Label(key), RawCurrent(current ?? ""));
    }

    private string? ChooseModel(ServiceDescriptor service, bool adding = false)
    {
        string path = breadcrumb + " > " + SetupHelp.Name("model");
        while (!interaction.Navigation.Pending)
        {
            IReadOnlyList<string>? models = service.Adapter == AdapterKind.OnnxLocal ? draft!.InstalledLocalModels
                : observations.GetValueOrDefault(GroupKey(service));
            if (models is { Count: 0 })
            {
                Notice(service.Adapter == AdapterKind.OnnxLocal ? Ui.Format("assist.emptyLocalModels {0}", draft!.LocalModelsDirectory) : Ui.Text("assist.emptyModels"));
            }
            Frame(path, Ui.Text("assist.modelHelp") + " " + service.Endpoint);
            List<(string Id, string Label)> choices = [.. (models ?? []).Select(model => ("pick:" + model,
                (!adding && model == service.Model ? "(x) " : "( ) ") + model + " - " + Ui.Text("assist.discoveredModel") + " " + ModelDescription(service, model)))];
            var excluded = modelDescriptions.GetValueOrDefault(GroupKey(service))?.Keys.Where(model => !(models ?? []).Contains(model)).ToArray() ?? [];
            if (excluded.Length > 0) { Frame(path, Ui.Text("assist2.catalogueUnsupported") + " " + string.Join(", ", excluded)); }
            if (ServiceSetupCatalog.Describe(service).DiscoverModels)
            {
                choices.Add(("discover", Ui.Text(models is null ? "assist.discover" : "assist.refresh")));
            }
            else if (service.Adapter == AdapterKind.OnnxLocal) { choices.Add(("refresh", Ui.Text("assist.refreshLocal"))); }
            choices.Add(("manual", Ui.Text("assist.manualModel")));
            choices.Add(("back", SetupHelp.Label("back")));
            string? choice = interaction.Choose(Ui.Text("assist.modelTitle"), choices);
            if (choice is null or "back") { return null; }
            if (choice == "manual") { return Manual("model", adding ? "" : service.Model); }
            if (choice == "refresh") { continue; }
            if (choice == "discover")
            {
                var found = Discover(service);
                if (found is not null) { observations[GroupKey(service)] = found; }
                continue;
            }
            return choice["pick:".Length..];
        }
        return null;
    }

    private string? ChooseExecutable(ServiceDescriptor service)
    {
        string path = breadcrumb + " > " + SetupHelp.Name("executable");
        while (!interaction.Navigation.Pending)
        {
            var found = SetupDiscovery.Executables(service);
            Frame(path, Ui.Text("assist.programHelp") + (found.Count == 0 ? " " + Ui.Text("assist.noPrograms") : ""));
            string? choice = interaction.Choose(Ui.Text("assist.programTitle"), [.. found.Select(file => ("pick:" + file,
                (file == service.Executable ? "(x) " : "( ) ") + file + " - " + Ui.Text("assist.foundProgram"))),
                ("refresh", Ui.Text("assist.refreshLocal")), ("browse", Ui.Text("assist.browse")),
                ("manual", Ui.Text("assist.manualPath")), ("back", SetupHelp.Label("back"))]);
            if (choice is null or "back") { return null; }
            if (choice == "refresh") { continue; }
            if (choice == "manual") { return Manual("executable", service.Executable); }
            if (choice == "browse")
            {
                string? file = BrowseProgram(service.Executable);
                if (file is not null) { return file; }
                continue;
            }
            return choice["pick:".Length..];
        }
        return null;
    }

    private string? BrowseProgram(string? current)
    {
        string directory = File.Exists(current) ? Path.GetDirectoryName(current)! : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Path.IsPathFullyQualified(directory)) { directory = Environment.CurrentDirectory; }
        string path = breadcrumb + " > " + Ui.Text("assist.browseTitle");
        while (!interaction.Navigation.Pending)
        {
            var listing = new ReplFileMenu(Spectre.Console.AnsiConsole.Console, directory).Discover("");
            Frame(path, directory + " - " + Ui.Text("assist.browseHelp")
                + (listing.Error is null ? "" : " " + listing.Error)
                + (listing.Limited ? " " + Ui.Text("assist.browseLimited") : ""));
            var choices = listing.Entries.Select((entry, index) => (index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                entry.Name + " - " + Ui.Text(entry.Directory ? "assist.openFolder" : "assist.chooseFile"))).ToList();
            if (Directory.GetParent(directory) is not null) { choices.Add(("parent", Ui.Text("assist.parent"))); }
            choices.Add(("back", SetupHelp.Label("back")));
            string? choice = interaction.Choose(Ui.Text("assist.browseTitle"), choices);
            if (choice is null or "back") { return null; }
            if (choice == "parent") { directory = Directory.GetParent(directory)!.FullName; continue; }
            var chosen = listing.Entries[int.Parse(choice, System.Globalization.CultureInfo.InvariantCulture)];
            string absolute = Path.GetFullPath(chosen.Path, directory);
            if (chosen.Directory) { directory = absolute; continue; }
            if (File.Exists(absolute)) { return absolute; }
        }
        return null;
    }

    private void ConfigureCredential(string id)
    {
        ServiceDescriptor service = draft!.Service(id);
        Frame(breadcrumb + " > " + SetupHelp.Name("secretRef"), Ui.Text("assist.credentialHelp"));
        string? choice = interaction.Choose(Ui.Text("assist.credentialTitle"), [.. draft.SecretReferences.Select(reference => ("pick:" + reference,
            (reference == service.SecretRef ? "(x) " : "( ) ") + reference + " - " + Ui.Text("assist.existingCredential"))),
            ("new", Ui.Text("assist.newCredential")), ("manual", Ui.Text("assist.manualReference")), ("back", SetupHelp.Label("back"))]);
        if (choice is null or "back") { return; }
        if (choice == "new")
        {
            string? secret = interaction.Edit(Ui.Text("Chiave API - Il valore e' mascherato; Salva configurazione lo conserva cifrato"), "", true);
            if (!string.IsNullOrWhiteSpace(secret) && ConfirmIdentityChange(false)) { draft.SetInstanceSecret(id, secret); Notice(Ui.Text("Credenziale acquisita nella sessione; verra' protetta al salvataggio.")); }
        }
        else
        {
            string? reference = choice == "manual" ? Manual("secretRef", service.SecretRef) : choice["pick:".Length..];
            if (reference is not null && (reference == service.SecretRef || ConfirmIdentityChange(false)))
            {
                var issues = draft.EditInstanceSecretReference(id, reference);
                Notice(issues.Count == 0 ? Ui.Text("Modifica confermata nella sessione.") : Friendly(issues[0].Message));
            }
        }
        observations.Clear(); // A catalogue observed with a different credential must be read again.
    }
}
