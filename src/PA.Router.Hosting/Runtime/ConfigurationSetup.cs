using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>Che cosa si sta correggendo.</summary>
public enum SetupRemedy
{
    /// <summary>Non esiste una directory dati: va creata.</summary>
    CreateWorkspace,

    /// <summary>La configurazione e' in una forma superata: la converte <c>pa config migrate</c>.</summary>
    Migrate,

    /// <summary>Quale servizio si vuole usare. Non scrive nulla: sceglie di che cosa parlare.</summary>
    ChooseService,

    /// <summary>Un servizio del registro va abilitato.</summary>
    EnableService,

    /// <summary>Il modello di un servizio e' ancora un segnaposto.</summary>
    SetModel,

    /// <summary>Il percorso del programma di un servizio e' ancora un segnaposto.</summary>
    SetExecutable,

    /// <summary>L'indirizzo di un servizio e' ancora un segnaposto.</summary>
    SetEndpoint,

    /// <summary>Il profilo predefinito non esiste, o non ammette nessun servizio abilitato.</summary>
    ChooseProfile,

    /// <summary>Nulla da scrivere: va osservato o attestato dall'operatore fuori di qui.</summary>
    OperatorAction,
}

/// <summary>
/// Un passo del configuratore: che cosa manca, che cosa si chiede e su che cosa si scrive.
/// </summary>
/// <param name="Remedy">Tipo di correzione.</param>
/// <param name="Section">Sezione della configurazione a cui appartiene.</param>
/// <param name="Title">Che cosa risulta, in una riga.</param>
/// <param name="Question">Domanda da porre all'operatore.</param>
/// <param name="ServiceId">Servizio a cui si applica, quando la correzione riguarda un servizio.</param>
/// <param name="Current">Valore attuale, da mostrare come punto di partenza.</param>
/// <param name="Choices">Valori ammessi, quando sono un insieme chiuso; vuoto quando il valore e' libero.</param>
public sealed record SetupStep(
    SetupRemedy Remedy,
    ConfigurationSection Section,
    string Title,
    string Question,
    string? ServiceId = null,
    string? Current = null,
    IReadOnlyList<string>? Choices = null)
{
    /// <summary>Vero se il passo si applica scrivendo un valore dato dall'operatore.</summary>
    public bool Writes => Remedy
        is SetupRemedy.EnableService
        or SetupRemedy.SetModel
        or SetupRemedy.SetExecutable
        or SetupRemedy.SetEndpoint
        or SetupRemedy.ChooseProfile;
}

/// <summary>
/// Il configuratore guidato, come **modello**: che cosa c'e' da correggere e come si corregge.
///
/// Sta qui e non nel terminale perche' l'avvio e <c>/setup</c> devono porre le stesse domande e
/// scrivere gli stessi file (INV-22). Il terminale ne e' la resa: disegna i passi, raccoglie le
/// risposte e le riporta qui. Una seconda copia della regola «che cosa manca» avrebbe prodotto un
/// avvio che chiede una cosa e un comando che ne chiede un'altra.
///
/// Non scrive nulla da solo: le modifiche passano da <see cref="RegistryRewrite"/>, che tiene
/// l'originale accanto, rilegge e **rimette il file di prima** se il risultato non si carica. Un
/// configuratore che lasciasse al suo posto un file rotto sarebbe peggio dell'errore che voleva
/// correggere.
/// </summary>
public static class ConfigurationSetup
{
    /// <summary>Suffisso delle copie lasciate accanto ai file toccati dal configuratore.</summary>
    public const string BackupSuffix = ".before-setup";

    /// <summary>
    /// I passi da proporre, nell'ordine in cui conviene farli.
    ///
    /// L'ordine non e' estetico: senza directory non c'e' nulla da correggere, e senza un servizio
    /// abilitato chiedere quale modello usi non ha risposta. Ogni passo presuppone che i precedenti
    /// siano stati fatti, ed e' per questo che dopo ciascuno si riesamina invece di proseguire con
    /// un elenco calcolato una volta sola.
    /// </summary>
    /// <param name="report">Esame della configurazione.</param>
    public static IReadOnlyList<SetupStep> Plan(ConfigurationReport report) => Plan(report, null);

