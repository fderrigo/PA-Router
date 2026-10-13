using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Infrastructure;
using PA.Router.Policy;
using PA.Router.Privacy;
using PA.Router.Registry.Loading;
using PA.Router.Routing;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Esito dell'analisi locale di una richiesta: che cosa il router ha capito del testo e quali
/// servizi resterebbero ammissibili. Non emette grant e non tocca alcun adapter.
/// </summary>
/// <param name="Profile">Analisi deterministica della richiesta: intento, banda, token stimati.</param>
/// <param name="Band">Finestra minima che la richiesta pretende (ADR-032, fase 3: nessuna qualita' minima).</param>
/// <param name="Analysis">Esito della pipeline di privacy: classe, certezza, digest.</param>
/// <param name="Explain">Decisioni della policy, ammessi e negati con le ragioni.</param>
/// <param name="RequiredCapabilities">Capacita' effettive della richiesta, chat compresa.</param>
/// <param name="Snapshot">Configurazione con cui l'analisi e' stata fatta.</param>
/// <param name="Costs">
/// Costo stimato della richiesta per ciascun servizio ammesso, nell'ordine degli ammessi; null se il
/// prezzo e' sconosciuto. E' la stessa stima che usa il ranking.
/// </param>
/// <param name="Currency">Valuta dei prezzi del registro, null se nessun servizio ne dichiara uno.</param>
/// <param name="Order">
/// Posto dei servizi nell'ordine dei modelli (<see cref="ServiceOrder"/>): la qualita' del ranking, e chi legge l'analisi
/// deve vedere su che cosa poggera' la scelta.
/// </param>
public sealed record LocalAnalysisResult(
    RequestProfile Profile,
    BandRequirements Band,
    PrivacyAnalysis Analysis,
    PolicyExplain Explain,
    IReadOnlySet<Capability> RequiredCapabilities,
    ConfigurationSnapshot Snapshot,
    IReadOnlyList<ServiceCost> Costs,
    string? Currency,
    IReadOnlyDictionary<string, OrderPosition> Order)
{
    /// <summary>Spesa massima per richiesta della configurazione (ADR-032, fase 5); null senza tetto.</summary>
    public double? MaxCostPerRequest { get; init; }

    /// <summary>
    /// Quanto puo' costare al massimo la richiesta su **ogni** candidato, ammesso o no; null se il prezzo e' sconosciuto.
    /// Serve a dire contro quale cifra un servizio e' rimasto fuori per il tetto di spesa.
    /// </summary>
    public IReadOnlyDictionary<string, double?> Ceilings { get; init; } = new Dictionary<string, double?>(StringComparer.Ordinal);
}

/// <summary>Costo stimato di una richiesta su un servizio.</summary>
/// <param name="ServiceId">Servizio.</param>
/// <param name="EstimatedCost">Costo stimato, null se il prezzo e' sconosciuto.</param>
public sealed record ServiceCost(string ServiceId, double? EstimatedCost)
{
    /// <summary>
    /// Quanto puo' costare al massimo: la stima con l'uscita limitata dal tetto dichiarato del servizio
    /// (`maxOutputTokens`), che l'adapter fa rispettare (23 settembre 2026). La stima del ranking usa lo stesso tetto
    /// del profilo per tutti, ed e' giusta per confrontare; mostrata come cifra diceva 2,62 USD per una domanda di 24
    /// token su un servizio che non puo' scriverne piu' di 2048.
    /// </summary>
    public double? Ceiling { get; init; }
}

/// <summary>
/// Analisi locale di una richiesta, in un punto solo.
///
/// `pa explain`, il `/explain` del REPL e chiunque altro voglia sapere che cosa il router ha
/// capito devono dare la stessa risposta della decisione vera: se
/// ciascuno ricostruisse la propria catena, due percorsi potrebbero divergere e l'explain
/// diventerebbe una descrizione plausibile invece di una prova. Qui la catena e' una.
/// </summary>
public static class LocalAnalysis
{
    /// <summary>Limiti di privacy derivati dal profilo compilato.</summary>
    public static PrivacyLimits LimitsOf(CompiledPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return PrivacyLimits.Default with
        {
            MaxInputBytes = policy.MaxInputBytes,
            MaxContextBytes = policy.MaxContextBytes,
            MaxOutputBytes = policy.MaxOutputBytes,
        };
    }

