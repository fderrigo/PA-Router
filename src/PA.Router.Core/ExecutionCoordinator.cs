using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Abstractions.Observability;
using PA.Router.Execution;
using PA.Router.Policy;
using PA.Router.Policy.Grants;
using PA.Router.Privacy;
using PA.Router.Routing;

namespace PA.Router.Core;

/// <summary>
/// Coordinatore della pipeline: ammissione, privacy, policy, ranking, dispatch, screening,
/// persistenza.
///
/// L'ordine e' quello di 02-architecture/EXECUTION-PIPELINE.md e non e' negoziabile. I punti che
/// contano davvero, e che i test presidiano:
/// l'audit di intento viene committato prima che l'adapter parta; il grant viene riscattato
/// immediatamente prima del dispatch e non e' riusabile; nessun frammento di output raggiunge il
/// chiamante prima dello screening e del commit finale; un guasto dopo il dispatch con effetti
/// possibili resta <c>uncertain</c> e non viene ritentato.
/// </summary>
public sealed class ExecutionCoordinator
{
    private readonly ExecutionScheduler scheduler;
    private readonly PrivacyPipeline privacy;
    private readonly OutputScreener screener;
    private readonly PolicyEvaluator evaluator;
    private readonly GrantService grants;
    private readonly IExecutionStore store;
    private readonly IMetadataDigest digest;
    private readonly IClock clock;
    private readonly DispatchAudit dispatchAudit;

    /// <summary>
    /// Chi ricorda che un servizio ha finito la quota, con la data in cui torna se il fornitore la dice (24 settembre 2026).
    /// La composizione lo collega alla pausa dei servizi; senza, il coordinatore non ricorda niente.
    /// </summary>
    public Action<string, DateTimeOffset?>? QuotaExhausted { get; init; }
    private readonly IIdGenerator ids;
    private readonly IReadOnlyDictionary<AdapterKind, IExecutionAdapter> adapters;

    public ExecutionCoordinator(
        ExecutionScheduler scheduler,
        PrivacyPipeline privacy,
        OutputScreener screener,
        PolicyEvaluator evaluator,
        GrantService grants,
        IExecutionStore store,
        IMetadataDigest digest,
        IClock clock,
        IIdGenerator ids,
        IReadOnlyDictionary<AdapterKind, IExecutionAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(privacy);
        ArgumentNullException.ThrowIfNull(screener);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(adapters);

        this.scheduler = scheduler;
        this.privacy = privacy;
        this.screener = screener;
        this.evaluator = evaluator;
        this.grants = grants;
        this.store = store;
        this.digest = digest;
        this.clock = clock;
        this.ids = ids;
        this.adapters = adapters;
        dispatchAudit = new DispatchAudit(store, ids);
    }

