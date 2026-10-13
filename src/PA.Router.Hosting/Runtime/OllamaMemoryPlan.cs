using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>Un modello di Ollama scelto nel router, con quanto occupa sul disco.</summary>
/// <param name="ServiceId">Servizio del registro.</param>
/// <param name="Model">Nome del modello.</param>
/// <param name="SizeBytes">Dimensione del modello dichiarata da Ollama; null se non osservata.</param>
/// <param name="Auxiliary">Vero per chi lavora per il router a ogni turno (embedding, analizzatore), falso per chi risponde.</param>
public sealed record OllamaModelNeed(string ServiceId, string Model, long? SizeBytes, bool Auxiliary);

/// <summary>La proposta per la memoria di Ollama, con i numeri da cui nasce.</summary>
/// <param name="TotalBytes">Memoria del computer.</param>
/// <param name="BudgetBytes">Parte che si propone di lasciare a Ollama.</param>
/// <param name="AuxiliaryBytes">Stima di cio' che occupano i modelli del router tenuti sempre caricati.</param>
/// <param name="LargestChatBytes">Stima del modello che risponde piu' grande, caricato con la sua finestra.</param>
/// <param name="ChatSlots">Modelli che rispondono tenuti caricati insieme.</param>
/// <param name="MaxLoadedModels">Il valore proposto per <c>OLLAMA_MAX_LOADED_MODELS</c>.</param>
/// <param name="KeepAliveMinutes">Permanenza proposta per servizio: -1 sempre, altrimenti minuti.</param>
/// <param name="Unmeasured">Modelli di cui Ollama non ha detto la dimensione: la proposta non li conta.</param>
/// <param name="Tight">Vero se gia' i modelli del router e un solo modello che risponde superano la parte proposta.</param>
public sealed record OllamaMemoryProposal(
    long TotalBytes,
    long BudgetBytes,
    long AuxiliaryBytes,
    long LargestChatBytes,
    int ChatSlots,
    int MaxLoadedModels,
    IReadOnlyDictionary<string, int> KeepAliveMinutes,
    IReadOnlyList<string> Unmeasured,
    bool Tight);

/// <summary>
/// Quanti modelli Ollama puo' tenere caricati insieme su questo computer, e quanto ciascuno resta in memoria (23 settembre
/// 2026). Lo propone il setup, l'operatore accetta o cambia.
///
/// Misurato sulla macchina di riferimento (16 GB, senza scheda grafica): con cinque modelli caricati Ollama occupava 15,5
/// GB e la generazione scendeva da 10 a 2 token al secondo; con una permanenza uguale per tutti Ollama scaricava il modello
/// di embedding a ogni turno, e rientrando quello buttava fuori il modello che doveva rispondere. La regola:
///
/// - a Ollama si propone meta' della memoria: il resto e' del sistema e dei programmi dell'operatore;
/// - i modelli che lavorano per il router a ogni turno (embedding, analizzatore) restano caricati finche' Ollama e' acceso;
/// - nel resto si contano quanti modelli che rispondono stanno insieme, stimati sul piu' grande, e almeno uno;
/// - chi risponde resta caricato mezz'ora dopo l'ultima risposta.
///
/// Le stime vengono dai caricamenti osservati: un modello di chat caricato con una finestra di 8192 token occupa circa una
/// volta e mezza il suo file, un modello di embedding o l'analizzatore con la finestra piccola poco piu' del file.
/// </summary>
public static class OllamaMemoryPlan
{
    /// <summary>Parte della memoria proposta per Ollama.</summary>
    public const double BudgetShare = 0.5;

    /// <summary>Un modello di chat caricato con 8192 token di finestra, rispetto al suo file (2,5 GB diventano 3,6).</summary>
    public const double ChatOverhead = 1.45;

    /// <summary>Un modello del router caricato con una finestra piccola, rispetto al suo file (2,5 GB diventano 2,7).</summary>
    public const double AuxiliaryOverhead = 1.1;

    /// <summary>Permanenza proposta per chi risponde.</summary>
    public const int ChatKeepAliveMinutes = 30;

    /// <summary>La variabile di Ollama che limita i modelli caricati insieme.</summary>
    public const string MaxLoadedVariable = "OLLAMA_MAX_LOADED_MODELS";

    /// <summary>La proposta, dai numeri osservati. Pura: nessuna lettura, nessuna scrittura.</summary>
    public static OllamaMemoryProposal Propose(long totalBytes, IReadOnlyList<OllamaModelNeed> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);

        long budget = (long)(totalBytes * BudgetShare);
        OllamaModelNeed[] measured = [.. models.Where(model => model.SizeBytes is > 0)];
        string[] unmeasured = [.. models.Where(model => model.SizeBytes is not > 0).Select(model => model.Model).Distinct(StringComparer.Ordinal)];

