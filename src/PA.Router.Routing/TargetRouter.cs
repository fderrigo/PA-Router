using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Routing;

/// <summary>
/// Preferenze di routing. Sono le uniche impostazioni che il ranking conosce: tutto il resto e'
/// gia' stato deciso dalla policy.
/// </summary>
/// <param name="PreferSubscription">
/// Preferenza forte per i target a subscription, applicata <em>dopo</em> tutti i vincoli. Non
/// attribuisce costo zero a un abbonamento e non puo' riabilitare un target negato.
/// </param>
/// <param name="RequestedTargetId">Servizio richiesto esplicitamente, se presente.</param>
/// <param name="Capability">
/// Capacita' da instradare. Il routing avviene per capacita' (ADR-015): i servizi di un'altra
/// capacita' non vengono classificati, non vengono penalizzati. Null classifica tutto cio' che la
/// policy ha ammesso, ed e' il comportamento dei soli chiamanti che valutano una capacita' sola.
/// </param>
/// <param name="EstimatedInputTokens">Token stimati della richiesta, per il costo.</param>
/// <param name="EstimatedOutputTokens">Token in uscita con cui stimare il costo.</param>
public sealed record RoutingOptions(
    bool PreferSubscription,
    string? RequestedTargetId = null,
    ServiceCapability? Capability = null,
    int EstimatedInputTokens = 0,
    int EstimatedOutputTokens = 0)
{
    /// <summary>
    /// Preferenze per un canale. Il canale chatbot forza <c>preferSubscription</c> a false: le
    /// sessioni del browser non devono neppure poter esprimere una preferenza per un abbonamento
    /// personale (INV-09).
    /// </summary>
    public static RoutingOptions ForChannel(
        bool preferSubscription,
        Channel channel,
        string? requestedTargetId = null,
        ServiceCapability? capability = null,
        int estimatedInputTokens = 0,
        int estimatedOutputTokens = 0) =>
        new(
            channel is Channel.Chatbot ? false : preferSubscription,
            requestedTargetId,
            capability,
            estimatedInputTokens,
            estimatedOutputTokens);
}

/// <summary>Un candidato classificato.</summary>
/// <param name="Target">Target ammissibile.</param>
/// <param name="Tier">Fascia di preferenza: 0 batte 1.</param>
/// <param name="Components">Componenti del punteggio.</param>
/// <param name="Position">Posizione finale, a partire da 1.</param>
/// <param name="EstimatedCost">Costo stimato della richiesta, null se il prezzo e' sconosciuto.</param>
public sealed record RankedTarget(
    AdmissibleTarget Target,
    int Tier,
    ScoreComponents Components,
    int Position,
    double? EstimatedCost = null);

/// <summary>
/// Esito del ranking: candidati ordinati, scelta e versione dell'algoritmo.
/// </summary>
/// <param name="AlgorithmVersion">Versione dell'algoritmo usato.</param>
/// <param name="PreferSubscription">Preferenza applicata.</param>
/// <param name="Ranked">Candidati in ordine, il primo e' quello scelto.</param>
/// <param name="TieBrokenById">Vero se due candidati sono stati separati dall'ordine degli ID.</param>
public sealed record RouteDecision(
    string AlgorithmVersion,
    bool PreferSubscription,
    IReadOnlyList<RankedTarget> Ranked,
    bool TieBrokenById)
{
    /// <summary>Target scelto, null se nessun candidato e' rimasto.</summary>
    public AdmissibleTarget? Chosen => Ranked.Count > 0 ? Ranked[0].Target : null;

    /// <summary>Vero se non c'e' nulla da eseguire.</summary>
    public bool IsEmpty => Ranked.Count == 0;

    /// <summary>Errore tipizzato corrispondente a un insieme vuoto.</summary>
    public PaRouterException NoAdmissibleTarget() =>
        new(ErrorCode.NoAdmissibleTarget, "No target satisfies the active profile for this request.");

    /// <summary>Resa compatta: identificatori e posizioni, nessun contenuto.</summary>
    public override string ToString() =>
        "RouteDecision { " + AlgorithmVersion + ", " + string.Join(
            " > ",
            Ranked.Select(entry => entry.Target.TargetId + "@t" + entry.Tier.ToString(System.Globalization.CultureInfo.InvariantCulture))) + " }";
}

