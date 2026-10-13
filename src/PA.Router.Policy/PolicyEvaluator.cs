using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Policy;

/// <summary>
/// Esito della valutazione di un insieme di candidati: una decisione per ciascuno e i soli
/// ammissibili costruiti.
/// </summary>
/// <param name="Decisions">Decisione per ogni candidato, nell'ordine dei candidati.</param>
/// <param name="Admissible">Target ammissibili; puo' essere vuoto.</param>
public sealed record PolicyExplain(
    IReadOnlyList<PolicyDecision> Decisions,
    IReadOnlyList<AdmissibleTarget> Admissible)
{
    /// <summary>Vero se nessun candidato e' passato.</summary>
    public bool IsEmpty => Admissible.Count == 0;

    /// <summary>Decisione di un candidato.</summary>
    public PolicyDecision For(string targetId) =>
        Decisions.First(decision => string.Equals(decision.TargetId, targetId, StringComparison.Ordinal));
}

/// <summary>
/// Valutazione dei candidati.
///
/// Non c'e' first-match e non c'e' ordinamento delle regole: ogni controllo viene eseguito e
/// tutte le ragioni vengono raccolte, poi ordinate per codice. Serve a chi legge un explain:
/// sapere che mancano tre condizioni e' diverso dal sapere che ne manca una.
///
/// Un deny non e' compensabile. Nessun punteggio, nessuna preferenza e nessuna variabile
/// d'ambiente entra in questa classe: il ranking riceve soltanto cio' che esce di qui (INV-02).
/// </summary>
public sealed class PolicyEvaluator
{
    /// <summary>
    /// L'orologio non serve piu' a nessun controllo dal 25 settembre 2026 (ADR-032, fase 4): le conferme non scadono e le
    /// evidenze con scadenza non esistono piu'. Resta nella firma perche' ogni chiamante lo passa gia'.
    /// </summary>
    public PolicyEvaluator(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
    }

    /// <summary>Valuta tutti i candidati contro la policy compilata.</summary>
    public PolicyExplain Evaluate(
        CompiledPolicy policy,
        PolicyRequest request,
        IReadOnlyList<PolicyCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);

        List<PolicyDecision> decisions = [];
        List<AdmissibleTarget> admissible = [];

        foreach (PolicyCandidate candidate in candidates)
        {
            SortedSet<PolicyReason> reasons = new(PolicyReasonComparer.Instance);

            EvaluateSnapshotAndChannel(policy, request, reasons);
            EvaluateAvailability(candidate, reasons);
            EvaluateAllowlists(policy, request, candidate, reasons);
            EvaluateControls(request, candidate, reasons);
            EvaluateDataClass(policy, request, candidate, reasons);
            EvaluateBudgetAndQuota(policy, request, candidate, reasons);
            EvaluateCost(policy, request, candidate, reasons);
            EvaluateObligations(policy, request, reasons);
            EvaluateBand(policy, request, candidate, reasons);

            bool allowed = reasons.Count == 0;

            decisions.Add(new PolicyDecision(
                candidate.TargetId,
                allowed,
                [.. reasons],
                policy.RequiredObligations,
                request.SnapshotDigest));

            if (allowed)
            {
                admissible.Add(new AdmissibleTarget(
                    candidate.TargetId,
                    candidate.Capability,
                    candidate.Adapter,
                    candidate.AuthMode,
                    candidate.InferencePlacement,
                    candidate.Capabilities,
                    candidate.Controls,
                    policy.RequiredObligations,
                    request.PayloadDigest,
                    request.EffectiveClass,
                    policy.TimeoutSeconds,
                    policy.MaxOutputBytes,
                    request.SnapshotDigest,
                    candidate.Calibration with { QuotaState = candidate.QuotaState }));
            }
        }

