using System.Globalization;
using System.Text;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Routing.Demand;

/// <summary>
/// I livelli di capacita' di un servizio, gia' risolti: dichiarati dall'operatore, ricavati dall'ordine dei modelli o
/// dalla finestra di contesto, altrimenti neutri (ADR-027; ADR-032, fase 3).
/// </summary>
/// <param name="General">Livello generale: la correzione, il terzile dell'ordine, altrimenti il neutro 2.</param>
/// <param name="Reasoning">Ragionamento.</param>
/// <param name="Coding">Codice.</param>
/// <param name="Context">Lavoro su testi lunghi.</param>
/// <param name="Source">Da dove viene il livello generale: <c>correction</c>, <c>rank</c> o <c>neutral</c>.</param>
public sealed record CapabilityLevels(int? General, int? Reasoning, int? Coding, int Context, string Source);

/// <summary>Esito della selezione del minimo sufficiente.</summary>
/// <param name="Chosen">Servizio che sceglierebbe; null se nessuno e' compatibile.</param>
/// <param name="Compatible">Servizi abbastanza capaci su ogni dimensione, nell'ordine della scelta.</param>
/// <param name="Missing">Per ogni servizio escluso, le dimensioni che mancano: nome, livello del servizio, livello chiesto.</param>
public sealed record MinimumSufficientResult(
    string? Chosen,
    IReadOnlyList<string> Compatible,
    IReadOnlyDictionary<string, IReadOnlyList<(string Dimension, int Has, int Needs)>> Missing)
{
    /// <summary>Vero se la scelta e' il servizio del turno precedente, preferito per continuita' (<c>rank-v6</c>).</summary>
    public bool Continued { get; init; }
}

/// <summary>
/// Il minimo sufficiente (ADR-027, <c>rank-v5</c> da ADR-031): fra i servizi gia' ammessi dalla policy, quelli abbastanza capaci su
/// **ogni** dimensione del profilo di domanda, e fra questi il piu' economico e veloce. Non il migliore: la protezione
/// contro un modello troppo debole sta nel profilo, che sale quando e' incerto, mai qui.
///
/// Riusa i numeri che il ranking ha gia' calcolato per ciascun ammesso — fascia dell'abbonamento, costo stimato,
/// latenza — senza ricalcolarli. In ombra si calcola e si racconta accanto alla scelta di <c>rank-v3</c>; con il profilo
/// di domanda decisivo (ADR-031) e' la scelta.
/// </summary>
public static class MinimumSufficient
{
    /// <summary>
    /// Versione dell'algoritmo e della regola che ricava i livelli. <c>rank-v5</c> (ADR-031) aggiunge, dopo il costo, il
    /// livello piu' basso: fra modelli locali a costo zero decideva la velocita' dichiarata o il nome, non la dimensione.
    /// <c>rank-v6</c> (23 settembre 2026) aggiunge la continuita': a parita' di fascia e di costo, il servizio che ha
    /// risposto al turno prima, se basta, risponde ancora.
    /// </summary>
    public const string Version = "rank-v6";

    /// <summary>Sceglie fra i candidati gia' classificati.</summary>
    /// <param name="demand">Profilo di domanda.</param>
    /// <param name="ranked">Ammessi, nell'ordine di <c>rank-v3</c>.</param>
    /// <param name="levels">Livelli dei servizi.</param>
    /// <param name="previous">
    /// Servizio che ha risposto al turno precedente della conversazione. Se basta, e costa quanto il primo della scelta
    /// nella stessa fascia, risponde lui: ha gia' letto la conversazione. Su un processore cambiare modello costava da 3 a 9
    /// secondi di caricamento e la rilettura della cronologia da capo — 20-30 secondi per mille token. Non scavalca mai la
    /// fascia ne' il costo: un modello piu' caro non resta perche' era gia' li'.
    /// </param>
    public static MinimumSufficientResult Select(
        DemandProfile demand,
        IReadOnlyList<RankedTarget> ranked,
        IReadOnlyDictionary<string, CapabilityLevels> levels,
        string? previous = null)
    {
        ArgumentNullException.ThrowIfNull(demand);
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(levels);

        Dictionary<string, IReadOnlyList<(string, int, int)>> missing = new(StringComparer.Ordinal);
        List<RankedTarget> compatible = [];

        foreach (RankedTarget entry in ranked)
        {
            string id = entry.Target.TargetId;

            if (!levels.TryGetValue(id, out CapabilityLevels? service) || service.General is not int general)
            {
                missing[id] = [("levels", 0, demand.BaseTier)];
                continue;
            }

            List<(string, int, int)> short_ = [];
            Check("general", general, demand.BaseTier);
            Check("reasoning", service.Reasoning ?? general, demand.Reasoning);
            Check("coding", service.Coding ?? general, demand.Coding);
            Check("context", service.Context, demand.Context);

            if (short_.Count == 0) { compatible.Add(entry); } else { missing[id] = short_; }

            void Check(string dimension, int has, int needs)
            {
                if (has < needs) { short_.Add((dimension, has, needs)); }
            }
        }

        // Fascia dell'abbonamento come oggi (ADR-003, invariata), poi costo stimato crescente — un costo sconosciuto
        // dopo quelli noti, poi la preferenza di costo dichiarata —, poi il livello piu' basso che basta (rank-v5), poi il
        // piu' veloce, poi l'identificatore.
        RankedTarget[] ordered = [.. compatible
            .OrderBy(entry => entry.Tier)
            .ThenBy(entry => entry.EstimatedCost ?? double.MaxValue)
            .ThenByDescending(entry => entry.Components.Cost)
            .ThenBy(entry => levels[entry.Target.TargetId].General ?? int.MaxValue)
            .ThenBy(entry => levels[entry.Target.TargetId].Reasoning ?? int.MaxValue)
            .ThenByDescending(entry => entry.Components.Latency)
            .ThenBy(entry => entry.Target.TargetId, StringComparer.Ordinal)];

        if (previous is not null
            && ordered.Length > 1
            && ordered.FirstOrDefault(entry => entry.Target.TargetId == previous) is RankedTarget kept
            && kept.Tier == ordered[0].Tier
            && kept.EstimatedCost == ordered[0].EstimatedCost)
        {
            ordered = [kept, .. ordered.Where(entry => !ReferenceEquals(entry, kept))];
        }

        return new MinimumSufficientResult(
            ordered.Length > 0 ? ordered[0].Target.TargetId : null,
            [.. ordered.Select(entry => entry.Target.TargetId)],
            missing)
        {
            Continued = previous is not null && ordered.Length > 1 && ordered[0].Target.TargetId == previous,
        };
    }