    /// <summary>
    /// I passi da proporre per un servizio scelto.
    ///
    /// **L'ordine e' un vincolo del prodotto, non una preferenza.** Un servizio disabilitato puo'
    /// portare segnaposto — e' il default dei file consegnati — ma un servizio **abilitato** che li
    /// porta ancora rende la configurazione invalida, e lo stesso vale per un'attestazione senza
    /// scadenza. Chiedere prima di abilitare e poi di indicare il modello produrrebbe una scrittura
    /// che il riscrittore annulla ogni volta: l'operatore risponderebbe correttamente a ogni domanda
    /// e non arriverebbe da nessuna parte.
    ///
    /// Quindi: si sceglie il servizio, si riempiono i suoi campi mentre e' ancora spento, si dice cio'
    /// che deve attestare chi ha verificato la macchina, e solo alla fine lo si abilita.
    /// </summary>
    /// <param name="report">Esame della configurazione.</param>
    /// <param name="serviceId">Servizio su cui si sta lavorando; null per farlo scegliere.</param>
    public static IReadOnlyList<SetupStep> Plan(ConfigurationReport report, string? serviceId)
    {
        ArgumentNullException.ThrowIfNull(report);

        switch (report.Readiness)
        {
            case ConfigurationReadiness.Missing:
                return
                [
                    new SetupStep(
                        SetupRemedy.CreateWorkspace,
                        ConfigurationSection.Storage,
                        "nessuna configurazione in questa directory",
                        "Creo la configurazione iniziale qui?",
                        Choices: ["si", "no"]),
                ];

            case ConfigurationReadiness.MigrationRequired:
                return
                [
                    new SetupStep(
                        SetupRemedy.Migrate,
                        ConfigurationSection.Storage,
                        "configurazione in una forma superata",
                        "Converti con 'pa config migrate --apply', poi rientra qui."),
                ];

            case ConfigurationReadiness.Invalid:
                // Un documento che non si carica non ha uno snapshot da cui ricavare domande: i
                // problemi sono gia' nel rapporto, e indovinare quale riga l'operatore volesse
                // scrivere significherebbe riscrivergli il file.
                return [];
        }

        return report.Snapshot is not ConfigurationSnapshot snapshot
            ? []
            : [.. StepsFor(report, snapshot, serviceId)];
    }

    private static IEnumerable<SetupStep> StepsFor(
        ConfigurationReport report,
        ConfigurationSnapshot snapshot,
        string? serviceId)
    {
        IReadOnlyList<ServiceDescriptor> services = snapshot.Registry.Services;

        if (Target(snapshot, serviceId) is not ServiceDescriptor service)
        {
            yield return new SetupStep(
                SetupRemedy.ChooseService,
                ConfigurationSection.Provider,
                "nessun servizio abilitato",
                "Quale servizio vuoi usare?",
                Choices: [.. ProfileCandidates(snapshot).Select(candidate => candidate.Id)]);

            yield break;
        }

        bool ready = true;

        if (service.Executable is string executable
            && executable.Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal))
        {
            ready = false;

            yield return new SetupStep(
                SetupRemedy.SetExecutable,
                ConfigurationSection.Provider,
                service.Id + ": percorso del programma non ancora indicato",
                "Percorso assoluto del programma per '" + service.Id + "'",
                service.Id,
                executable);
        }