        return new PolicyExplain(decisions, admissible);
    }

    /// <summary>1. Integrita' dello snapshot e canale.</summary>
    private static void EvaluateSnapshotAndChannel(
        CompiledPolicy policy,
        PolicyRequest request,
        SortedSet<PolicyReason> reasons)
    {
        if (policy.DenyAll && policy.DenyAllReason is PolicyReason denyReason)
        {
            reasons.Add(denyReason);
        }

        if (string.IsNullOrWhiteSpace(request.SnapshotDigest))
        {
            // Senza il digest della configurazione non si puo' dire su che cosa si sta decidendo.
            reasons.Add(PolicyReason.PolicyInvalid);
        }

        if (policy.Channel != request.Channel)
        {
            reasons.Add(PolicyReason.ChannelMismatch);
        }
    }

    /// <summary>2. Presenza, abilitazione, probe e autenticazione.</summary>
    private static void EvaluateAvailability(PolicyCandidate candidate, SortedSet<PolicyReason> reasons)
    {
        if (!candidate.Enabled)
        {
            reasons.Add(PolicyReason.TargetDisabled);
        }

        if (!candidate.HasFreshProbe)
        {
            // Un probe scaduto o assente blocca finche' doctor o preflight non lo rinnova.
            reasons.Add(PolicyReason.ProbeStale);
        }

        if (candidate.AuthMode is not AuthMode.None && candidate.AuthStatus is not AuthStatus.Active)
        {
            reasons.Add(PolicyReason.AuthUnavailable);
        }
    }

    /// <summary>
    /// 3. Elenco dei servizi e capacita'. Dal 25 settembre 2026 (ADR-032, fase 4) un elenco vuoto ammette ogni servizio, e
    /// l'autenticazione non e' piu' un permesso: una chiave non rende un servizio meno sicuro, e la spesa la governa il
    /// tetto per richiesta (fase 5).
    /// </summary>
    private static void EvaluateAllowlists(
        CompiledPolicy policy,
        PolicyRequest request,
        PolicyCandidate candidate,
        SortedSet<PolicyReason> reasons)
    {
        if (candidate.Capability != request.RoutedCapability)
        {
            reasons.Add(PolicyReason.CapabilityMissing);
        }

        if (!policy.AllowsService(candidate.TargetId))
        {
            reasons.Add(PolicyReason.TargetNotAllowed);
        }

        if (request.RequestedTargetId is string requested
            && !string.Equals(requested, candidate.TargetId, StringComparison.Ordinal))
        {
            // Il target richiesto e' un vincolo in piu', mai un permesso in piu'.
            reasons.Add(PolicyReason.TargetNotAllowed);
        }

        foreach (Capability capability in request.RequiredCapabilities)
        {
            if (!policy.AllowedCapabilities.Contains(capability))
            {
                reasons.Add(PolicyReason.CapabilityForbidden);
            }

            if (!candidate.Capabilities.Contains(capability))
            {
                reasons.Add(PolicyReason.CapabilityMissing);
            }
        }
    }

    /// <summary>
    /// 4. Controlli che il servizio deve avere (ADR-032, fase 4): non li configura il profilo, vengono dal tipo di servizio
    /// e da cio' che il turno chiede (<see cref="AdmissionControls"/>). Una chat riceve solo il contenuto autorizzato; un
    /// agente sa tenere il recinto per le capacita' che il turno gli concede.
    /// </summary>
    private static void EvaluateControls(PolicyRequest request, PolicyCandidate candidate, SortedSet<PolicyReason> reasons)
    {
        if (!AdmissionControls.For(candidate.Capability, request.RequiredCapabilities).IsSubsetOf(candidate.Controls))
        {
            reasons.Add(PolicyReason.ControlUnavailable);
        }
    }

    /// <summary>
    /// 5. Classe dei dati e zona (ADR-029): dal 25 settembre 2026 (ADR-032, fase 4) l'unica regola su dove vanno i dati. La
    /// zona del servizio si ricava da collocazione e controlli effettivi (<see cref="TrustZones"/>); la classe decide la
    /// zona piu' lontana ammessa; fuori dal perimetro fisso gli allegati restano dentro e le classi che il profilo
    /// sottopone a conferma aspettano quella dell'operatore (<see cref="ZoneRule"/>).
    ///
    /// Un servizio su questo computer o in casa con l'elaborazione locale non confermata vale come un terzo: se la zona lo
    /// nega, il motivo e' anche la conferma mancante, perche' e' quella la cosa da fare.
    /// </summary>
    private static void EvaluateDataClass(
        CompiledPolicy policy,
        PolicyRequest request,
        PolicyCandidate candidate,
        SortedSet<PolicyReason> reasons)
    {
        TrustZone zone = TrustZones.Of(candidate);

        if (ZoneRule.Evaluate(
                policy.Zones,
                request.EffectiveClass,
                request.Certainty,
                zone,
                candidate.InferencePlacement,
                request.HasAttachments,
                request.ExternalConfirmed) is not PolicyReason reason)
        {
            return;
        }

        reasons.Add(reason);

        if (ServiceZones.LocalButUnconfirmed(candidate.InferencePlacement, candidate.Controls))
        {
            reasons.Add(PolicyReason.ControlUnavailable);
        }
    }

    /// <summary>6. Budget del profilo e quota nota come esaurita.</summary>
    private static void EvaluateBudgetAndQuota(
        CompiledPolicy policy,
        PolicyRequest request,
        PolicyCandidate candidate,
        SortedSet<PolicyReason> reasons)
    {
        if (request.InputBytes > policy.MaxInputBytes || request.ContextBytes > policy.MaxContextBytes)
        {
            reasons.Add(PolicyReason.BudgetExceeded);
        }

        if (candidate.QuotaState is QuotaState.Exhausted)
        {
            reasons.Add(PolicyReason.QuotaExhausted);
        }

        // QuotaState.Unknown non nega: ma non viene neppure trattato come quota residua, e infatti
        // non concede nulla qui.
    }

    /// <summary>
    /// 6bis. Tetto di spesa (ADR-032, fase 5): quanto puo' costare al massimo la richiesta su questo servizio non supera
    /// la spesa massima per richiesta. Un servizio senza costo a consumo vale zero e passa sempre; uno di terzi senza
    /// prezzo resta fuori, perche' non si puo' dimostrare che ci stia sotto. Vale anche per il secondo tentativo, che
    /// rivaluta con la stessa richiesta: un ripiego non apre la strada a una spesa che il primo non avrebbe avuto.
    /// </summary>
    private static void EvaluateCost(
        CompiledPolicy policy,
        PolicyRequest request,
        PolicyCandidate candidate,
        SortedSet<PolicyReason> reasons)
    {
        if (CostCeiling.Exceeds(
                CostCeiling.Of(candidate, request.EstimatedInputTokens, policy.MaxOutputBytes),
                policy.MaxCostPerRequest))
        {
            reasons.Add(PolicyReason.CostLimit);
        }
    }

    /// <summary>
    /// 11. Finestra richiesta: il payload stimato piu' la risposta, mai sotto il pavimento fisso. E' un gate: una finestra
    /// insufficiente nega, non tronca. Dal 24 settembre 2026 (ADR-032, fase 3) non c'e' piu' una qualita' minima: la
    /// qualita' e' il posto nell'ordine dei modelli e pesa nel ranking, non esclude. Senza requisito il gate non si applica.
    /// </summary>
    private static void EvaluateBand(
        CompiledPolicy policy,
        PolicyRequest request,
        PolicyCandidate candidate,
        SortedSet<PolicyReason> reasons)
    {
        if (request.Band is BandRequirements band
            && band.MinContextTokens > 0
            && candidate.ContextWindowTokens < band.MinContextTokens)
        {
            reasons.Add(PolicyReason.ContextWindowInsufficient);
        }

        if (candidate.ContextWindowTokens > 0 && request.EstimatedInputTokens > 0
            && (long)request.EstimatedInputTokens + GenerationLimits.OutputTokens(policy.MaxOutputBytes, candidate.MaxOutputTokens)
                > candidate.ContextWindowTokens)
        {
            reasons.Add(PolicyReason.ContextWindowInsufficient);
        }
    }

    /// <summary>
    /// Gli obblighi devono risultare gia' applicati al payload. E' una verifica difensiva: se la
    /// pipeline non li ha applicati, il difetto e' a monte e non va compensato qui autorizzando
    /// comunque.
    /// </summary>
    private static void EvaluateObligations(
        CompiledPolicy policy,
        PolicyRequest request,
        SortedSet<PolicyReason> reasons)
    {
        if (!policy.RequiredObligations.IsSubsetOf(request.SatisfiedObligations))
        {
            reasons.Add(PolicyReason.PolicyInvalid);
        }
    }

    /// <summary>Ordina le ragioni per codice wire, cosi' l'explain e' stabile e confrontabile.</summary>
    private sealed class PolicyReasonComparer : IComparer<PolicyReason>
    {
        internal static PolicyReasonComparer Instance { get; } = new();

        public int Compare(PolicyReason first, PolicyReason second) =>
            string.CompareOrdinal(first.ToWire(), second.ToWire());
    }
}