    /// <summary>
    /// Esegue una richiesta fino a uno stato terminale e registra l'evento finale.
    ///
    /// L'evento terminale si scrive qui e in nessun altro punto: i percorsi interni vengono usati
    /// anche fra un tentativo e l'altro, e scriverlo li' significherebbe annunciare una fine che non
    /// e' ancora arrivata.
    /// </summary>
    public async Task<ExecutionResult> ExecuteAsync(ExecutionRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Lo span radice porta il canale e nulla che identifichi la richiesta: l'identificatore
        // resta quello dello span, utile in locale e assente dalle metriche.
        using Activity? span = RouterTelemetry.StartSpan("execution", ("channel", context.Policy.Channel.ToWire()));

        ExecutionResult result = await RunAsync(context, cancellationToken).ConfigureAwait(false);

        span?.SetTag("state", result.State.ToWire());
        RouterTelemetry.RecordExecution(result.State, context.Policy.Channel);

        if (result.ExecutionId is string terminalId && result.State is not ExecutionState.Accepted)
        {
            // Il token del chiamante puo' essere gia' annullato: l'evento terminale si scrive
            // comunque, perche' un turno cancellato resta un turno di cui si deve poter leggere la
            // fine.
            await AppendTerminalEventAsync(terminalId, result.State, CancellationToken.None).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<ExecutionResult> RunAsync(ExecutionRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 1. Ammissione. Occupato significa busy adesso: nessuna esecuzione, nessuna chiave. Chi accetta la richiesta
        // prima del turno — l'host API, che deve rispondere 429 subito — l'ammissione l'ha gia' presa e la consegna qui.
        SchedulerLease? granted = context.Admission;

        if (granted is null)
        {
            if (!scheduler.TryAcquire(new ScheduleRequest(context.SessionId, context.WorkspaceKey), out SchedulerLease? lease, out ErrorCode busy))
            {
                RouterTelemetry.RecordBusy(busy.ToWire());

                return new ExecutionResult(null, ExecutionState.Failed, null, busy, SideEffectState.None, [], null, 0);
            }

            granted = lease!;
        }

        using SchedulerLease reservation = granted;

        // L'host API deve poter rispondere 202 con l'identificatore prima che il turno finisca:
        // quando lo ha gia' scelto, il coordinatore usa quello invece di generarne un altro.
        string executionId = context.PreassignedExecutionId ?? ids.NewId();
        DateTimeOffset deadline = clock.UtcNow.AddSeconds(context.Policy.TimeoutSeconds);

        using CancellationTokenSource deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineSource.CancelAfter(TimeSpan.FromSeconds(context.Policy.TimeoutSeconds));

        // Il limite e' di inattivita' (23 settembre 2026): ogni pezzo di risposta che arriva lo fa ripartire. Prima era la
        // durata totale, e su un processore tre risposte lunghe sono state buttate dopo 300 s mentre il testo scorreva.
        Inactivity.Value = new InactivityDeadline(deadlineSource, TimeSpan.FromSeconds(context.Policy.TimeoutSeconds));

        try
        {
            return await RunAsync(context, reservation, executionId, deadline, cancellationToken, deadlineSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ExecutionResult(
                executionId, ExecutionState.Cancelled, null, ErrorCode.Cancelled, SideEffectState.None, [], null, 0);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // **Un difetto senza codice non lascia una riga in corso** (difetto trovato dall'audit, S73).
            //
            // Le tre chiusure del turno stanno dentro il percorso normale, e nessuna viene raggiunta
            // quando un'eccezione non prevista scappa da li'. L'esito arrivava comunque al chiamante
            // — l'host API e il canale del worker la traducono in SIDE_EFFECT_UNCERTAIN — ma la riga
            // in `executions` restava `running`: un'esecuzione in corso che nessuno sta eseguendo,
            // che il recupero all'avvio trova non riconciliata e che blocca la sessione.
            //
            // Gli effetti sono `possible` e non `none`: dopo il dispatch nessuno puo' affermare che
            // il servizio non abbia lavorato, ed e' la regola di S49 — che era scritta e non
            // attuata.
            await CloseAfterDefectAsync(executionId, cancellationToken).ConfigureAwait(false);

            // L'eccezione risale comunque: chi sta sopra la traduce gia' e il difetto non va
            // inghiottito qui, altrimenti sparirebbe dai log di chi lo deve correggere.
            throw;
        }
    }

    /// <summary>
    /// Chiude nello store un'esecuzione interrotta da un difetto, dichiarando che non si sa se il
    /// servizio abbia lavorato.
    ///
    /// Passa da <see cref="FailAsync"/> come ogni altra chiusura: una seconda strada verso
    /// <c>CompleteAsync</c> divergerebbe dalla prima senza che nulla lo faccia notare (INV-22). Un
    /// guasto dello store qui non viene propagato: l'eccezione che conta e' quella originale, e
    /// sostituirla con un errore di scrittura nasconderebbe il difetto da correggere.
    /// </summary>
    private async Task CloseAfterDefectAsync(string executionId, CancellationToken cancellationToken)
    {
        try
        {
            await FailAsync(
                executionId,
                null,
                ExecutionState.Uncertain,
                ErrorCode.SideEffectUncertain,
                [],
                CancellationToken.None,
                SideEffectState.Possible).ConfigureAwait(false);
        }
        catch (PaRouterException)
        {
            // Lo store ha rifiutato la chiusura: il difetto originale resta quello da risalire.
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<ExecutionResult> RunAsync(
        ExecutionRequestContext context,
        SchedulerLease lease,
        string executionId,
        DateTimeOffset deadline,
        CancellationToken callerToken,
        CancellationToken deadlineToken)
    {
        context = TurnSpendBudget.Prepare(context);
        TurnSpendBudget spend = new(context.Policy);
        ObservedUsage observedUsage = new();
        // 2. T1: accettazione, idempotency e audit di ingresso.
        string inputHmac = digest.Compute(Encoding.UTF8.GetBytes(context.Input));

        await store.AcceptAsync(
            new AcceptedExecution(
                executionId,
                context.SessionId,
                context.PolicyDigest,
                context.RegistryDigest,
                inputHmac,
                deadline.ToUnixTimeMilliseconds(),
                context.IdempotencyKey is string key ? digest.Compute(Encoding.UTF8.GetBytes(key)) : null,
                ids.NewId())
            {
                // Chi ha una chiave di idempotenza ha anche il digest della richiesta intera a cui
                // legarla: senza, la riga conserverebbe l'HMAC del solo input e il confronto dopo
                // un riavvio sarebbe piu' debole di quello fatto in memoria (S74).
                RequestDigest = context.RequestDigest,
            },
            callerToken).ConfigureAwait(false);

        await store.AppendStateEventAsync(
            executionId, 1, ExecutionState.Accepted, deadline.ToUnixTimeMilliseconds(), callerToken).ConfigureAwait(false);

        // 3-4. Manifest e analisi privacy dell'intero payload trasferibile.
        PrivacyAnalysis analysis;

        long privacyStart = Stopwatch.GetTimestamp();

        try
        {
            using Activity? privacySpan = RouterTelemetry.StartSpan(
                "privacy.analyze",
                ("bytes_bucket", RouterTelemetry.BytesBucket(Encoding.UTF8.GetByteCount(context.Input))));

            analysis = privacy.Analyze(new PrivacyRequest(
                context.Input,
                context.Context,
                context.Policy.MinimumDataClass,
                DeclaredWithAnalyzer(context),
                context.Policy.RedactContacts,
                context.Limits,
                Instructions: context.Instructions,
                Tools: context.Tools?.Gateway.Definitions,
                Grading: context.Policy.PersonalDataGrading));
        }
        catch (PaRouterException ex)
        {
            return await FailAsync(executionId, attemptId: null, ExecutionState.Failed, ex.Code, [], callerToken)
                .ConfigureAwait(false);
        }
        finally
        {
            RouterTelemetry.RecordStage("privacy.analyze", Stopwatch.GetElapsedTime(privacyStart).TotalMilliseconds);
        }

        // Le fasi si dicono a chi guarda il turno, ciascuna con la sua durata (S116). Fino a qui ne arrivava una
        // sola, «Controllo risposta»: la riga del terminale ne annunciava cinque e non diceva dove se ne andasse
        // il tempo. L'esito porta classe, conteggi e identificatori di servizio — mai testo della richiesta.
        ITurnObserver phases = context.Observer ?? NullTurnObserver.Instance;

        phases.Observe(new TurnPhaseEnded(
            "Analisi locale",
            "class=" + analysis.Payload.EffectiveClass.ToWire() + ";certainty=" + analysis.FinalReport.Certainty.ToWire()
            // La difficolta' decide le soglie della banda: senza il suo **nome** un rifiuto mostra
            // tre soglie e nessuna causa, e la causa e' l'unica cosa che l'operatore puo' cambiare.
            + (context.Difficulty is string difficulty ? ";difficulty=" + difficulty : string.Empty)
            + (context.DifficultyReasons is { Length: > 0 } reasons ? ";" + reasons : string.Empty)
            + ";" + ClassReasons(context, analysis),
            Stopwatch.GetElapsedTime(privacyStart)));

        // L'audit registra la classe risultante e la completezza della scansione, mai il testo:
        // servono a ricostruire perche' una destinazione e' stata ammessa o negata.
        List<(string Key, string Value)> privacyMetadata =
        [
            ("dataClass", analysis.Payload.EffectiveClass.ToWire()),
            ("certainty", analysis.FinalReport.Certainty.ToWire()),
        ];

        if (analysis.Payload.InstructionsDigest is string instructionsDigest)
        {
            // Le istruzioni dell'applicazione sono contenuto: nell'audit ne resta il digest.
            privacyMetadata.Add(("instructionsDigest", instructionsDigest));
        }

        await AuditAsync(
            executionId,
            null,
            AuditCodes.PrivacyAnalyzed,
            callerToken,
            [.. privacyMetadata]).ConfigureAwait(false);

        // 5-6. Policy su ogni candidato, poi ranking dei soli ammessi.
        // Con strumenti, il modello scelto deve poter vedere anche cio' che gli strumenti dichiarano di restituire
        // (ADR-020, flusso 2-3): sceglierlo sulla sola domanda porterebbe a fermare la conversazione dopo che uno
        // strumento e' gia' partito.
        DataClass expectedClass = analysis.Payload.EffectiveClass;

        if (context.Tools is ToolSession offeredTools)
        {
            foreach (ToolDefinition offered in offeredTools.Gateway.Definitions)
            {
                if (offeredTools.Gateway.DestinationOf(offered.Name) is ToolDestination declared)
                {
                    expectedClass = DataClasses.Max(expectedClass, declared.ResultClass);
                }
            }
        }

        PolicyRequest policyRequest = new(
            context.Policy.Channel,
            context.Intent,
            CapabilityRouting.ForRequest(context.RequiredCapabilities),
            context.RequiredCapabilities,
            expectedClass,
            analysis.FinalReport.Certainty,
            analysis.Payload.PayloadDigest,
            analysis.Payload.ContextManifestDigest,
            Encoding.UTF8.GetByteCount(context.Input),
            analysis.Payload.Manifest.Sum(entry => entry.ByteLength),
            context.Policy.RequiredObligations,
            context.SnapshotDigest,
            context.RequestedTargetId,
            Band: context.Band)
        {
            HasAttachments = analysis.Payload.Manifest.Any(IsAttachment),
            ExternalConfirmed = context.ExternalConfirmed,
            EstimatedInputTokens = ConversationMeasure.InputTokens(analysis.Payload, [], context.EstimatedTokens),
        };

        PolicyExplain explain;
        long policyStart = Stopwatch.GetTimestamp();

        using (RouterTelemetry.StartSpan("policy.evaluate", ("channel", context.Policy.Channel.ToWire())))
        {
            explain = evaluator.Evaluate(context.Policy, policyRequest, context.Candidates);
        }

        RouterTelemetry.RecordStage("policy.evaluate", Stopwatch.GetElapsedTime(policyStart).TotalMilliseconds);

        phases.Observe(new TurnPhaseEnded(
            "Policy",
            "admissible=" + explain.Admissible.Count.ToString(CultureInfo.InvariantCulture)
            + ";total=" + context.Candidates.Count.ToString(CultureInfo.InvariantCulture)
            + (context.Band is BandRequirements band
                ? ";minContext=" + band.MinContextTokens.ToString(CultureInfo.InvariantCulture)
                : string.Empty)
            // Chi e' passato: «1 ammesso su 10» senza dire quale lasciava l'operatore a indovinare chi aveva poi fallito.
            + (explain.Admissible.Count > 0 ? ";admitted=" + string.Join(',', explain.Admissible.Select(candidate => candidate.TargetId)) : string.Empty)
            // Chi e' rimasto fuori e perche', con i codici: il perche' in parole lo scrive il front end.
            + (explain.Decisions.Any(decision => !decision.Allowed)
                ? ";excluded=" + string.Join(',', explain.Decisions.Where(decision => !decision.Allowed)
                    .Select(decision => decision.TargetId + ":" + string.Join('+', decision.Reasons.Select(reason => reason.ToWire()))))
                : string.Empty),
            Stopwatch.GetElapsedTime(policyStart)));

        // Quanti candidati e quanti ne sono passati. I motivi per singolo candidato stanno nella
        // tabella delle decisioni: ripeterli qui farebbe crescere l'audit senza aggiungere nulla.
        await AuditAsync(
            executionId,
            null,
            AuditCodes.CandidateEvaluated,
            callerToken,
            ("candidates", AuditMetadata.Count(explain.Decisions.Count)),
            ("admissible", AuditMetadata.Count(explain.Admissible.Count))).ConfigureAwait(false);

        // Perimetro debole (ADR-029, ultimo punto di I): la regola `declare` allentava la sola qualita' minima dentro il
        // perimetro. Dal 24 settembre 2026 (ADR-032, fase 3) una qualita' minima non esiste piu', quindi non c'e' niente da
        // allentare: la seconda valutazione avrebbe dato lo stesso esito ed e' stata tolta. Il campo del profilo resta.

        // Con strumenti, solo un modello che sa usarli puo' ricevere la richiesta (ADR-020). Se fra gli ammessi
        // non ce n'e' uno si nega dicendo per quale classe: ripiegare su un modello che ignora gli strumenti
        // darebbe una risposta senza i dati che l'applicazione ha chiesto di usare.
        HashSet<string>? toolCapable = context.Tools is null
            ? null
            : new HashSet<string>(
                context.Candidates.Where(candidate => candidate.SupportsTools).Select(candidate => candidate.TargetId),
                StringComparer.Ordinal);

        if (toolCapable is not null
            && explain.Admissible.Count > 0
            && !explain.Admissible.Any(target => toolCapable.Contains(target.TargetId)))
        {
            ExecutionResult denied = await FailAsync(
                executionId, attemptId: null, ExecutionState.Denied, ErrorCode.NoToolCapableModel, explain.Decisions, callerToken)
                .ConfigureAwait(false);

            return denied with { Detail = Detail(("dataClass", expectedClass.ToWire())) };
        }

        // 7-12. Fino a due tentativi, con la stessa deadline e target distinti. Dal secondo in
        // avanti l'ammissibilita' viene rivalutata: i gate che leggono l'orologio possono aver
        // cambiato esito mentre il primo tentativo era in corso (S78).
        HashSet<string> excluded = new(StringComparer.Ordinal);
        int attemptsUsed = 0;
        ExecutionResult? last = null;

        while (attemptsUsed < context.Policy.MaxAttempts)
        {
            if (attemptsUsed > 0)
            {
                // **Un retry non eredita permessi** (INV-04), e questa e' la sua forma temporale
                // (difetto trovato dall'audit, S78).
                //
                // La policy era valutata una volta sola, prima del ciclo: il ripiego riordinava la
                // stessa lista di ammessi escludendo il primo servizio. Per il payload e' corretto —
                // e' immutabile, e i verdetti sarebbero identici — ma non per i gate che confrontano
                // un istante con l'orologio: la scadenza dell'evidenza di una regione, la freschezza
                // di una probe. Fra i due tentativi possono passare fino a 300 secondi, cioe' la
                // deadline del profilo, e in quella finestra un'evidenza valida al primo tentativo
                // puo' non esserlo piu'.
                //
                // Si rivaluta solo dal secondo giro in avanti: al primo sarebbe la stessa
                // valutazione appena fatta, e il turno normale non paga nulla. Il verdetto nuovo
                // sostituisce quello vecchio anche per il diniego e per le decisioni persistite, che
                // devono dire cio' che valeva **per quel** tentativo.
                using (RouterTelemetry.StartSpan("policy.evaluate", ("channel", context.Policy.Channel.ToWire())))
                {
                    explain = evaluator.Evaluate(spend.RemainingPolicy, policyRequest, context.Candidates);
                }
            }

            RouteDecision route;
            long routingStart = Stopwatch.GetTimestamp();

            using (RouterTelemetry.StartSpan("routing.rank"))
            {
                route = TargetRouter.Rank(
                    [.. explain.Admissible.Where(target =>
                        !excluded.Contains(target.TargetId) && (toolCapable is null || toolCapable.Contains(target.TargetId)))],
                    RoutingOptions.ForChannel(
                        context.PreferSubscription,
                        context.Policy.Channel,
                        context.RequestedTargetId,
                        CapabilityRouting.ForRequest(context.RequiredCapabilities),
                        context.EstimatedTokens,
                        CostEstimate.OutputTokens(context.Limits.MaxOutputBytes)));
            }

            RouterTelemetry.RecordStage("routing.rank", Stopwatch.GetElapsedTime(routingStart).TotalMilliseconds);

            // Il minimo sufficiente (ADR-027): gli stessi ammessi e gli stessi numeri del ranking, un'altra regola. In ombra
            // si racconta accanto alla scelta di rank-v3; quando il profilo di domanda decide (ADR-031) e' la scelta.
            RankedTarget? pick = route.IsEmpty ? null : route.Ranked[0];
            string algorithm = route.AlgorithmVersion;
            bool belowDemand = false;

            if (context.Demand is DemandProfile demand && context.CapabilityLevels is { } levels)
            {
                long shadowStart = Stopwatch.GetTimestamp();
                PA.Router.Routing.Demand.MinimumSufficientResult sufficient = PA.Router.Routing.Demand.MinimumSufficient.Select(
                    demand, route.Ranked, levels, context.NativeHistory?.LastServiceId);
                string outcome;

                if (context.DemandDecides && !route.IsEmpty)
                {
                    RankedTarget? fallback = null;

                    if (sufficient.Chosen is string minimum)
                    {
                        pick = route.Ranked.First(entry => entry.Target.TargetId == minimum);
                    }
                    else
                    {
                        // Nessun ammesso basta: il piu' capace, dichiarato (ADR-031, «rispondi dicendolo»). Non si esce dal
                        // perimetro per cercarne uno migliore: gli ammessi sono gia' tutto cio' che la policy consente.
                        fallback = PA.Router.Routing.Demand.MinimumSufficient.MostCapable(route.Ranked, levels);
                        pick = fallback;
                        belowDemand = true;
                    }

                    algorithm = PA.Router.Routing.Demand.MinimumSufficient.Version;
                    outcome = PA.Router.Routing.Demand.MinimumSufficient.Describe(sufficient, pick!.Target.TargetId)
                        + ";decisive=1"
                        + (fallback is not null ? ";below=1;fallback=" + fallback.Target.TargetId : string.Empty);
                }
                else
                {
                    outcome = PA.Router.Routing.Demand.MinimumSufficient.Describe(sufficient, route.Chosen?.TargetId);
                }

                phases.Observe(new TurnPhaseEnded("Minimo sufficiente", outcome, Stopwatch.GetElapsedTime(shadowStart)));
            }

            // Chi risponde davvero, dopo l'eventuale scelta del minimo sufficiente: il racconto legge da qui il servizio e
            // il suo punteggio, e non deve nominare il primo di rank-v3 quando ha scelto il profilo di domanda.
            phases.Observe(new TurnPhaseEnded(
                "Selezione target",
                RouteOutcome(route, excluded, pick, algorithm),
                Stopwatch.GetElapsedTime(routingStart)));

            if (pick is null)
            {
                return last ?? await DenyAsync(executionId, explain, context, policyRequest.EffectiveClass, callerToken).ConfigureAwait(false);
            }

            attemptsUsed++;
            AdmissibleTarget chosen = pick.Target;

            (string zone, string perimeter) = Perimeter(context, chosen.TargetId, policyRequest);

            await AuditAsync(
                executionId,
                null,
                AuditCodes.RouteSelected,
                callerToken,
                ("targetId", chosen.TargetId),
                ("capability", chosen.Capability.ToWire()),
                ("zone", zone),
                ("perimeter", perimeter),
                ("algorithmVersion", algorithm),
                ("tier", AuditMetadata.Count(pick.Tier)),
                ("score", pick.Components.RoundedScore.ToString(
                    "0.000000", System.Globalization.CultureInfo.InvariantCulture))).ConfigureAwait(false);

            // Le componenti del punteggio esistono solo qui: `AttemptAsync` riceve i candidati
            // ammessi, non la classifica. Senza questa mappa `score_json` resterebbe null, ed e' il
            // motivo per cui dallo store non si ricostruiva **perche'** un servizio avesse vinto.
            Dictionary<string, RankedTarget> ranked = route.Ranked.ToDictionary(
                entry => entry.Target.TargetId,
                entry => entry,
                StringComparer.Ordinal);

            ExecutionResult attempt = await AttemptAsync(
                context, lease, executionId, chosen, explain, ranked, analysis, policyRequest, spend, observedUsage,
                attemptsUsed, deadline, callerToken, deadlineToken)
                .ConfigureAwait(false);

            if (attempt.State is ExecutionState.Completed or ExecutionState.Denied or ExecutionState.Cancelled)
            {
                return attempt.State is ExecutionState.Completed
                    ? attempt with { BelowDemand = belowDemand }
                    : attempt;
            }

            last = attempt;

            // Un secondo tentativo esiste solo se il primo non puo' aver prodotto effetti e non ha
            // pubblicato nulla: dopo un output non si ritenta mai (INV-07).
            // Un secondo tentativo solo per un guasto del servizio, prima di qualunque effetto e di qualunque byte di
            // risposta, e con tempo ancora disponibile. Una risposta bloccata, un errore di protocollo o un limite non si
            // ripetono altrove: manderebbero lo stesso payload a un altro servizio (ERRORS.md).
            if (!AttemptLifecycle.CanRetry(
                    attemptsUsed,
                    context.Policy.MaxAttempts,
                    attempt.SideEffects,
                    attempt.HasOutput || attempt.OutputBytes > 0,
                    attempt.ErrorCode)
                || clock.UtcNow >= deadline)
            {
                return attempt;
            }

            // Il ripiego e' una decisione, non un dettaglio: viene registrato prima di ripetere,
            // con il servizio escluso e il motivo per cui il primo tentativo non e' bastato.
            await AuditAsync(
                executionId,
                null,
                AuditCodes.FallbackConsidered,
                callerToken,
                ("targetId", chosen.TargetId),
                ("attempt", AuditMetadata.Count(attemptsUsed)),
                ("errorCode", attempt.ErrorCode?.ToWire() ?? "none")).ConfigureAwait(false);

            excluded.Add(chosen.TargetId);
            scheduler.ReleaseTarget(lease);
        }

        return last ?? await DenyAsync(executionId, explain, context, policyRequest.EffectiveClass, callerToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Registra un evento di audit. Un guasto qui non annulla il turno gia' avviato: l'evento di
    /// ingresso e l'intenzione di dispatch restano gli unici scritti nella stessa transazione
    /// delle decisioni, e sono quelli su cui la pipeline si ferma se non riesce a scrivere.
    /// </summary>
    private async Task AuditAsync(
        string executionId,
        string? attemptId,
        string code,
        CancellationToken cancellationToken,
        params (string Key, string Value)[] metadata)
    {
        try
        {
            await store.AppendAuditAsync(
                new AuditRecord(ids.NewId(), executionId, attemptId, code, AuditMetadata.ToJson(metadata)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (PaRouterException)
        {
            // Lo store ha rifiutato la scrittura: il turno prosegue e il guasto viene contato
            // dalla telemetria, che esiste per questo.
            RouterTelemetry.RecordAuditFailure();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Progress audit must not erase the effect state held by the dispatch loop on cancellation.
            // Effect-boundary audit uses DispatchAudit instead and never absorbs a failed commit.
            RouterTelemetry.RecordAuditFailure();
        }
    }

    private async Task<ExecutionResult> AttemptAsync(
        ExecutionRequestContext context,
        SchedulerLease lease,
        string executionId,
        AdmissibleTarget target,
        PolicyExplain explain,
        IReadOnlyDictionary<string, RankedTarget> ranked,
        PrivacyAnalysis analysis,
        PolicyRequest policyRequest,
        TurnSpendBudget spend,
        ObservedUsage observedUsage,
        int ordinal,
        DateTimeOffset deadline,
        CancellationToken callerToken,
        CancellationToken deadlineToken)
    {
        TargetDispatchInfo dispatchInfo = context.Dispatch[target.TargetId];
        PolicyCandidate candidate = context.Candidates.Single(entry => entry.TargetId == target.TargetId);

        if (lease.ReservedTargetId is null
            && !scheduler.TryReserveTarget(lease, target.TargetId, dispatchInfo.MaxConcurrent))
        {
            return new ExecutionResult(
                executionId, ExecutionState.Failed, null, ErrorCode.TargetUnavailable,
                SideEffectState.None, explain.Decisions, observedUsage.Value, ordinal);
        }

        string attemptId = ids.NewId();

        if (!spend.TryReserve(candidate, policyRequest.EstimatedInputTokens))
        {
            return await FailAsync(executionId, null, ExecutionState.Denied, ErrorCode.NoAdmissibleTarget,
                explain.Decisions, callerToken, usage: observedUsage.Value).ConfigureAwait(false);
        }

        ExecutionGrant grant = grants.Issue(
            target,
            executionId,
            attemptId,
            GrantPurpose.Foreground,
            analysis.Payload.ContextManifestDigest,
            context.PolicyDigest,
            context.RegistryDigest);

        SideEffectState sideEffects = AttemptLifecycle.OnDispatch(target.Kind);

        // Il target e' un identificatore di configurazione, non una persona ne' un contenuto: resta
        // nella allowlist proprio perche' senza di esso un conteggio di tentativi non direbbe nulla.
        RouterTelemetry.RecordAttempt(target.TargetId, target.Adapter);

        // 8. T2 committato PRIMA del dispatch: se il processo muore subito dopo l'avvio del
        //    target, questa riga e' l'unica prova che qualcosa e' partito (INV-06).
        await store.CommitDispatchIntentAsync(
            new DispatchIntent(
                executionId,
                attemptId,
                ordinal,
                target.TargetId,
                digest.Compute(Encoding.UTF8.GetBytes(grant.Nonce)),
                digest.Compute(analysis.Payload.Bytes.Span),
                sideEffects,
                [.. explain.Decisions.Select(decision => ToStored(decision, ranked))],
                ids.NewId()),
            callerToken).ConfigureAwait(false);

        // Il tentativo e' partito: l'intenzione era gia' scritta, questo dice che l'adapter e'
        // stato davvero contattato.
        await AuditAsync(
            executionId,
            attemptId,
            AuditCodes.AttemptStarted,
            callerToken,
            ("targetId", target.TargetId),
            ("ordinal", AuditMetadata.Count(ordinal))).ConfigureAwait(false);

        await store.AppendStateEventAsync(
            executionId, ordinal + 1, ExecutionState.Running, deadline.ToUnixTimeMilliseconds(), callerToken)
            .ConfigureAwait(false);

        // Validazione immediatamente prima del dispatch: scadenza, scopo e digest.
        GrantRejection rejection = grants.Redeem(
            grant,
            target.TargetId,
            GrantPurpose.Foreground,
            analysis.Payload.PayloadDigest,
            analysis.Payload.ContextManifestDigest,
            context.PolicyDigest,
            context.RegistryDigest);

        if (rejection is not GrantRejection.None)
        {
            return await FailAsync(
                executionId, attemptId, ExecutionState.Failed, ErrorCode.ControlUnavailable, explain.Decisions, callerToken,
                usage: observedUsage.Value)
                .ConfigureAwait(false);
        }

        AuthorizedExecution authorized = Authorize(grant, target, analysis.Payload, context, dispatchInfo, []) with
        {
            NativeSession = NativeSessionFor(context, target, dispatchInfo),
        };

        return await DispatchAsync(
                context, executionId, attemptId, target, explain, analysis, policyRequest, spend, observedUsage, dispatchInfo, authorized, sideEffects,
                ordinal, callerToken, deadlineToken)
            .ConfigureAwait(false);
    }

    private async Task<ExecutionResult> DispatchAsync(
        ExecutionRequestContext context,
        string executionId,
        string attemptId,
        AdmissibleTarget target,
        PolicyExplain explain,
        PrivacyAnalysis analysis,
        PolicyRequest policyRequest,
        TurnSpendBudget spend,
        ObservedUsage observedUsage,
        TargetDispatchInfo dispatchInfo,
        AuthorizedExecution authorized,
        SideEffectState sideEffects,
        int ordinal,
        CancellationToken callerToken,
        CancellationToken deadlineToken)
    {
        if (!adapters.TryGetValue(target.Adapter, out IExecutionAdapter? adapter))
        {
            return await FailAsync(
                executionId, attemptId, ExecutionState.Failed, ErrorCode.TargetUnavailable, explain.Decisions, callerToken,
                usage: observedUsage.Value)
                .ConfigureAwait(false);
        }

        using Activity? attemptSpan = RouterTelemetry.StartSpan(
            "adapter.attempt",
            ("target_id", target.TargetId),
            ("target_kind", target.Kind.ToWire()),
            ("adapter", target.Adapter.ToWire()));

        long attemptStart = Stopwatch.GetTimestamp();

        // Il giro modello -> strumento -> modello (ADR-020). Il modello resta quello scelto per tutta la
        // conversazione: la cronologia con gli strumenti non si trasferisce fra fornitori (INV-08).
        List<ToolRound> rounds = [];
        AuthorizedExecution current = authorized;

        // Chi guarda il turno, e il rilascio a blocchi che ne consegue. Senza osservatore restano
        // entrambi assenti: il buffer e lo screening finale sono quelli di sempre, e nessuno paga
        // una scansione in piu'.
        ITurnObserver observer = context.Observer ?? NullTurnObserver.Instance;
        StreamingScreener? streaming = context.Observer is null
            ? null
            : new StreamingScreener(
                screener,
                context.Limits,
                context.Policy.RedactContacts,
                context.Policy.MinimumDataClass);

        DataClass conversationClass = authorized.Payload.EffectiveClass;
        ScanCertainty certainty = analysis.FinalReport.Certainty;
        UsageReport? usage = observedUsage.Value;
        int totalCalls = 0;
        int executedCalls = 0;
        ToolStop? stop = null;
        ModelReply reply;

        while (true)
        {
            // 9. Gli eventi vengono normalizzati; i frammenti finiscono solo nel buffer interno.
            reply = await CallModelAsync(
                    adapter,
                    current,
                    context.Policy.MaxOutputBytes,
                    streaming,
                    observer,
                    callerToken,
                    deadlineToken)
                .ConfigureAwait(false);

            // Il consumo lo dichiara il provider; il costo lo calcola il router dal prezzo del registro, dichiarandolo tale.
            UsageReport? priced = NormalizeUsage(reply.Usage)?.PricedWith(context.Dispatch.GetValueOrDefault(target.TargetId)?.Pricing);
            usage = observedUsage.Add(priced);

            if (priced is UsageReport reported)
            {
                // Il consumo lo dichiara il provider: il router non lo stima e non lo inventa.
                observer.Observe(new TurnUsageObserved(reported));
            }

            if (reply.ArtifactChanged)
            {
                sideEffects = SideEffectState.Observed;
            }

            if (reply.Failure is not null || reply.Buffer.Overflowed || reply.Calls.Count == 0)
            {
                break;
            }

            if (context.Tools is not ToolSession tools)
            {
                // Un modello che chiede strumenti mai offerti non segue il contratto della richiesta.
                reply = reply with
                {
                    Failure = new AdapterFailed(ErrorCode.ProtocolError, "The model asked for tools that were not offered."),
                };

                break;
            }

            int round = rounds.Count + 1;
            stop = LimitStop(tools.Limits, rounds.Count, reply.Calls.Count, totalCalls);

            if (stop is not null)
            {
                break;
            }

            totalCalls += reply.Calls.Count;

            // Ogni chiamata si controlla prima di eseguirne una qualunque: una chiamata negata ferma il giro, e
            // le altre dello stesso giro non partono.
            List<PreparedCall> prepared = [];

            foreach (ToolCall call in reply.Calls)
            {
                try
                {
                    (PreparedCall? checkedCall, ToolStop? denied) = await CheckCallAsync(
                        context, executionId, attemptId, tools, call, round, conversationClass, callerToken).ConfigureAwait(false);
                    if (denied is not null)
                    {
                        stop = denied;
                        break;
                    }
                    prepared.Add(checkedCall!);
                }
                catch (OperationCanceledException)
                {
                    stop = new ToolStop(ExecutionState.Cancelled, ErrorCode.Cancelled, Detail(("reason", "cancelled")));
                    break;
                }
            }

            if (stop is not null)
            {
                break;
            }

            try
            {
                // Commit every real call's intent before starting any call in this batch.
                foreach (PreparedCall allowed in prepared.Where(entry => entry.Rejection is null))
                {
                    await dispatchAudit.WriteAsync(executionId, attemptId, AuditCodes.ToolCallStarted, deadlineToken,
                        ("tool", allowed.Call.Name), ("toolSource", allowed.Destination!.SourceId),
                        ("round", AuditMetadata.Count(round))).ConfigureAwait(false);
                }
            }
            catch (PaRouterException exception)
            {
                reply = reply with { Failure = new AdapterFailed(exception.Code, "The tool dispatch intent could not be committed.") };
                break;
            }
            catch (OperationCanceledException)
            {
                reply = reply with
                {
                    Failure = new AdapterFailed(callerToken.IsCancellationRequested ? ErrorCode.Cancelled : ErrorCode.ProviderTimeout,
                    "The tool dispatch was cancelled before its audit committed.")
                };
                break;
            }

            // Negli eventi si vede che il turno sta chiamando strumenti, e nient'altro: ne' quali, ne' con che cosa.
            await AppendProgressEventAsync(executionId, ExecutionState.ToolCalling).ConfigureAwait(false);

            // Le chiamate dello stesso giro non dipendono l'una dall'altra: partono insieme.
            (PreparedCall Call, ToolInvocation Invocation)[] invoked;

            try
            {
                deadlineToken.ThrowIfCancellationRequested();
                int realCalls = prepared.Count(entry => entry.Rejection is null);
                executedCalls += realCalls;
                if (realCalls > 0 && sideEffects is SideEffectState.None) { sideEffects = SideEffectState.Possible; }
                invoked = await Task.WhenAll(prepared.Select(entry => InvokeAsync(tools, entry, deadlineToken)))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                reply = reply with
                {
                    Failure = new AdapterFailed(callerToken.IsCancellationRequested ? ErrorCode.Cancelled : ErrorCode.ProviderTimeout,
                        "The tool invocation was interrupted."),
                };

                break;
            }

            // Ogni risultato si esamina prima che il modello lo veda.
            List<ToolCallResult> results = [];

            foreach ((PreparedCall entry, ToolInvocation invocation) in invoked)
            {
                if (entry.Rejection is not null)
                {
                    results.Add(new ToolCallResult(entry.Call.CallId, entry.Call.Name, invocation.Content, IsError: true));
                    continue;
                }

                ToolDestination destination = entry.Destination!;

                stop = invocation.Failure switch
                {
                    ToolInvocationFailure.None when Encoding.UTF8.GetByteCount(invocation.Content) > tools.Limits.MaxResultBytes =>
                        Limit("result_size", entry.Call.Name),
                    ToolInvocationFailure.None => null,
                    ToolInvocationFailure.Timeout => Limit("call_time", entry.Call.Name),
                    ToolInvocationFailure.CredentialsRejected => new ToolStop(
                        ExecutionState.Failed,
                        ErrorCode.ToolCredentialsRejected,
                        Detail(("toolSource", destination.SourceId), ("tool", entry.Call.Name))),
                    _ => new ToolStop(
                        ExecutionState.Failed,
                        ErrorCode.ToolSourceUnavailable,
                        Detail(("toolSource", destination.SourceId), ("tool", entry.Call.Name))),
                };

                if (stop is not null)
                {
                    await AuditAsync(
                        executionId,
                        attemptId,
                        AuditCodes.ToolCallCompleted,
                        callerToken,
                        ("tool", entry.Call.Name),
                        ("toolSource", destination.SourceId),
                        ("round", AuditMetadata.Count(round)),
                        ("errorCode", stop.Code.ToWire())).ConfigureAwait(false);

                    break;
                }

                // Il risultato e' un dato con la sua provenienza: si esamina con il floor del profilo, si
                // redigono i contatti se il profilo lo chiede, e la classe dichiarata per cio' che lo strumento
                // restituisce vale come minimo.
                SurfaceAnalysis examined = privacy.AnalyzeSurface(
                    invocation.Content, context.Policy.MinimumDataClass, context.Policy.RedactContacts);

                DataClass resultClass = DataClasses.Max(examined.EffectiveClass, destination.ResultClass);
                conversationClass = DataClasses.Max(conversationClass, resultClass);

                if (examined.Report.Certainty is ScanCertainty.Incomplete)
                {
                    certainty = ScanCertainty.Incomplete;
                }

                results.Add(new ToolCallResult(entry.Call.CallId, entry.Call.Name, examined.Text, invocation.IsError));

                await AuditAsync(
                    executionId,
                    attemptId,
                    AuditCodes.ToolCallCompleted,
                    callerToken,
                    ("tool", entry.Call.Name),
                    ("toolSource", destination.SourceId),
                    ("round", AuditMetadata.Count(round)),
                    ("dataClass", resultClass.ToWire())).ConfigureAwait(false);
            }

            if (stop is not null)
            {
                break;
            }

            rounds.Add(new ToolRound(reply.Buffer.ToUnscreenedText(), reply.Calls, results));

            await AppendProgressEventAsync(executionId, ExecutionState.Running).ConfigureAwait(false);

            // Il modello fissato deve poter vedere tutto cio' che ricevera'. Se un risultato alza la classe oltre
            // quello che il profilo gli concede, la conversazione si ferma: nessun ripiego su un altro modello.
            string conversationDigest = ConversationDigest(analysis.Payload.PayloadDigest, rounds);
            int inputTokens = ConversationMeasure.InputTokens(authorized.Payload, rounds, context.EstimatedTokens);
            PolicyCandidate currentCandidate = context.Candidates.Single(candidate => candidate.TargetId == target.TargetId);
            PolicyRequest nextRequest = policyRequest with
            {
                EffectiveClass = conversationClass,
                Certainty = certainty,
                PayloadDigest = conversationDigest,
                EstimatedInputTokens = inputTokens,
                Band = new BandRequirements(Math.Max(policyRequest.Band?.MinContextTokens ?? 0,
                    ConversationMeasure.RequiredWindow(inputTokens, current.MaxOutputTokens))),
            };

            PolicyExplain renewed = evaluator.Evaluate(
                spend.RemainingPolicy,
                nextRequest,
                [.. context.Candidates.Where(candidate => string.Equals(candidate.TargetId, target.TargetId, StringComparison.Ordinal))]);

            if (renewed.Admissible.Count == 0)
            {
                explain = renewed;
                await AuditAsync(
                    executionId,
                    attemptId,
                    AuditCodes.ToolCallDenied,
                    callerToken,
                    ("targetId", target.TargetId),
                    ("round", AuditMetadata.Count(round)),
                    ("dataClass", conversationClass.ToWire()),
                    ("reason", "result_not_admissible")).ConfigureAwait(false);

                stop = new ToolStop(
                    sideEffects is SideEffectState.None ? ExecutionState.Denied : ExecutionState.Uncertain,
                    ErrorCode.ToolResultDenied,
                    Detail(
                        // Solo nomi di strumenti offerti: un nome inventato dal modello puo' contenere dati della conversazione.
                        ("tool", string.Join(
                            ",",
                            results.Where(result => tools.Gateway.DestinationOf(result.Name) is not null)
                                .Select(result => result.Name)
                                .Distinct(StringComparer.Ordinal))),
                        ("dataClass", conversationClass.ToWire()),
                        ("targetId", target.TargetId)));

                break;
            }

            // Un giro nuovo e' un payload nuovo: grant nuovo, legato al digest della conversazione.
            AdmissibleTarget nextTarget = renewed.Admissible[0];

            ExecutionGrant nextGrant = grants.Issue(
                nextTarget,
                executionId,
                attemptId,
                GrantPurpose.Foreground,
                analysis.Payload.ContextManifestDigest,
                context.PolicyDigest,
                context.RegistryDigest);

            if (!spend.TryReserve(currentCandidate, inputTokens))
            {
                reply = reply with { Failure = new AdapterFailed(ErrorCode.NoAdmissibleTarget, "The remaining execution budget cannot cover the next dispatch.") };
                break;
            }

            try
            {
                await dispatchAudit.WriteAsync(executionId, attemptId, AuditCodes.AttemptDispatchIntent, deadlineToken,
                    ("targetId", target.TargetId), ("round", AuditMetadata.Count(round)),
                    ("payloadDigest", conversationDigest),
                    ("grantNonceDigest", digest.Compute(Encoding.UTF8.GetBytes(nextGrant.Nonce)))).ConfigureAwait(false);
            }
            catch (PaRouterException exception)
            {
                reply = reply with { Failure = new AdapterFailed(exception.Code, "The model dispatch intent could not be committed.") };
                break;
            }
            catch (OperationCanceledException)
            {
                reply = reply with
                {
                    Failure = new AdapterFailed(callerToken.IsCancellationRequested ? ErrorCode.Cancelled : ErrorCode.ProviderTimeout,
                    "The model dispatch was cancelled before its audit committed.")
                };
                break;
            }

            GrantRejection rejection = grants.Redeem(
                nextGrant,
                target.TargetId,
                GrantPurpose.Foreground,
                conversationDigest,
                analysis.Payload.ContextManifestDigest,
                context.PolicyDigest,
                context.RegistryDigest);

            if (rejection is not GrantRejection.None)
            {
                reply = reply with
                {
                    Failure = new AdapterFailed(ErrorCode.ControlUnavailable, "The grant of the next round was rejected."),
                };

                break;
            }

            current = Authorize(
                nextGrant,
                nextTarget,
                authorized.Payload with { PayloadDigest = conversationDigest, EffectiveClass = conversationClass },
                context,
                dispatchInfo,
                [.. rounds]) with
            {
                NativeSession = authorized.NativeSession,
                ContextWindowTokens = WorkingWindow(currentCandidate.ContextWindowTokens, nextRequest.Band?.MinContextTokens ?? 0,
                    inputTokens, current.MaxOutputTokens),
            };
        }

        RouterTelemetry.RecordStage("adapter.attempt", Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds);

        // L'esito e' quello vero: un guasto dell'adapter — un timeout, un protocollo rotto — arrivava
        // qui dopo che la fase aveva gia' detto «ok», e il riepilogo di un turno fallito conteneva
        // «il servizio ha risposto» accanto a PROVIDER_TIMEOUT.
        observer.Observe(new TurnPhaseEnded(
            "Esecuzione",
            stop is not null ? stop.Code.ToWire()
                : reply.Failure is AdapterFailed adapterFailure
                    ? adapterFailure.Code.ToWire() + (adapterFailure.HttpStatus is int http ? " (HTTP " + http.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : string.Empty)
                : "ok",
            Stopwatch.GetElapsedTime(attemptStart)));

        if (stop is not null)
        {
            attemptSpan?.SetTag("reason", stop.Code.ToWire());

            ExecutionResult stopped = await FailAsync(
                executionId, attemptId, sideEffects is SideEffectState.None ? stop.State : ExecutionState.Uncertain,
                stop.Code, explain.Decisions, CancellationToken.None, sideEffects, usage, ordinal)
                .ConfigureAwait(false);

            return stopped with { Detail = stop.Detail };
        }

        if (reply.Failure is AdapterFailed failure)
        {
            ExecutionState state = sideEffects is SideEffectState.None && failure.Code is ErrorCode.Cancelled
                ? ExecutionState.Cancelled : AttemptLifecycle.AfterDispatchFailure(sideEffects);

            // Quota finita per piu' di un momento (24 settembre 2026): il servizio va in pausa, e chi guarda il turno lo
            // sa adesso — non al decimo turno che lo salta in silenzio. La fase porta servizio e data, nessun contenuto.
            if (failure is { Code: ErrorCode.QuotaExhausted, QuotaLasting: true })
            {
                try
                {
                    QuotaExhausted?.Invoke(target.TargetId, failure.QuotaUntil);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Una pausa non scritta costa un tentativo al turno dopo, che la riscrive: il turno non si ferma.
                }

                observer.Observe(new TurnPhaseEnded(
                    "Quota",
                    "service=" + target.TargetId + ";until="
                        + (failure.QuotaUntil?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm'Z'", System.Globalization.CultureInfo.InvariantCulture) ?? "manual"),
                    TimeSpan.Zero));
            }

            if (failure.Code is ErrorCode.ProtocolError)
            {
                RouterTelemetry.RecordProtocolError(target.Adapter.ToWire());
            }

            attemptSpan?.SetTag("reason", failure.Code.ToWire());

            ExecutionResult failed = await FailAsync(
                executionId,
                attemptId,
                state,
                failure.Code,
                explain.Decisions,
                CancellationToken.None,
                sideEffects,
                usage,
                ordinal).ConfigureAwait(false);

            // I byte gia' arrivati contano: un servizio che ha cominciato a rispondere ha gia' lavorato sul payload.
            return failed with { OutputBytes = reply.Buffer.ByteLength };
        }

        // 11. Screening del messaggio completo. Prima di qui nulla e' uscito.
        if (reply.Buffer.Overflowed)
        {
            RouterTelemetry.RecordOutputBlocked(ErrorCode.OutputLimit.ToWire());

            return await FailAsync(
                executionId, attemptId, AttemptLifecycle.AfterDispatchFailure(sideEffects), ErrorCode.OutputLimit,
                explain.Decisions, CancellationToken.None, sideEffects, usage, ordinal).ConfigureAwait(false);
        }

        // 11bis. La conclusione del rilascio a blocchi (S71). Fino a qui `Complete()` esisteva e non
        // la chiamava nessuno: la coda trattenuta arrivava a schermo perche' il terminale la
        // ricavava dal testo finale, e le regole 4 e 5bis di 03-specs/PRIVACY.md — trattenere gli
        // ultimi caratteri **finche' la risposta non e' conclusa**, e lasciare alla conclusione il
        // compito di chiudere quando l'incompletezza persiste — non avevano attuazione.
        //
        // Il verdetto qui e' preso su **tutto cio' che e' comparso** in questo tentativo, giri degli
        // strumenti compresi, mentre lo screening del punto 11 guarda la risposta dell'ultimo giro:
        // sono due domande diverse, e questa e' quella che difende lo schermo. Un finding che nasce
        // dall'unione di due giri esiste solo qui.
        StreamCompletion? completion = streaming?.Complete();

        if (completion is { Full.IsAllowed: false })
        {
            // Regola 5bis: alla fine non c'e' un confine successivo a cui riprovare, quindi
            // un'incompletezza che persiste chiude l'output invece di lasciarlo passare.
            RouterTelemetry.RecordOutputBlocked(completion.Full.ErrorCode!.Value.ToWire());

            // Il motivo si dice (23 settembre 2026): una risposta pagata e bloccata senza spiegazione non si corregge.
            observer.Observe(new TurnPhaseEnded("Controllo risposta", Blocked(completion.Full), TimeSpan.Zero));

            return await FailAsync(
                executionId, attemptId, AttemptLifecycle.AfterDispatchFailure(sideEffects), completion.Full.ErrorCode!.Value,
                explain.Decisions, CancellationToken.None, sideEffects, usage, ordinal).ConfigureAwait(false);
        }

        ScreenedOutput screened;
        long screenStart = Stopwatch.GetTimestamp();

        using (RouterTelemetry.StartSpan("output.screen"))
        {
            screened = screener.Screen(
                reply.Buffer.ToUnscreenedText(),
                context.Limits,
                context.Policy.RedactContacts,
                context.Policy.MinimumDataClass,
                final: true);
        }

        RouterTelemetry.RecordStage("output.screen", Stopwatch.GetElapsedTime(screenStart).TotalMilliseconds);

        // La stessa misura che va alla telemetria va anche a chi guarda il turno, con l'esito: senza
        // durata non si sa dove se ne vada il tempo di una richiesta.
        observer.Observe(new TurnPhaseEnded(
            "Controllo risposta",
            screened.IsAllowed ? "ammessa" : Blocked(screened),
            Stopwatch.GetElapsedTime(screenStart)));

        if (!screened.IsAllowed)
        {
            RouterTelemetry.RecordOutputBlocked(screened.ErrorCode!.Value.ToWire());

            return await FailAsync(
                executionId, attemptId, AttemptLifecycle.AfterDispatchFailure(sideEffects), screened.ErrorCode!.Value,
                explain.Decisions, CancellationToken.None, sideEffects, usage, ordinal).ConfigureAwait(false);
        }

        if (callerToken.IsCancellationRequested)
        {
            return await FailAsync(executionId, attemptId,
                sideEffects is SideEffectState.None ? ExecutionState.Cancelled : ExecutionState.Uncertain,
                ErrorCode.Cancelled, explain.Decisions, CancellationToken.None, sideEffects, usage, ordinal).ConfigureAwait(false);
        }

        // La coda trattenuta esce **adesso**, e solo adesso: e' l'attuazione della regola 4. Prima
        // restava allo screener e chi guardava non la riceveva mai, quindi il terminale la ricavava
        // sottraendo il prefisso gia' mostrato dal testo finale — una seconda strada per la stessa
        // cosa, che divergeva dalla specifica senza che nulla lo dicesse.
        //
        // Esce dopo il punto 11: cio' che non e' ammesso non compare, nemmeno in coda.
        if (completion?.Remainder is string tail && tail.Length > 0)
        {
            observer.Observe(new TurnBlockReleased(tail));
        }

        // 12. T3: esito, audit terminale e solo dopo il commit si pubblica completed.
        using Activity? commitSpan = RouterTelemetry.StartSpan("storage.commit");

        await store.CompleteAsync(
            new ExecutionOutcome(
                executionId,
                attemptId,
                ExecutionState.Completed,
                AttemptState.Completed,
                sideEffects,
                null,
                usage is null ? null : JsonSerializer.Serialize(usage),
                ContentAvailability.Unavailable,
                [],
                ids.NewId()),
            CancellationToken.None).ConfigureAwait(false);

        // Lo stato terminale e' un evento come gli altri: chi osserva lo stream deve vederlo
        // arrivare, e lo stream si chiude su di esso.
        return new ExecutionResult(
            executionId,
            ExecutionState.Completed,
            screened.Text,
            null,
            sideEffects,
            explain.Decisions,
            usage,
            ordinal)
        {
            ToolsUnused = context.Tools is { Gateway.Definitions.Count: > 0 } && executedCalls == 0,
            ConversationClass = conversationClass,
            TargetId = target.TargetId,
            NativeSessionId = authorized.NativeSession?.Id,
        };
    }

    /// <summary>
    /// Perche' e' stato scelto quel servizio, in una forma che ogni front end puo' rendere nella propria lingua:
    /// identificatori e numeri del ranking, mai contenuti. Chi guarda un turno deve poter giustificare la scelta —
    /// componenti, pesi, versione dell'algoritmo e chi e' arrivato dopo — senza aprire l'audit.
    /// </summary>
    private static string Neutral(TargetCalibration calibration) => string.Join('+', new[]
    {
        calibration.Quality is null ? "quality" : null,
        calibration.LatencyScore is null ? "latency" : null,
        calibration.Price is null && calibration.CostScore is null ? "cost" : null,
        // Con quota disponibile la componente e' neutra finche' nessuno misura la quota residua (ADR-032).
        calibration.QuotaState is QuotaState.Available && calibration.QuotaScore is not null ? null : "quota",
    }.OfType<string>());

    /// <summary>
    /// Un pezzo del payload che e' un allegato (ADR-029, decisione I): un file dell'operatore (<c>@percorso</c>,
    /// <c>--attach</c>) o un contenuto dell'applicazione, cioe' materiale con provenienza <c>workspace</c>. Non lo sono la
    /// skill, testo scelto dall'operatore, ne' cio' che costruisce il router: capsule di un handover, istruzioni e
    /// strumenti dell'applicazione.
    /// </summary>
    private static bool IsAttachment(ContextManifestEntry entry) =>
        entry.Provenance is Provenance.Workspace
        && !HistoryItems.Is(entry.ItemId)
        && entry.ItemId is not ("handover-capsule" or "instructions" or "tools");


    /// <summary>
    /// Zona del servizio scelto e posizione rispetto al perimetro (ADR-029): <c>inside</c>, oppure <c>outside</c> con la
    /// regola che lo ha permesso — <c>confirmed</c> se l'operatore lo ha confermato, <c>attachments</c> se c'erano
    /// allegati e il profilo ne ammette l'uscita. Finisce nell'audit: un'uscita si registra sempre.
    /// </summary>
    private (string Zone, string Perimeter) Perimeter(ExecutionRequestContext context, string targetId, PolicyRequest request)
    {
        if (context.Candidates.FirstOrDefault(candidate => string.Equals(candidate.TargetId, targetId, StringComparison.Ordinal))
            is not PolicyCandidate candidate)
        {
            return ("unknown", "unknown");
        }

        TrustZone zone = TrustZones.Of(candidate);

        if (context.Policy.Zones.Perimeter.Contains(zone))
        {
            return (zone.ToWire(), "inside");
        }

        string rule = request.HasAttachments ? "outside_attachments"
            : request.ExternalConfirmed && context.Policy.Zones.ConfirmOutside.Contains(request.EffectiveClass) ? "outside_confirmed"
            : "outside";

        return (zone.ToWire(), rule);
    }

    private static string RouteOutcome(RouteDecision route, IReadOnlyCollection<string> tried, RankedTarget? pick, string algorithm)
    {
        if (route.IsEmpty || pick is null)
        {
            // Al secondo giro, dopo un guasto, la selezione vuota cancellava dal racconto chi era stato scelto e aveva
            // fallito: «nessun servizio» accanto a TARGET_UNAVAILABLE non diceva quale servizio non aveva risposto.
            return tried.Count > 0 ? "service=-;tried=" + string.Join(',', tried) : "service=-";
        }

        static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        RankedTarget first = pick;
        IEnumerable<RankedTarget> others = route.Ranked.Where(item => !ReferenceEquals(item, pick));

        return "service=" + first.Target.TargetId
            + ";score=" + Number(first.Components.Score)
            + ";quality=" + Number(first.Components.Quality) + ";wQuality=" + Number(RankingAlgorithm.QualityWeight)
            + ";latency=" + Number(first.Components.Latency) + ";wLatency=" + Number(RankingAlgorithm.LatencyWeight)
            + ";cost=" + Number(first.Components.Cost) + ";wCost=" + Number(RankingAlgorithm.CostWeight)
            + ";quota=" + Number(first.Components.Quota) + ";wQuota=" + Number(RankingAlgorithm.QuotaWeight)
            // Quali componenti valgono il neutro perche' manca il dato: un 5 che viene da un'assenza non e' una
            // preferenza, e chi legge il punteggio deve poterlo distinguere (S117).
            + (Neutral(first.Target.Calibration) is { Length: > 0 } neutral ? ";neutral=" + neutral : string.Empty)
            + ";algorithm=" + algorithm
            + ";candidates=" + route.Ranked.Count.ToString(CultureInfo.InvariantCulture)
            + (route.TieBrokenById && algorithm == route.AlgorithmVersion ? ";tie=id" : string.Empty)
            + (route.Ranked.Count > 1
                ? ";next=" + string.Join(",", others.Take(3).Select(item => item.Target.TargetId + ":" + Number(item.Components.Score)))
                : string.Empty);
    }

    /// <summary>
    /// Una chiamata al modello, con gli eventi gia' raccolti e il terminale verificato.
    ///
    /// Il buffer e lo screening finale restano quelli di sempre: il verdetto del turno si prende
    /// sulla risposta completa. <paramref name="streaming"/> si aggiunge come secondo osservatore
    /// dei frammenti e decide soltanto **che cosa si puo' gia' mostrare**, secondo la regola di
    /// rilascio di 03-specs/PRIVACY.md. Puo' quindi solo mostrare meno, mai di piu'.
    /// </summary>
    private static async Task<ModelReply> CallModelAsync(
        IExecutionAdapter adapter,
        AuthorizedExecution execution,
        int maxOutputBytes,
        StreamingScreener? streaming,
        ITurnObserver observer,
        CancellationToken callerToken,
        CancellationToken deadlineToken)
    {
        OutputBuffer buffer = new(maxOutputBytes);
        UsageReport? usage = null;
        AdapterFailed? failure = null;
        List<ToolCall> calls = [];
        bool terminal = false;
        bool artifactChanged = false;

        try
        {
            await foreach (AdapterEvent adapterEvent in adapter.ExecuteAsync(execution, deadlineToken).ConfigureAwait(false))
            {
                switch (adapterEvent)
                {
                    case AdapterOutputChunk chunk:
                        buffer.Append(chunk.Text);

                        // Il servizio sta lavorando: il limite di inattivita' riparte.
                        Inactivity.Value?.Touch();

                        // Il frammento non raggiunge mai il chiamante cosi' com'e': entra nel
                        // rilascio a blocchi, che restituisce testo solo quando l'intero accumulato
                        // e' stato riesaminato ed e' pulito, a righe intere e con la coda trattenuta.
                        if (streaming?.Append(chunk.Text) is string released)
                        {
                            observer.Observe(new TurnBlockReleased(released));
                        }

                        break;

                    case AdapterUsage reported:
                        usage = reported.Usage;
                        break;

                    case AdapterArtifactChanged changed:
                        artifactChanged = true;

                        // Il percorso arriva dall'adapter e fino alla S60 veniva buttato: il
                        // coordinatore lo schiacciava in un booleano, e chi guardava il turno non
                        // poteva sapere **quali** file l'agente avesse toccato. E' un effetto
                        // dichiarato dal programma, non osservato dal router: chi lo mostra deve
                        // dirlo.
                        observer.Observe(new TurnArtifactChanged(changed.RelativePath));
                        break;

                    case AdapterToolCalls requested:
                        calls.AddRange(requested.Calls);
                        break;

                    case AdapterCompleted:
                        if (terminal)
                        {
                            // Due terminali logici sono un errore di protocollo, non un successo.
                            failure = new AdapterFailed(ErrorCode.ProtocolError, "The adapter reported more than one terminal event.");
                        }

                        terminal = true;
                        break;

                    case AdapterFailed failed:
                        failure ??= failed;
                        terminal = true;
                        break;

                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            failure = new AdapterFailed(callerToken.IsCancellationRequested ? ErrorCode.Cancelled : ErrorCode.ProviderTimeout,
                "The target invocation was interrupted.");
            terminal = true;
        }
        catch (PaRouterException ex)
        {
            failure = new AdapterFailed(ex.Code, ex.Message);
            terminal = true;
        }

        if (!terminal)
        {
            // Lo stream si e' chiuso senza terminale valido: non e' un completed.
            failure = new AdapterFailed(ErrorCode.ProtocolError, "The adapter closed without a terminal event.");
        }

        return new ModelReply(buffer, usage, failure, calls, artifactChanged);
    }

    /// <summary>
    /// Controllo di una chiamata proposta: strumento offerto, argomenti leggibili, classe degli argomenti ammessa
    /// dalla destinazione e dal profilo.
    /// </summary>
    private async Task<(PreparedCall? Call, ToolStop? Stop)> CheckCallAsync(
        ExecutionRequestContext context,
        string executionId,
        string attemptId,
        ToolSession tools,
        ToolCall call,
        int round,
        DataClass conversationClass,
        CancellationToken callerToken)
    {
        if (tools.Gateway.DestinationOf(call.Name) is not ToolDestination destination)
        {
            // Un nome inventato dal modello non corrisponde a nulla da eseguire: gli si risponde con un errore,
            // come prevedono i fornitori, e la conversazione continua senza che nulla parta.
            await AuditAsync(
                executionId,
                attemptId,
                AuditCodes.ToolCallDenied,
                callerToken,
                // Il nome inventato non entra nell'audit: e' testo del modello, e puo' contenere dati della conversazione.
                ("round", AuditMetadata.Count(round)),
                ("reason", "unknown_tool")).ConfigureAwait(false);

            return (new PreparedCall(call, null, "This tool is not available."), null);
        }

        if (!ToolCall.IsJsonObject(call.ArgumentsJson))
        {
            await AuditAsync(
                executionId,
                attemptId,
                AuditCodes.ToolCallDenied,
                callerToken,
                ("tool", call.Name),
                ("toolSource", destination.SourceId),
                ("round", AuditMetadata.Count(round)),
                ("reason", "arguments_malformed")).ConfigureAwait(false);

            return (new PreparedCall(call, destination, "The arguments must be a JSON object that follows the tool schema."), null);
        }

        // Gli argomenti li ha scritti il modello con cio' che ha letto: valgono almeno il floor del profilo, come
        // ogni testo che nasce dalla richiesta.
        SurfaceAnalysis arguments = privacy.AnalyzeSurface(call.ArgumentsJson, context.Policy.MinimumDataClass, redactContacts: false);
        // Gli argomenti possono riportare cio' che il modello ha letto finora, anche senza una forma riconoscibile: valgono
        // almeno la classe della conversazione.
        DataClass argumentClass = DataClasses.Max(arguments.EffectiveClass, conversationClass);

        if (ToolPolicy.ArgumentsDenial(context.Policy, destination, argumentClass, clock.UtcNow) is string reason)
        {
            await AuditAsync(
                executionId,
                attemptId,
                AuditCodes.ToolCallDenied,
                callerToken,
                ("tool", call.Name),
                ("toolSource", destination.SourceId),
                ("round", AuditMetadata.Count(round)),
                ("dataClass", argumentClass.ToWire()),
                ("reason", reason)).ConfigureAwait(false);

            return (null, new ToolStop(
                ExecutionState.Denied,
                ErrorCode.ToolArgumentsDenied,
                Detail(
                    ("tool", call.Name),
                    ("toolSource", destination.SourceId),
                    ("dataClass", argumentClass.ToWire()),
                    ("reason", reason))));
        }

        return (new PreparedCall(call, destination, null), null);
    }

    /// <summary>Esegue una chiamata autorizzata entro il suo tempo; una chiamata rifiutata risponde da sola.</summary>
    private static async Task<(PreparedCall Call, ToolInvocation Invocation)> InvokeAsync(
        ToolSession tools,
        PreparedCall prepared,
        CancellationToken deadlineToken)
    {
        if (prepared.Rejection is string rejection)
        {
            return (prepared, new ToolInvocation(rejection, IsError: true));
        }

        using CancellationTokenSource callToken = CancellationTokenSource.CreateLinkedTokenSource(deadlineToken);
        callToken.CancelAfter(TimeSpan.FromSeconds(tools.Limits.CallTimeoutSeconds));

        try
        {
            return (prepared, await tools.Gateway.InvokeAsync(prepared.Call, callToken.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!deadlineToken.IsCancellationRequested)
        {
            return (prepared, new ToolInvocation(string.Empty, IsError: true, ToolInvocationFailure.Timeout));
        }
        catch (PaRouterException)
        {
            // Il gateway dichiara i guasti invece di lanciare; un'eccezione tipizzata vale come sorgente indisponibile.
            return (prepared, new ToolInvocation(string.Empty, IsError: true, ToolInvocationFailure.SourceUnavailable));
        }
    }

    /// <summary>Primo limite del giro superato, null se nessuno.</summary>
    private static ToolStop? LimitStop(ToolLimits limits, int roundsSoFar, int calls, int totalCalls) =>
        roundsSoFar >= limits.MaxRounds ? Limit("rounds")
        : calls > limits.MaxCallsPerRound ? Limit("calls_per_round")
        : totalCalls + calls > limits.MaxCalls ? Limit("calls")
        : null;

    private static ToolStop Limit(string limit, string? tool = null) =>
        new(
            ExecutionState.Failed,
            ErrorCode.ToolLimit,
            tool is null ? Detail(("limit", limit)) : Detail(("limit", limit), ("tool", tool)));

    /// <summary>
    /// Digest della conversazione che parte al giro successivo: il payload esaminato e tutti i giri, in forma
    /// canonica. Il grant del giro e' legato a questo valore.
    /// </summary>
    private string ConversationDigest(string payloadDigest, IReadOnlyList<ToolRound> rounds) =>
        digest.Compute(JsonSerializer.SerializeToUtf8Bytes(new object[]
        {
            payloadDigest,
            rounds.Select(round => new object?[]
            {
                round.AssistantText,
                round.Calls.Select(call => new[] { call.CallId, call.Name, call.ArgumentsJson }).ToArray(),
                round.Results.Select(result => new object[] { result.CallId, result.Name, result.Content, result.IsError }).ToArray(),
            }).ToArray(),
        }));

    /// <summary>
    /// La sessione propria di un agente, quando l'operatore l'ha scelta per quel servizio (ADR-022).
    ///
    /// Si **prosegue** solo se l'ultimo turno conservato lo ha fatto questo stesso servizio e aveva una sessione
    /// nativa: allora l'agente ha gia' letto tutto cio' che precede. In ogni altro caso — primo turno, servizio
    /// cambiato, turno precedente senza sessione — se ne **apre una nuova** con un identificatore che assegna il
    /// router, e l'agente riceve il contesto impacchettato come vuole S15. Senza cronologia conservata non c'e'
    /// modo di sapere chi abbia fatto il turno prima: niente sessione nativa, e l'agente non conserva nulla.
    /// </summary>
    private static NativeSession? NativeSessionFor(
        ExecutionRequestContext context,
        AdmissibleTarget target,
        TargetDispatchInfo dispatchInfo)
    {
        if (!dispatchInfo.NativeSession || target.Kind is not TargetKind.Agent || context.NativeHistory is not NativeHistory history)
        {
            return null;
        }

        return history.LastNativeSessionId is string previous
            && string.Equals(history.LastServiceId, target.TargetId, StringComparison.Ordinal)
            ? new NativeSession(previous, Resume: true)
            : new NativeSession(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), Resume: false);
    }

    private static AuthorizedExecution Authorize(
        ExecutionGrant grant,
        AdmissibleTarget target,
        SafePayload payload,
        ExecutionRequestContext context,
        TargetDispatchInfo dispatchInfo,
        IReadOnlyList<ToolRound> rounds) =>
        AuthorizedExecutionFactory.Create(
            grant,
            target,
            payload,
            new ExecutionLimits(context.Policy.TimeoutSeconds, context.Policy.MaxOutputBytes, context.Policy.MaxAttempts),
            dispatchInfo.Model,
            dispatchInfo.Endpoint,
            dispatchInfo.Executable,
            target.Kind is TargetKind.Agent ? context.Workspace : null,
            dispatchInfo.SecretRef,
            dispatchInfo.Arguments,
            GenerationLimits.OutputTokens(context.Policy.MaxOutputBytes,
                context.Candidates.First(candidate => candidate.TargetId == target.TargetId).MaxOutputTokens),
            rounds) with
        {
            PromptCaching = dispatchInfo.PromptCaching,
            Thinking = dispatchInfo.Thinking,
            KeepAliveMinutes = dispatchInfo.KeepAliveMinutes,
            ContextWindowTokens = WorkingWindow(
                context.Candidates.FirstOrDefault(candidate => string.Equals(candidate.TargetId, target.TargetId, StringComparison.Ordinal))?.ContextWindowTokens ?? 0,
                context.Band?.MinContextTokens ?? 0,
                ConversationMeasure.InputTokens(payload, rounds, context.EstimatedTokens),
                GenerationLimits.OutputTokens(context.Policy.MaxOutputBytes,
                    context.Candidates.First(candidate => candidate.TargetId == target.TargetId).MaxOutputTokens)),
        };

    /// <summary>
    /// Il limite di tempo del turno in corso, che ogni pezzo di risposta fa ripartire. Viaggia con il flusso asincrono del
    /// turno: turni concorrenti hanno ciascuno il proprio.
    /// </summary>
    private static readonly AsyncLocal<InactivityDeadline?> Inactivity = new();

    /// <summary>Scadenza che si sposta in avanti a ogni segno di vita del servizio.</summary>
    private sealed class InactivityDeadline(CancellationTokenSource source, TimeSpan window)
    {
        public void Touch()
        {
            try
            {
                if (!source.IsCancellationRequested) { source.CancelAfter(window); }
            }
            catch (ObjectDisposedException)
            {
                // Il turno e' gia' finito: non c'e' piu' nulla da far ripartire.
            }
        }
    }

    /// <summary>
    /// Perche' una risposta non e' comparsa, come coppie senza contenuti: il motivo e i tipi di dato trovati. Mai il testo.
    /// </summary>
    private static string Blocked(ScreenedOutput screened) =>
        "bloccata;reason=" + screened.Reason switch
        {
            OutputBlockReason.Credential => "credential",
            OutputBlockReason.Size => "size",
            OutputBlockReason.ScanIncomplete => "scan_incomplete",
            _ => "other",
        }
        + (screened.Report.Findings.Count == 0
            ? string.Empty
            : ";types=" + string.Join(',', screened.Report.Findings.Select(finding => finding.Type.ToWire()).Distinct(StringComparer.Ordinal)));

    /// <summary>Margine per la risposta quando il servizio non dichiara un tetto in token.</summary>
    public const int DefaultOutputReserve = 2048;

    /// <summary>
    /// Il gradino piu' piccolo della finestra di lavoro. Era 4096: una conversazione con due turni e un ricordo lo supera, e
    /// il passaggio a 8192 ricaricava il modello a meta' conversazione (sei secondi per llama3.2:3b, 23 settembre 2026).
    /// </summary>
    public const int SmallestWindow = 8192;

    /// <summary>
    /// La finestra da preparare per questo turno (23 settembre 2026). La finestra **dichiarata** dice quanto il modello
    /// regge e decide l'ammissione; quella di lavoro e' quanto serve adesso: il minimo della difficolta', oppure testo piu'
    /// margine per la risposta se e' di piu', arrotondato al gradino (4096, 8192, 16384…) e mai oltre la dichiarata.
    ///
    /// Prima si mandava la dichiarata: qwen3:4b dichiara 256.000 token, e Ollama senza GPU, chiamato a preparare la memoria
    /// per quella finestra, falliva con «out-of-memory» dopo sette secondi a ogni turno — e il router ripiegava su un altro
    /// modello. I gradini tengono la stessa finestra fra turni simili: una finestra diversa fa ricaricare il modello.
    /// </summary>
    public static int WorkingWindow(int declared, int minimum, int payloadTokens, int outputReserve)
    {
        if (declared <= 0) { return 0; }

        long needed = Math.Max(minimum, (long)Math.Max(0, payloadTokens) + Math.Max(0, outputReserve));
        long window = SmallestWindow;

        while (window < needed && window < declared) { window *= 2; }

        return (int)Math.Min(window, declared);
    }

    private static UsageReport? NormalizeUsage(UsageReport? report) => report is null ? null : report with
    {
        InputTokens = report.InputTokens is >= 0 ? report.InputTokens : null,
        OutputTokens = report.OutputTokens is >= 0 ? report.OutputTokens : null,
        BilledCost = report.BilledCost is >= 0 ? report.BilledCost : null,
        LoadMilliseconds = report.LoadMilliseconds is >= 0 ? report.LoadMilliseconds : null,
        ReasoningCharacters = report.ReasoningCharacters is >= 0 ? report.ReasoningCharacters : null,
    };

    private static UsageReport? AddUsage(UsageReport? total, UsageReport? next, bool firstCall)
    {
        if (firstCall) { return next; }
        if (total is null && next is null) { return null; }

        // An absent report is a dispatched call with unknown usage, not the initial accumulator.
        // Once one summand is unknown, later observations cannot turn the total into a measurement.
        bool sameCurrency = !string.IsNullOrWhiteSpace(total?.Currency)
            && string.Equals(total.Currency, next?.Currency, StringComparison.Ordinal);
        decimal? priced = sameCurrency ? SumCost(total!.PricedCost, next?.PricedCost) : null;
        return new UsageReport(
            Sum(total?.InputTokens, next?.InputTokens),
            Sum(total?.OutputTokens, next?.OutputTokens),
            sameCurrency ? SumCost(total?.BilledCost, next?.BilledCost) : null)
        {
            LoadMilliseconds = Sum(total?.LoadMilliseconds, next?.LoadMilliseconds),
            ReasoningCharacters = Sum(total?.ReasoningCharacters, next?.ReasoningCharacters),
            PricedCost = priced,
            Currency = priced is null ? null : total!.Currency,
            Truncated = (total?.Truncated ?? false) || (next?.Truncated ?? false),
        };
    }

    private static long? Sum(long? first, long? second) =>
        first is >= 0 && second is >= 0 && first.Value <= long.MaxValue - second.Value
            ? first.Value + second.Value : null;

    private static decimal? SumCost(decimal? first, decimal? second)
    {
        if (first is not >= 0 || second is not >= 0) { return null; }
        try { return checked(first.Value + second.Value); }
        catch (OverflowException) { return null; }
    }

    /// <summary>One lifetime for all actual model calls, including fallback attempts; no reservation accounting.</summary>
    private sealed class ObservedUsage
    {
        private int calls;
        internal UsageReport? Value { get; private set; }
        internal UsageReport? Add(UsageReport? report) => Value = AddUsage(Value, report, firstCall: calls++ == 0);
    }

    private static IReadOnlyDictionary<string, string> Detail(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>Esito di una chiamata al modello, prima dello screening.</summary>
    private sealed record ModelReply(
        OutputBuffer Buffer,
        UsageReport? Usage,
        AdapterFailed? Failure,
        IReadOnlyList<ToolCall> Calls,
        bool ArtifactChanged);

    /// <summary>Arresto deliberato del giro degli strumenti, con il riscontro senza contenuti.</summary>
    private sealed record ToolStop(ExecutionState State, ErrorCode Code, IReadOnlyDictionary<string, string> Detail);

    /// <summary>Chiamata controllata: con la destinazione se ammessa, con il messaggio d'errore se rifiutata.</summary>
    private sealed record PreparedCall(ToolCall Call, ToolDestination? Destination, string? Rejection);

    private async Task<ExecutionResult> DenyAsync(
        string executionId,
        PolicyExplain explain,
        ExecutionRequestContext context,
        DataClass dataClass,
        CancellationToken cancellationToken)
    {
        foreach (PolicyDecision decision in explain.Decisions.Where(decision => !decision.Allowed))
        {
            // Un diniego per motivo, non per esecuzione: senza il motivo il conteggio direbbe solo
            // che qualcosa e' stato rifiutato.
            RouterTelemetry.RecordDenial(decision.Reasons.Count == 0 ? "unspecified" : decision.Reasons[0].ToWire());
        }

        await store.CompleteAsync(
            new ExecutionOutcome(
                executionId,
                // Nessun tentativo esiste: un deny si ferma prima di crearne uno.
                null,
                ExecutionState.Denied,
                AttemptState.Failed,
                SideEffectState.None,
                ErrorCode.NoAdmissibleTarget,
                null,
                ContentAvailability.Unavailable,
                [],
                ids.NewId(),
                // **Perche'** e' stato negato, non solo **che** e' stato negato (S83). Fino alla S82
                // queste righe non avevano dove stare: le decisioni si scrivevano solo in T2,
                // insieme al tentativo, e un turno negato non ne crea nessuno. Il ranking non e'
                // avvenuto — nessun candidato era ammesso — quindi il dizionario e' vuoto per
                // costruzione e nessun punteggio viene inventato.
                [.. explain.Decisions.Select(decision => ToStored(decision, EmptyRanking))]),
            cancellationToken).ConfigureAwait(false);

        return new ExecutionResult(
            executionId, ExecutionState.Denied, null, ErrorCode.NoAdmissibleTarget,
            SideEffectState.None, explain.Decisions, null, 0)
        {
            // I numeri che hanno deciso esistono solo qui: la banda della richiesta e i candidati con
            // il loro voto e la loro finestra. Trasformare la decisione in «servizio [CODICI]» e
            // basta — come faceva fino alla S116 — li buttava via, e chi leggeva il rifiuto doveva
            // aprire due file per sapere contro quale soglia era stato misurato.
            Denial = DenialFacts.Build(
                explain,
                context.Candidates,
                context.Band,
                context.Difficulty,
                (context.Policy.Zones, dataClass, clock.UtcNow),
                (context.Policy, context.EstimatedTokens, context.Dispatch.Values.Select(info => info.Pricing?.Currency).FirstOrDefault(value => value is not null))),
        };
    }

    private async Task<ExecutionResult> FailAsync(
        string executionId,
        string? attemptId,
        ExecutionState state,
        ErrorCode code,
        IReadOnlyList<PolicyDecision> decisions,
        CancellationToken cancellationToken,
        SideEffectState sideEffects = SideEffectState.None,
        UsageReport? usage = null,
        int attemptsUsed = 0)
    {
        await store.CompleteAsync(
            new ExecutionOutcome(
                executionId,
                attemptId,
                state,
                state is ExecutionState.Uncertain ? AttemptState.Uncertain : AttemptState.Failed,
                sideEffects,
                code,
                usage is null ? null : JsonSerializer.Serialize(usage),
                ContentAvailability.Unavailable,
                [],
                ids.NewId()),
            cancellationToken).ConfigureAwait(false);

        return new ExecutionResult(executionId, state, null, code, sideEffects, decisions, usage, attemptsUsed);
    }

    /// <summary>
    /// Registra l'evento terminale con la sequenza successiva a quelle gia' scritte. La sequenza
    /// viene calcolata qui perche' i percorsi di errore non conoscono l'ordinale del tentativo, e
    /// due eventi con la stessa sequenza violerebbero la chiave primaria.
    /// </summary>
    private async Task AppendTerminalEventAsync(
        string executionId,
        ExecutionState state,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.AppendNextStateEventAsync(
                executionId,
                state,
                clock.UtcNow.AddHours(24).ToUnixTimeMilliseconds(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (PaRouterException)
        {
            // L'esito e' gia' committato: non riuscire a scrivere l'evento di stato non deve
            // trasformare un turno concluso in un fallimento.
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Evento di avanzamento con la sequenza successiva. Un guasto dello store qui non ferma il turno: l'esito si
    /// scrive comunque, e lo stream salta una fase invece di mentire sulla fine.
    /// </summary>
    private async Task AppendProgressEventAsync(string executionId, ExecutionState state)
    {
        try
        {
            await store.AppendNextStateEventAsync(
                executionId,
                state,
                clock.UtcNow.AddHours(24).ToUnixTimeMilliseconds(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (PaRouterException)
        {
        }
    }

    /// <summary>
    /// Decisione da persistere: solo codici e numeri, mai dettagli sensibili.
    ///
    /// Le componenti del punteggio si scrivono per i soli candidati **ammessi**, ed e' l'unica
    /// forma che rende una decisione ricostruibile: fino a S75 `score_json` era sempre null, quindi
    /// dallo store si sapeva quale servizio avesse vinto e non **perche'**. Il commento di
    /// <c>ScoreComponents</c> lo diceva gia' — «vengono restituite per intero perche' l'explain
    /// deve poter mostrare da dove viene il numero, non solo il numero» — e la persistenza lo
    /// contraddiceva.
    ///
    /// Un candidato negato non ha punteggio: il ranking riceve solo cio' che la policy ha ammesso,
    /// e inventargli un numero direbbe che e' stato confrontato quando non lo e' stato.
    /// </summary>
    /// <summary>
    /// Classifica vuota: su un diniego nessun candidato e' stato ammesso, quindi nessuno e' stato
    /// confrontato. Non e' un caso limite da gestire, e' la condizione normale di quel percorso.
    /// </summary>
    private static readonly Dictionary<string, RankedTarget> EmptyRanking = [];

    private static StoredDecision ToStored(
        PolicyDecision decision,
        IReadOnlyDictionary<string, RankedTarget> ranked) =>
        new(
            decision.TargetId,
            decision.Allowed,
            JsonSerializer.Serialize(decision.Reasons.Select(reason => reason.ToWire())),
            ranked.TryGetValue(decision.TargetId, out RankedTarget? entry)
                ? JsonSerializer.Serialize(new
                {
                    quality = entry.Components.Quality,
                    latency = entry.Components.Latency,
                    cost = entry.Components.Cost,
                    quota = entry.Components.Quota,
                    score = entry.Components.RoundedScore,
                    tier = entry.Tier,
                    position = entry.Position,
                    estimatedCost = entry.EstimatedCost,
                })
                : null);

    /// <summary>
    /// La classe minima che l'analizzatore impone (24 settembre 2026, decisione dell'operatore): se vede un dato dell'art. 9
    /// riferito a una persona, il turno e' almeno sensibile. **Puo' solo restringere**: alza come la classe dichiarata,
    /// non abbassa mai cio' che i rilevatori hanno trovato, e dove un dato sensibile possa andare lo decidono ancora le
    /// zone del profilo. L'elenco di parole non capisce il senso («e' in cura per depressione» gli sfuggiva); il segnale
    /// di un modello locale copre quello che l'elenco non conosce. Senza analizzatore, o se non ha girato, non cambia nulla.
    /// </summary>
    internal static DataClass? AnalyzerFloor(ExecutionRequestContext context) =>
        context.Demand?.Signals is { Sensitive: 3 } ? DataClass.Sensitive : null;

    private static DataClass? DeclaredWithAnalyzer(ExecutionRequestContext context) =>
        (context.DeclaredClass, AnalyzerFloor(context)) switch
        {
            (DataClass declared, DataClass floor) => DataClasses.Max(declared, floor),
            (DataClass declared, null) => declared,
            (null, DataClass floor) => floor,
            _ => null,
        };

    /// <summary>
    /// Da che cosa viene la classe dei dati, per chi guarda il turno: il minimo del profilo, la classe dichiarata,
    /// quali rilevatori hanno girato, che cosa ha trovato ciascuno — **tipo e quanti, mai il testo** — e chi non ha
    /// concluso. La classe resta quella calcolata dalla pipeline: qui si dice solo da dove viene.
    /// </summary>
    private static string ClassReasons(ExecutionRequestContext context, PrivacyAnalysis analysis)
    {
        string? analyzer = AnalyzerFloor(context) is DataClass raised ? "analyzer=" + raised.ToWire() : null;

        List<string> parts =
        [
            "floor=" + context.Policy.MinimumDataClass.ToWire(),
            "detectors=" + analysis.FinalReport.DetectorVersion.Replace(';', ',').Replace('=', '-'),
        ];

        if (context.DeclaredClass is DataClass declared) { parts.Add("declared=" + declared.ToWire()); }
        if (analyzer is not null) { parts.Add(analyzer); }

        // Perche' il dato e' stretto (ADR-029): identificativo diretto, nome con indirizzo, o regola del profilo.
        if (analysis.Grade is string grade) { parts.Add("grade=" + grade); }

        // Ritrovamenti della prima scansione, che ha visto tutto cio' che e' stato esaminato: per rilevatore, tipo e
        // **provenienza** (S117) — un dato scritto tre domande fa riparte con la cronologia, ma non e' nella domanda
        // di adesso. Un «!» dopo il tipo dice che la forma non torna (un codice fiscale scritto male).
        // Forma: versione:tipo[!]@provenienza*quanti.
        string[] found = [.. analysis.FindingOrigins
            .GroupBy(origin => (origin.DetectorVersion, origin.Type, origin.Source, Invalid: origin.FormValid == false))
            .OrderBy(group => group.Key.DetectorVersion, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Source)
            .ThenBy(group => group.Key.Type.ToWire(), StringComparer.Ordinal)
            .Select(group => group.Key.DetectorVersion + ":" + group.Key.Type.ToWire() + (group.Key.Invalid ? "!" : string.Empty)
                + "@" + group.Key.Source.ToWire() + "*" + group.Count().ToString(CultureInfo.InvariantCulture))];

        if (found.Length > 0) { parts.Add("found=" + string.Join(',', found)); }

        string[] incomplete = [.. analysis.InitialReport.IncompleteDetectors.Concat(analysis.FinalReport.IncompleteDetectors)
            .Distinct(StringComparer.Ordinal)];

        if (incomplete.Length > 0) { parts.Add("incomplete=" + string.Join(',', incomplete)); }

        // Si conta cio' che parte davvero, cioe' il manifest dopo il taglio al budget. La cronologia ha provenienza
        // «operatore» per le domande: contarla fra gli allegati faceva di ogni turno precedente un allegato.
        IReadOnlyList<ContextManifestEntry> sent = analysis.Payload.Manifest;
        int memory = sent.Count(entry => entry.Provenance is Provenance.Memory);
        int attached = sent.Count(entry => !HistoryItems.Is(entry.ItemId) && entry.Provenance is Provenance.Workspace or Provenance.Operator);
        int history = HistoryItems.Turns(sent.Select(entry => entry.ItemId));

        if (memory > 0) { parts.Add("memoryItems=" + memory.ToString(CultureInfo.InvariantCulture)); }
        if (attached > 0) { parts.Add("attachedItems=" + attached.ToString(CultureInfo.InvariantCulture)); }

        parts.Add("historyTurns=" + history.ToString(CultureInfo.InvariantCulture));

        return string.Join(';', parts);
    }
}