/// <summary>
/// Ordinamento dei target ammissibili.
///
/// L'ordine e' (tier crescente, punteggio decrescente arrotondato a sei decimali, costo stimato
/// crescente, ID ordinale crescente). A punteggio uguale vince il servizio che costa meno; solo a
/// costo uguale o sconosciuto decide l'ID, che esiste perche' due configurazioni identiche devono
/// produrre la stessa scelta su macchine diverse.
/// </summary>
public static class TargetRouter
{
    /// <summary>Classifica i target ammissibili.</summary>
    /// <param name="admissible">Target gia' ammessi dalla policy.</param>
    /// <param name="options">Preferenze di routing.</param>
    public static RouteDecision Rank(IReadOnlyList<AdmissibleTarget> admissible, RoutingOptions options)
    {
        ArgumentNullException.ThrowIfNull(admissible);
        ArgumentNullException.ThrowIfNull(options);

        List<AdmissibleTarget> candidates = [];

        foreach (AdmissibleTarget target in admissible)
        {
            if (options.Capability is ServiceCapability capability && target.Capability != capability)
            {
                // Un servizio di un'altra capacita' non e' un candidato peggiore: non e' un
                // candidato. Classificarlo significherebbe poterlo scegliere per ripiego.
                continue;
            }

            if (options.RequestedTargetId is string requested
                && !string.Equals(requested, target.TargetId, StringComparison.Ordinal))
            {
                // La policy ha gia' applicato il filtro; qui e' una difesa, non una seconda regola.
                continue;
            }

            if (target.Calibration.QuotaState is QuotaState.Exhausted)
            {
                // Un target a quota esaurita non entra nella classifica. Anche questo caso e' gia'
                // negato dalla policy: se arriva fin qui, meglio escluderlo che ordinarlo.
                continue;
            }

            candidates.Add(target);
        }

        // Il costo della richiesta si stima con lo stesso numero di token per tutti. Fra i candidati
        // con un prezzo la componente di costo e' relativa al piu' economico, come la latenza e' relativa
        // al piu' veloce: un prezzo per milione non dice nulla da solo, confrontato si'.
        Dictionary<string, double> costs = new(StringComparer.Ordinal);

        foreach (AdmissibleTarget target in candidates)
        {
            if (CostEstimate.Of(target.Calibration.Price, options.EstimatedInputTokens, options.EstimatedOutputTokens) is double cost)
            {
                costs[target.TargetId] = cost;
            }
        }

        double cheapest = costs.Count == 0 ? 0 : costs.Values.Min();

        List<(AdmissibleTarget Target, int Tier, ScoreComponents Components, double? Cost)> eligible = [];

        foreach (AdmissibleTarget target in candidates)
        {
            double? cost = costs.TryGetValue(target.TargetId, out double known) ? known : null;

            TargetCalibration calibration = cost is double estimated
                ? target.Calibration with { CostScore = CostEstimate.RelativeScore(cheapest, estimated) }
                : target.Calibration;

            eligible.Add((target, TierOf(target, options.PreferSubscription), RankingAlgorithm.Score(calibration), cost));
        }

        List<(AdmissibleTarget Target, int Tier, ScoreComponents Components, double? Cost)> ordered =
        [
            .. eligible
                .OrderBy(entry => entry.Tier)
                .ThenByDescending(entry => entry.Components.RoundedScore)
                .ThenBy(entry => entry.Cost ?? double.PositiveInfinity)
                .ThenBy(entry => entry.Target.TargetId, StringComparer.Ordinal)
        ];

        bool tieBroken = HasTie(ordered);

        return new RouteDecision(
            RankingAlgorithm.Version,
            options.PreferSubscription,
            [.. ordered.Select((entry, index) => new RankedTarget(entry.Target, entry.Tier, entry.Components, index + 1, entry.Cost))],
            tieBroken);
    }

    /// <summary>
    /// Fascia di preferenza. Con <c>preferSubscription</c> attivo i target a subscription stanno
    /// nella fascia 0 e tutti gli altri nella 1; altrimenti tutti nella 0.
    /// </summary>
    private static int TierOf(AdmissibleTarget target, bool preferSubscription)
    {
        if (!preferSubscription)
        {
            return 0;
        }

        return target.AuthMode is AuthMode.Subscription ? 0 : 1;
    }

    private static bool HasTie(IReadOnlyList<(AdmissibleTarget Target, int Tier, ScoreComponents Components, double? Cost)> ordered)
    {
        for (int index = 1; index < ordered.Count; index++)
        {
            if (ordered[index].Tier == ordered[index - 1].Tier
                && ordered[index].Components.RoundedScore.Equals(ordered[index - 1].Components.RoundedScore)
                && Nullable.Equals(ordered[index].Cost, ordered[index - 1].Cost))
            {
                return true;
            }
        }

        return false;
    }
}