        if (service.Endpoint is Uri endpoint
            && endpoint.OriginalString.Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal))
        {
            ready = false;

            yield return new SetupStep(
                SetupRemedy.SetEndpoint,
                ConfigurationSection.Provider,
                service.Id + ": indirizzo non ancora indicato",
                "Indirizzo del servizio '" + service.Id + "'",
                service.Id,
                endpoint.OriginalString);
        }

        if (service.Model is string model
            && model.Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal))
        {
            ready = false;

            yield return new SetupStep(
                SetupRemedy.SetModel,
                ConfigurationSection.Models,
                service.Id + ": modello non ancora indicato",
                "Modello installato per '" + service.Id + "'",
                service.Id,
                model);
        }

        // Un'attestazione senza scadenza non e' un'osservazione, ed e' un errore appena il servizio
        // viene abilitato. Il router non la puo' compilare al posto dell'operatore (S30, S53): la
        // nomina, dice dove sta, e non propone un'abilitazione che verrebbe annullata.
        foreach (string control in service.Attestations
            .Where(entry => entry.Value.ExpiresAt is null)
            .Select(entry => entry.Key.ToWire())
            .Order(StringComparer.Ordinal))
        {
            ready = false;

            yield return new SetupStep(
                SetupRemedy.OperatorAction,
                ConfigurationSection.Privacy,
                service.Id + ": '" + control + "' attestato senza scadenza",
                "compila 'by', 'observedAt' ed 'expiresAt' sotto attestations/" + control
                    + " in " + snapshot.Config.RegistryFile + ": e' un'osservazione che spetta a te",
                service.Id);
        }

        if (ready && !service.Enabled)
        {
            yield return new SetupStep(
                SetupRemedy.EnableService,
                ConfigurationSection.Provider,
                service.Id + ": pronto ma non abilitato",
                "Abilito '" + service.Id + "'?",
                service.Id,
                Choices: [service.Id]);
        }

        // Il profilo si tocca per ultimo: cambiare profilo per far entrare un servizio e' una
        // decisione piu' grande che indicare un modello, e spesso diventa inutile una volta che il
        // servizio giusto e' abilitato.
        foreach (SectionReport section in report.Sections.Where(
            section => section.Section is ConfigurationSection.Routing
                && section.Severity is SectionSeverity.Missing or SectionSeverity.Error))
        {
            yield return new SetupStep(
                SetupRemedy.ChooseProfile,
                ConfigurationSection.Routing,
                section.Summary,
                "Quale profilo uso come predefinito?",
                Current: snapshot.Config.DefaultProfileId,
                Choices: [.. snapshot.Profiles.Keys.Order(StringComparer.Ordinal)]);
        }

        // Cio' che il router non puo' scrivere al posto dell'operatore resta detto, non chiesto:
        // un'attestazione la firma chi ha osservato il controllo (S30, S53).
        foreach (SectionReport section in report.Sections.Where(
            section => section.Section is ConfigurationSection.Privacy
                && section.Severity is SectionSeverity.Warning))
        {
            yield return new SetupStep(
                SetupRemedy.OperatorAction,
                ConfigurationSection.Privacy,
                section.Summary,
                section.Hint ?? "richiede un'osservazione dell'operatore");
        }
    }

    /// <summary>
    /// Servizio di cui si sta parlando: quello scelto, altrimenti il primo gia' abilitato.
    ///
    /// Con un servizio gia' abilitato non si chiede di sceglierne uno: l'installazione ha gia' preso
    /// quella decisione, e rimetterla in discussione a ogni avvio sarebbe una domanda a cui e' gia'
    /// stata data risposta.
    /// </summary>
    private static ServiceDescriptor? Target(ConfigurationSnapshot snapshot, string? serviceId) =>
        serviceId is null
            ? snapshot.Registry.Services.FirstOrDefault(service => service.Enabled)
            : snapshot.Registry.Services.FirstOrDefault(
                service => string.Equals(service.Id, serviceId, StringComparison.Ordinal));

    /// <summary>
    /// Servizi che il profilo predefinito ammette: sono quelli che, una volta abilitati, servono
    /// davvero a qualcosa. Abilitarne uno fuori dal profilo darebbe un registro piu' ricco e un
    /// router che nega comunque.
    /// </summary>
    private static IReadOnlyList<ServiceDescriptor> ProfileCandidates(ConfigurationSnapshot snapshot)
    {
        IReadOnlyList<ServiceDescriptor> services = snapshot.Registry.Services;

        return snapshot.Profiles.TryGetValue(snapshot.Config.DefaultProfileId, out PolicyDocument? profile)
            ? [.. services.Where(service => profile.Constraints.AllowsService(service.Id))]
            : services;
    }

    /// <summary>Esito di una conversione chiesta dal configuratore.</summary>
    /// <param name="Applied">Vero se i file sono stati convertiti.</param>
    /// <param name="Problems">Perche' non si poteva convertire, quando non e' avvenuto.</param>
    /// <param name="Files">File convertiti, relativi alla directory dati.</param>
    /// <param name="Report">Che cosa risulta della configurazione dopo la conversione.</param>
    public sealed record SetupMigration(
        bool Applied,
        IReadOnlyList<string> Problems,
        IReadOnlyList<string> Files,
        ConfigurationReport Report);

    /// <summary>
    /// Converte una configurazione in forma superata.
    ///
    /// E' la **stessa** operazione di <c>pa config migrate --apply</c>, invocata da qui: il divieto
    /// che vale in questo progetto e' aggiungere una conversione al *caricamento*, non offrire
    /// all'operatore quella che il comando esegue gia'. Una seconda conversione scritta apposta per
    /// il configuratore sarebbe invece proprio la copia che diverge.
    ///
    /// Gli originali restano accanto ai convertiti, e il verdetto finale lo da' la rilettura: una
    /// conversione riuscita non garantisce una configurazione utilizzabile, e dirlo subito evita di
    /// mandare avanti chi crede di avere finito.
    /// </summary>
    /// <param name="dataDirectory">Directory dati.</param>
    /// <param name="clock">Orologio della rilettura.</param>
    public static SetupMigration Migrate(string dataDirectory, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(clock);

        MigrationPlan plan = ConfigMigration.Plan(dataDirectory, clock.UtcNow);

        if (!plan.IsApplicable)
        {
            return new SetupMigration(
                false, plan.Problems, [], ConfigurationHealth.Inspect(dataDirectory, clock));
        }

        ConfigMigration.Apply(dataDirectory, plan);

        return new SetupMigration(
            true,
            [],
            [.. plan.Files.Select(file => file.RelativePath)],
            ConfigurationHealth.Inspect(dataDirectory, clock));
    }

    /// <summary>
    /// Controlla una risposta **prima** di scriverla.
    ///
    /// La rilettura di <see cref="RegistryRewrite"/> resta la rete di sicurezza, ma arriva dopo aver
    /// toccato il file: dire subito che un percorso non esiste evita all'operatore di scoprirlo da un
    /// ripristino.
    /// </summary>
    /// <returns>Il motivo del rifiuto, oppure null se il valore va bene.</returns>
    public static string? Validate(SetupStep step, string value)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (string.IsNullOrWhiteSpace(value))
        {
            return "il valore non puo' essere vuoto";
        }

        string trimmed = value.Trim();

        if (trimmed.Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal))
        {
            return "questo e' ancora il segnaposto consegnato, non un valore";
        }

        if (step.Choices is { Count: > 0 } choices
            && !choices.Contains(trimmed, StringComparer.Ordinal))
        {
            return "valore non fra quelli possibili: " + string.Join(", ", choices);
        }

        return step.Remedy switch
        {
            // Le stesse due condizioni che fermano una probe (CliProbe): un nome risolto dal PATH
            // sarebbe il programma scelto dall'ambiente, non quello scelto nel registro.
            SetupRemedy.SetExecutable when !Path.IsPathRooted(trimmed) =>
                "serve un percorso assoluto, non un nome risolto dal PATH",
            SetupRemedy.SetExecutable when !File.Exists(trimmed) =>
                "questo percorso non esiste su questa macchina",
            SetupRemedy.SetEndpoint when !Uri.TryCreate(trimmed, UriKind.Absolute, out _) =>
                "non e' un indirizzo assoluto",
            _ => null,
        };
    }

    /// <summary>
    /// Scrive la risposta nel file che la contiene.
    ///
    /// Non valida di nuovo il valore: chi chiama ha gia' <see cref="Validate"/>, e la rilettura del
    /// riscrittore giudica il risultato intero. Qui c'e' solo la traduzione da risposta a campo.
    /// </summary>
    /// <param name="snapshot">Configurazione caricata.</param>
    /// <param name="step">Passo a cui si risponde.</param>
    /// <param name="value">Risposta dell'operatore.</param>
    /// <param name="clock">Orologio della rilettura.</param>
    public static RegistryRewriteResult Apply(
        ConfigurationSnapshot snapshot,
        SetupStep step,
        string value,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string trimmed = value.Trim();

        if (step.Remedy is SetupRemedy.ChooseProfile)
        {
            return RegistryRewrite.ApplyToConfiguration(
                snapshot,
                BackupSuffix,
                root => Assign(root, "defaultProfileId", trimmed),
                clock);
        }

        if (!step.Writes)
        {
            // Creazione e conversione hanno operazioni proprie, e un'attestazione non si scrive
            // affatto: chiamare il riscrittore qui toccherebbe un file senza nulla da cambiare.
            return new RegistryRewriteResult(RegistryRewriteStatus.Unchanged, snapshot.Config.RegistryFile, []);
        }

        string serviceId = step.Remedy is SetupRemedy.EnableService ? trimmed : step.ServiceId ?? string.Empty;

        return RegistryRewrite.Apply(
            snapshot,
            BackupSuffix,
            root => Service(root, serviceId) is JsonObject service && Field(service, step, trimmed),
            clock);
    }

    /// <summary>Servizio del registro per identificatore, oppure null.</summary>
    private static JsonObject? Service(JsonObject root, string serviceId) =>
        root["services"] is JsonArray services
            ? services
                .OfType<JsonObject>()
                .FirstOrDefault(service =>
                    string.Equals(service["id"]?.GetValue<string>(), serviceId, StringComparison.Ordinal))
            : null;

    private static bool Field(JsonObject service, SetupStep step, string value) => step.Remedy switch
    {
        SetupRemedy.EnableService => Enable(service),
        SetupRemedy.SetModel => Assign(service, "model", value),
        SetupRemedy.SetExecutable => Assign(service, "executable", value),
        SetupRemedy.SetEndpoint => Assign(service, "endpoint", value),
        _ => false,
    };

    private static bool Enable(JsonObject service)
    {
        if (service["enabled"]?.GetValue<bool>() == true)
        {
            return false;
        }

        service["enabled"] = true;

        return true;
    }

    /// <summary>Scrive un campo, e dice falso quando il valore era gia' quello.</summary>
    private static bool Assign(JsonObject target, string field, string value)
    {
        if (string.Equals(target[field]?.GetValue<string>(), value, StringComparison.Ordinal))
        {
            return false;
        }

        target[field] = value;

        return true;
    }
}