    /// <summary>
    /// Analizza il testo e valuta la policy contro i candidati osservati, con la configurazione che
    /// c'e' adesso sul disco: un'analisi fatta con la regola di prima non spiegherebbe la decisione.
    /// </summary>
    /// <param name="profileId">Profilo in uso; si risolve nella configurazione corrente.</param>
    /// <param name="input">Testo dell'operatore.</param>
    /// <param name="stack">Composizione aperta: fornisce scanner e candidati.</param>
    /// <param name="targetId">Servizio imposto dall'operatore, se presente.</param>
    /// <param name="requestedCapabilities">Capacita' agentiche dichiarate; la chat c'e' sempre.</param>
    public static LocalAnalysisResult Evaluate(
        string profileId,
        string input,
        RouterStack stack,
        string? targetId,
        IReadOnlySet<Capability> requestedCapabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(requestedCapabilities);

        StackView view = stack.EnsureCurrent();
        ConfigurationSnapshot snapshot = view.Snapshot;
        CompiledPolicy policy = PolicyCompiler.Compile(TurnInputs.ResolveProfile(snapshot, profileId), snapshot.Config);

        // Lo scanner e' quello della directory dati: con il rilevatore PII abilitato l'analisi
        // locale deve vedere le stesse cose che vedra' il turno.
        PrivacyAnalysis analysis = new PrivacyPipeline(view.Scanner(), stack.Digest)
            .Analyze(new PrivacyRequest(
                input, [], policy.MinimumDataClass, null, policy.RedactContacts, LimitsOf(policy), Grading: policy.PersonalDataGrading));

        HashSet<Capability> required = [Capability.Chat, .. requestedCapabilities];

        RequestProfile profile = RequestAnalyzer.Analyze(input, [], required, RequestClassifier.Classify(input).BandFloor);
        BandRequirements band = RequestAnalyzer.Requirements(profile);

        PolicyRequest request = new(
            policy.Channel,
            profile.Intent,
            CapabilityRouting.ForRequest(required),
            required,
            analysis.Payload.EffectiveClass,
            analysis.FinalReport.Certainty,
            analysis.Payload.PayloadDigest,
            analysis.Payload.ContextManifestDigest,
            System.Text.Encoding.UTF8.GetByteCount(input),
            0,
            policy.RequiredObligations,
            snapshot.Digest,
            targetId,
            Band: band)
        {
            EstimatedInputTokens = profile.EstimatedTokens,
        };

        IReadOnlyList<PolicyCandidate> candidates = view.Candidates(AdapterRegistry.Observe(snapshot));
        PolicyExplain explain = new PolicyEvaluator(SystemClock.Instance).Evaluate(policy, request, candidates);

        int outputTokens = CostEstimate.OutputTokens(policy.MaxOutputBytes);

        IReadOnlyList<ServiceCost> costs =
        [
            .. explain.Admissible.Select(target => new ServiceCost(
                target.TargetId,
                CostEstimate.Of(target.Calibration.Price, profile.EstimatedTokens, outputTokens))
            {
                // La stessa cifra che il tetto di spesa confronta (ADR-032, fase 5): un punto solo.
                Ceiling = CostCeiling.Of(
                    target.Calibration.Price,
                    profile.EstimatedTokens,
                    policy.MaxOutputBytes,
                    snapshot.FindService(target.TargetId)?.MaxOutputTokens ?? 0),
            }),
        ];

        string? currency = snapshot.Registry.Currency;

        return new LocalAnalysisResult(profile, band, analysis, explain, required, snapshot, costs, currency, ServiceOrder.Positions(snapshot))
        {
            MaxCostPerRequest = policy.MaxCostPerRequest,
            Ceilings = candidates.ToDictionary(
                candidate => candidate.TargetId,
                candidate => CostCeiling.Of(candidate, profile.EstimatedTokens, policy.MaxOutputBytes),
                StringComparer.Ordinal),
        };
    }
}