        // Due servizi sullo stesso modello con la stessa finestra sono un caricamento solo; per i modelli del router si
        // contano i nomi distinti.
        long auxiliary = measured
            .Where(model => model.Auxiliary)
            .DistinctBy(model => model.Model, StringComparer.Ordinal)
            .Sum(model => (long)(model.SizeBytes!.Value * AuxiliaryOverhead));
        int auxiliaryCount = models.Where(model => model.Auxiliary).Select(model => model.Model).Distinct(StringComparer.Ordinal).Count();

        OllamaModelNeed[] chats = [.. measured.Where(model => !model.Auxiliary).DistinctBy(model => model.Model, StringComparer.Ordinal)];
        int chatModels = models.Where(model => !model.Auxiliary).Select(model => model.Model).Distinct(StringComparer.Ordinal).Count();
        long largest = chats.Length == 0 ? 0 : chats.Max(model => (long)(model.SizeBytes!.Value * ChatOverhead));

        int slots = largest == 0 ? Math.Max(0, Math.Min(1, chatModels)) : (int)Math.Max(1, (budget - auxiliary) / largest);
        slots = Math.Min(slots, Math.Max(chatModels, 0));
        if (chatModels > 0) { slots = Math.Max(1, slots); }

        Dictionary<string, int> keep = new(StringComparer.Ordinal);
        foreach (OllamaModelNeed model in models)
        {
            keep[model.ServiceId] = model.Auxiliary ? -1 : ChatKeepAliveMinutes;
        }

        return new OllamaMemoryProposal(
            totalBytes,
            budget,
            auxiliary,
            largest,
            slots,
            Math.Max(1, auxiliaryCount + slots),
            keep,
            unmeasured,
            Tight: auxiliary + largest > budget);
    }

    /// <summary>La memoria del computer, come la vede il processo.</summary>
    public static long TotalMemoryBytes() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>
    /// I modelli di Ollama su questa macchina che il router usa: i servizi scelti o accesi, con la dimensione letta dal
    /// catalogo del loro Ollama. Legge solo il catalogo: nessun modello viene caricato.
    /// </summary>
    public static async Task<IReadOnlyList<OllamaModelNeed>> ObserveAsync(
        IEnumerable<ServiceDescriptor> services,
        IClock clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        ServiceDescriptor[] local = [.. services.Where(service =>
            service.Adapter is AdapterKind.Ollama or AdapterKind.OllamaEmbed
            && service.InferencePlacement is Placement.Loopback
            && service.Endpoint is not null
            && service.Model is { Length: > 0 })];

        Dictionary<string, IReadOnlyDictionary<string, PA.Router.Adapters.Ollama.OllamaProbe.InstalledModel>?> catalogues = new(StringComparer.Ordinal);
        List<OllamaModelNeed> needs = [];

        foreach (ServiceDescriptor service in local)
        {
            string key = service.Endpoint!.ToString();

            if (!catalogues.TryGetValue(key, out var catalogue))
            {
                try
                {
                    catalogue = await SetupDiscovery.ModelCatalogueAsync(service, clock, cancellationToken).ConfigureAwait(false);
                }
                catch (PaRouterException)
                {
                    catalogue = null;
                }

                catalogues[key] = catalogue;
            }

            long? size = catalogue is null ? null
                : catalogue.TryGetValue(service.Model!, out var exact) ? exact.SizeBytes
                : catalogue.TryGetValue(service.Model! + ":latest", out var tagged) ? tagged.SizeBytes
                : null;

            needs.Add(new OllamaModelNeed(service.Id, service.Model!, size, !ServiceSetupCatalog.AnswersRequests(service.Capability)));
        }

        return needs;
    }

    /// <summary>Il valore attuale della variabile di Ollama, per l'utente; null se non impostata o non leggibile.</summary>
    public static int? CurrentMaxLoaded()
    {
        string? value = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable(MaxLoadedVariable, EnvironmentVariableTarget.User)
            : Environment.GetEnvironmentVariable(MaxLoadedVariable);

        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
    }

    /// <summary>
    /// Scrive la variabile di Ollama per l'utente, dopo il si' dell'operatore. Vale dal prossimo avvio di Ollama. Solo su
    /// Windows: altrove Ollama gira come servizio di sistema e la variabile va nella sua configurazione, che il router non
    /// tocca — il setup mostra la riga da scrivere. Restituisce vero se l'ha scritta.
    /// </summary>
    public static bool ApplyMaxLoaded(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);

        if (!OperatingSystem.IsWindows()) { return false; }

        Environment.SetEnvironmentVariable(
            MaxLoadedVariable,
            value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            EnvironmentVariableTarget.User);
        return true;
    }
}
