using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Localization;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Repl;

/// <summary>
/// Come si chiama un servizio quando lo legge una persona.
///
/// L'identificatore — `chat-locale-llama3-2-3b` — non si cambia dal setup: a lui sono legate le
/// conferme firmate, l'elenco dei servizi che un uso accetta e i permessi. Dalla S117 nasce leggibile
/// (<c>ServiceIds.Readable</c>), ma resta un codice per il router: davanti a una persona ci va un
/// **nome**, e nessun elenco del terminale mostra l'identificatore al posto del nome.
///
/// Il nome ha due pezzi e li ha sempre tutti e due (deciso con l'operatore, S117):
///
/// - **l'istanza**, cioe' il nome che l'operatore le ha dato; senza, uno predefinito dedotto da
///   adattatore e collocazione («Ollama su questo computer»);
/// - **il modello**, sempre visibile, perche' sulla stessa istanza ce ne possono essere tre e senza
///   il modello non si capisce di quale si stia parlando.
///
/// Sta qui e in un punto solo: ogni resa del terminale — elenco dei servizi, rifiuto, barra di stato,
/// fasi del turno, setup — chiede il nome a questa classe. E' una **resa**, non un'operazione: il
/// nome e' localizzato, e la localizzazione vive nel front end (INV-22).
/// </summary>
internal static class ServiceNames
{
    /// <summary>Separatore fra istanza e modello.</summary>
    private const string Divider = " · ";

    /// <summary>
    /// Nome dell'istanza: quello dato dall'operatore, oppure quello predefinito per questo tipo di
    /// servizio. Non contiene mai il modello.
    /// </summary>
    internal static string Instance(ServiceDescriptor service)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (service.Name is { Length: > 0 } chosen) { return chosen; }

        // L'analizzatore lavora per il router e non risponde a nessuno: con il nome dell'istanza Ollama sembrava un modello
        // di chat, e lo stesso modello compariva due volte con lo stesso nome — fra gli ammessi e fra gli ausiliari.
        if (service.Capability is ServiceCapability.RequestAnalysis) { return Ui.Text("service.instance.analyzer"); }

        // Due servizi compatibili OpenAI di due fornitori avevano lo stesso nome (24 settembre 2026): Scaleway e OpenAI
        // comparivano entrambi come «un servizio via internet», e solo il modello li distingueva. L'indirizzo lo dice.
        if (service.Adapter is AdapterKind.OpenAiCompatible && service.InferencePlacement is Placement.ThirdParty
            && service.Endpoint is Uri endpoint)
        {
            return Ui.Format("service.instance.remoteHost {0}", endpoint.Host);
        }

        string key = "guide.service." + service.Adapter.ToWire() + "." + service.InferencePlacement.ToWire();

        return Ui.Contains(key) ? Ui.Text(key) : SetupHelp.Name(service.Adapter.ToWire());
    }

    /// <summary>
    /// Nome completo: istanza e modello. E' quello che va mostrato ovunque legga una persona.
    /// </summary>
    internal static string Display(ServiceDescriptor service)
    {
        ArgumentNullException.ThrowIfNull(service);

        return Model(service) is string model ? Instance(service) + Divider + model : Instance(service);
    }

    /// <summary>
    /// Nome completo del servizio con quell'identificatore, quando la configurazione lo conosce.
    /// Un identificatore che non risulta resta com'e': meglio un codice che una riga vuota, e succede
    /// solo per un servizio tolto mentre il turno girava.
    /// </summary>
    internal static string Display(ConfigurationSnapshot snapshot, string serviceId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        return snapshot.FindService(serviceId) is ServiceDescriptor service ? Display(service) : serviceId;
    }

    /// <summary>
    /// Il modello, quando c'e' ed e' stato scelto davvero: un segnaposto di esempio non e' un modello
    /// e mostrarlo come tale farebbe credere configurato cio' che non lo e'.
    /// </summary>
    internal static string? Model(ServiceDescriptor service)
    {
        ArgumentNullException.ThrowIfNull(service);

        return service.Model is { Length: > 0 } model
            && !model.Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal)
            ? model
            : null;
    }
}