    /// <summary>
    /// Quando nessun ammesso basta (ADR-031): il piu' capace — livello generale, poi ragionamento, poi contesto — e a pari
    /// livelli l'ordine di <c>rank-v3</c>, che mette avanti l'ordine dei modelli. Un servizio senza livelli vale meno di uno che li ha.
    /// </summary>
    public static RankedTarget MostCapable(IReadOnlyList<RankedTarget> ranked, IReadOnlyDictionary<string, CapabilityLevels> levels)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(levels);

        if (ranked.Count == 0) { throw new ArgumentException("At least one admitted service is required.", nameof(ranked)); }

        CapabilityLevels? Of(RankedTarget entry) => levels.GetValueOrDefault(entry.Target.TargetId);

        return ranked
            .Select((entry, order) => (entry, order))
            .OrderByDescending(item => Of(item.entry)?.General ?? 0)
            .ThenByDescending(item => Of(item.entry)?.Reasoning ?? Of(item.entry)?.General ?? 0)
            .ThenByDescending(item => Of(item.entry)?.Context ?? 0)
            .ThenBy(item => item.order)
            .First().entry;
    }

    /// <summary>Livello generale di un servizio senza correzione ne' posto nell'ordine: il medio.</summary>
    public const int NeutralLevel = 2;

    /// <summary>
    /// I livelli di un servizio che risponde. Generale: la correzione dell'operatore; altrimenti il posto nell'ordine dei
    /// modelli diviso in terzi (il primo terzo 3, il secondo 2, l'ultimo 1); altrimenti il neutro
    /// <see cref="NeutralLevel"/>. Fino al 24 settembre 2026 un servizio senza ordine prendeva il livello dal voto (almeno 8
    /// vale 3, almeno 5 vale 2) e senza voto restava senza livello, cioe' escluso: i voti non esistono piu' (ADR-032,
    /// fase 3). Contesto: la correzione, oppure la finestra (almeno 32768 token vale 3, almeno 8192 vale 2).
    /// </summary>
    /// <param name="declared">Correzioni dell'operatore.</param>
    /// <param name="rankPosition">Posizione fra i servizi ordinati (0 il piu' capace), e quanti sono; null se non ordinato.</param>
    /// <param name="contextWindowTokens">Finestra dichiarata.</param>
    public static CapabilityLevels Resolve(ServiceLevels? declared, (int Position, int Count)? rankPosition, int contextWindowTokens)
    {
        (int general, string source) = declared?.General is int corrected ? (corrected, "correction")
            : rankPosition is (int position, int count) ? (Tercile(position, count), "rank")
            : (NeutralLevel, "neutral");

        int context = declared?.Context ?? (contextWindowTokens >= 32768 ? 3 : contextWindowTokens >= 8192 ? 2 : 1);

        return new CapabilityLevels(general, declared?.Reasoning ?? general, declared?.Coding ?? general, context, source);
    }

    private static int Tercile(int position, int count) =>
        count <= 1 || position * 3 < count ? 3 : position * 3 < count * 2 ? 2 : 1;

    /// <summary>La selezione come coppie <c>chiave=valore</c>, senza contenuti, per gli eventi del turno.</summary>
    public static string Describe(MinimumSufficientResult result, string? today)
    {
        ArgumentNullException.ThrowIfNull(result);

        StringBuilder text = new("algorithm=" + Version);
        text.Append(";service=").Append(result.Chosen ?? "-");
        if (today is not null) { text.Append(";today=").Append(today); }
        if (result.Continued) { text.Append(";continued=1"); }
        if (result.Compatible.Count > 0) { text.Append(";compatible=").Append(string.Join(',', result.Compatible)); }

        if (result.Missing.Count > 0)
        {
            text.Append(";missing=").Append(string.Join(',', result.Missing
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + ":" + string.Join('+', pair.Value.Select(item =>
                    item.Dimension + item.Has.ToString(CultureInfo.InvariantCulture) + "<" + item.Needs.ToString(CultureInfo.InvariantCulture))))));
        }

        return text.ToString();
    }
}
