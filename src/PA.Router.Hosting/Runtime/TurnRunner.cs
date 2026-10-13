using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Core;
using PA.Router.Execution;
using PA.Router.Extensions;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Storage;
using PA.Router.Memory;
using PA.Router.Policy;
using PA.Router.Privacy;
using PA.Router.Registry.Loading;
using PA.Router.Routing;
using PA.Router.Session;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Richiesta di un turno, gia' risolta dal chiamante. Il canale non compare: viene dal profilo, e
/// il profilo dalla configurazione posseduta da chi esegue. Un client non lo dichiara.
/// </summary>
/// <param name="Profile">Profilo di policy da applicare.</param>
/// <param name="Input">Testo dell'operatore.</param>
/// <param name="SessionId">Sessione esistente da continuare, null per una nuova.</param>
/// <param name="RequestedServiceId">Servizio richiesto esplicitamente; e' un vincolo in piu'.</param>
/// <param name="SkillId">Skill scelta dall'operatore, che entra nel payload come testo.</param>
/// <param name="ContextItems">Contenuti forniti dal chiamante; restano dati non fidati.</param>
/// <param name="IdempotencyKey">Chiave del chiamante, quando il suo protocollo ne prevede una.</param>
/// <param name="RequestDigest">
/// Digest della richiesta intera a cui la chiave e' legata (S74). Viaggia con la chiave perche' la
/// riga di idempotenza deve poter distinguere due richieste con lo stesso testo e istruzioni
/// diverse, anche dopo un riavvio.
/// </param>
/// <param name="ExecutionId">Identificatore gia' assegnato, quando il chiamante lo ha promesso.</param>
/// <param name="Instructions">
/// Istruzioni dell'applicazione esterna (ADR-020). Vanno al modello nel ruolo di sistema, passano dalla
/// privacy e non sono policy. Solo l'host API le porta.
/// </param>
/// <param name="Tools">
/// Strumenti dell'applicazione gia' preparati (ADR-020). I limiti li decide il turno, dalla configurazione e dal
/// profilo: quelli portati dalla richiesta non contano.
/// </param>
public sealed record TurnRequest(
    PolicyDocument Profile,
    string Input,
    string? SessionId = null,
    string? RequestedServiceId = null,
    string? SkillId = null,
    IReadOnlySet<Capability>? RequestedCapabilities = null,
    WorkspaceDescriptor? Workspace = null,
    IReadOnlyList<ContextItem>? ContextItems = null,
    string? IdempotencyKey = null,
    string? RequestDigest = null,
    string? ExecutionId = null,
    string? Instructions = null,
    ToolSession? Tools = null,
    ITurnObserver? Observer = null)
{
    /// <summary>
    /// Conferma dell'operatore, data nel terminale per questo turno, all'uscita dal perimetro che il profilo sottopone a
    /// conferma (ADR-029, decisione I). Il canale di servizio non la porta: un turno che la porta li' non parte.
    /// </summary>
    public bool ExternalConfirmed { get; init; }

    /// <summary>
    /// Canale del front end che ha ricevuto la richiesta (ADR-032, fase 2): <c>chatbot</c> per l'host API e l'SDK di
    /// servizio, <c>personal_cli</c> per terminale, REPL, worker e script. Prima lo dichiarava il profilo; ora lo dice chi
    /// riceve la richiesta, e una sessione gia' esistente deve essere dello stesso canale.
    /// </summary>
    public Channel Channel { get; init; } = Channel.PersonalCli;
}

/// <summary>Esito di un turno.</summary>
/// <param name="SessionId">Sessione usata, creata se non esisteva.</param>
/// <param name="Result">Risultato dell'esecuzione.</param>
/// <param name="Memory">Esito dell'auto-injection della memoria.</param>
/// <param name="SkillMissing">Vero se la skill richiesta non e' disponibile: il turno non parte.</param>
/// <param name="OmittedMemories">Ricordi lasciati fuori dal budget del contesto.</param>
/// <param name="OmittedTurns">Turni piu' vecchi lasciati fuori dal budget del contesto.</param>
public sealed record TurnOutcome(
    string SessionId,
    ExecutionResult? Result,
    MemoryAugmentationResult Memory,
    bool SkillMissing = false,
    int OmittedMemories = 0,
    int OmittedTurns = 0);

/// <summary>
/// Esecuzione di un turno sopra una <see cref="RouterStack"/>.
///
/// E' il percorso unico: la CLI, il worker e l'host API eseguono lo stesso ordine — memoria,
/// cronologia, skill, privacy, policy, ranking, dispatch, screening — e nessuno dei tre ne tiene
/// una copia propria. Cio' che cambia fra loro e' dove gira, non che cosa succede.
/// </summary>
public static class TurnRunner
{
    /// <summary>
    /// Messaggio del rifiuto <c>session_profile_mismatch</c>: la resa lo traduce. Dice che cosa fare, perche' una sessione
    /// nata sotto una modalita' poi rinominata o tolta non si continua (24 settembre 2026).
    /// </summary>
    public const string SessionProfileMismatch =
        "This conversation was started under another mode and cannot continue under this one: start a new conversation, or switch back to the mode it was started with.";

    /// <summary>
    /// Turni di cronologia riportati nel contesto. Il budget completo lo impone la policy con
    /// <c>maxContextBytes</c>; questo e' il tetto sul numero, per non far crescere il payload a
    /// ogni turno.
    /// </summary>
    public const int HistoryTurns = LimitsConfig.DefaultHistoryTurns;

    /// <summary>Limiti del giro degli strumenti: quelli della configurazione, ristretti dal profilo.</summary>
    public static ToolLimits ToolLimitsFor(ConfigurationSnapshot snapshot, CompiledPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(policy);

        return (snapshot.Config.Tools?.Limits ?? ToolLimits.Default).Restrict(policy.ToolLimits);
    }

    /// <summary>Esegue un turno completo e restituisce l'esito.</summary>
    /// <summary>L'attesa delle sonde, dichiarata come fase: partono all'inizio del turno, e qui si vede se il turno le aspetta.</summary>
    private static async Task<IReadOnlyDictionary<string, ProbeResult>> WaitedAsync(
        Task<IReadOnlyDictionary<string, ProbeResult>> probing,
        ITurnObserver? observer)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        IReadOnlyDictionary<string, ProbeResult> probes = await probing.ConfigureAwait(false);
        observer?.Observe(new TurnPhaseEnded("Sonde", "services=" + probes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Diagnostics.Stopwatch.GetElapsedTime(start)));
        return probes;
    }

    public static async Task<TurnOutcome> RunAsync(
        RouterStack stack,
        TurnRequest request,
        CancellationToken cancellationToken,
        SchedulerLease? admission = null)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(request);

        long turnStart = System.Diagnostics.Stopwatch.GetTimestamp();

        // La vista si prende una volta, con i file che ci sono adesso sul disco: tutto il turno usa
        // la stessa configurazione. Se i file non sono validi il turno non parte.
        StackView view = stack.EnsureCurrent();
        ConfigurationSnapshot snapshot = view.Snapshot;

        // Il profilo si rilegge per identificatore dalla configurazione corrente. Quello ricevuto
        // puo' venire da un chiamante che lo ha risolto prima di una rilettura: se nel frattempo
        // l'operatore lo ha ristretto, vale la versione ristretta.
        PolicyDocument currentProfile = TurnInputs.ResolveProfile(snapshot, request.Profile.ProfileId);

        // I limiti di esecuzione vengono dalla sola configurazione e il canale dal front end (ADR-032, fase 2): la
        // compilazione li mette insieme in un punto solo.
        CompiledPolicy policy = PolicyCompiler.Compile(currentProfile, snapshot.Config, request.Channel);

        // L'host API non raggiunge mai un agente (INV-23). I front end del canale di servizio non
        // dichiarano capacita' ne' workspace; se una richiesta li portasse comunque, non parte.
        if (policy.Channel is Channel.Chatbot
            && (request.RequestedCapabilities is { Count: > 0 } || request.Workspace is not null))
        {
            throw new PaRouterException(ErrorCode.PolicyInvalid, "Agents are not available to the service channel.")
                .WithMetadata("reason", "agents_not_available_to_api");
        }

        // Nel canale di servizio non c'e' nessuno a cui chiedere: si fallisce chiusi (ADR-029, decisione I; INV-09).
        if (policy.Channel is Channel.Chatbot && request.ExternalConfirmed)
        {
            throw new PaRouterException(ErrorCode.PolicyInvalid, "The service channel cannot confirm sending data outside the perimeter.")
                .WithMetadata("reason", "confirmation_not_available_to_api");
        }

        if ((request.Instructions is not null || request.Tools is not null)
            && (request.RequestedCapabilities is { Count: > 0 } || request.Workspace is not null))
        {
            // Le istruzioni di un'applicazione vanno a un modello, mai a un agente: un programma agentico
            // non ha un ruolo di sistema separato dalla richiesta dell'operatore.
            throw new PaRouterException(ErrorCode.PolicyInvalid, "Application instructions cannot reach an agent.")
                .WithMetadata("reason", "instructions_not_for_agents");
        }

        PrivacyLimits limits = PrivacyLimits.Default with
        {
            MaxInputBytes = policy.MaxInputBytes,
            MaxContextBytes = policy.MaxContextBytes,
            MaxOutputBytes = policy.MaxOutputBytes,
        };

        // Sonde e analizzatore partono subito, in parallelo con la lettura della sessione e la ricerca in memoria
        // (24 settembre 2026): nessuno dei due dipende da quei passi, e in fila costavano secondi a ogni turno.
        Task<IReadOnlyDictionary<string, ProbeResult>> probing =
            AdapterRegistry.ObserveAsync(snapshot, cancellationToken, AdapterRegistry.TurnProbeReuse);
        Task<(AnalyzerSignals? Signals, string? Unavailable)>? analyzing =
            RequestAnalyzerModel.Start(snapshot, policy, request.Input, request.ContextItems?.Count ?? 0, cancellationToken);

        string session = request.SessionId ?? Guid.NewGuid().ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        SqliteSessionService sessions = stack.Sessions();

        // Una sessione indicata dall'operatore continua quella di prima: i turni conservati
        // tornano nel contesto. Senza conservazione non c'e' nulla da riprendere, e il turno
        // riparte dichiarando che il contesto non e' disponibile.
        IReadOnlyList<SessionTurn> history = request.SessionId is null
            ? []
            : await sessions.ReadTurnsAsync(session, cancellationToken).ConfigureAwait(false);

        await stack.Store.EnsureProjectAsync(
            snapshot.Config.Api.ProjectId, "local", cancellationToken).ConfigureAwait(false);

        // La sessione si crea una volta sola. Dedurlo dalla cronologia vuota sarebbe sbagliato in
        // memory_only, dove i turni non vengono conservati e la sessione esisterebbe comunque: il
        // secondo turno tenterebbe di ricrearla e la transazione fallirebbe.
        SessionSummary? existing = request.SessionId is null
            ? null
            : await sessions.ReadAsync(session, cancellationToken).ConfigureAwait(false);

        if (existing is not null
            && !string.Equals(existing.ProfileId, request.Profile.ProfileId, StringComparison.Ordinal))
        {
            // Una sessione appartiene al profilo con cui e' nata: continuarla sotto un altro
            // profilo cambierebbe le autorizzazioni a meta' conversazione. Nessuna eccezione per
            // nome (24 settembre 2026): una sessione nata sotto un profilo poi rinominato o tolto
            // non si continua, e il messaggio dice che cosa fare.
            throw new PaRouterException(ErrorCode.PolicyInvalid, SessionProfileMismatch)
                .WithMetadata("reason", "session_profile_mismatch");
        }

        if (existing is not null && existing.Channel != policy.Channel)
        {
            // E anche al canale da cui e' nata (ADR-032, fase 2): una sessione di un'applicazione ripresa dal terminale
            // cambierebbe l'ambito della memoria, e una del terminale ripresa dall'API non deve esistere.
            throw new PaRouterException(ErrorCode.PolicyInvalid, "That session belongs to another channel.")
                .WithMetadata("reason", "session_channel_mismatch");
        }

        if (existing is null)
        {
            await stack.Store.CreateSessionAsync(
                new SessionRecord(
                    session,
                    request.Profile.ProfileId,
                    policy.Channel,
                    snapshot.Config.ContentMode,
                    snapshot.Config.Api.ProjectId,
                    null,
                    SystemClock.Instance.UtcNow.AddHours(1).ToUnixTimeMilliseconds()),
                cancellationToken).ConfigureAwait(false);
        }

        MemoryScope scope = TurnMemory.ScopeFor(snapshot, policy, session);

        // La memoria durevole richiede la conservazione dei contenuti: in memory_only non esiste
        // un posto dove scrivere un ricordo, e prometterlo sarebbe falso.
        bool memoryPersisted = snapshot.Config.ContentMode is ContentMode.Encrypted;

        using MemoryStack? memory = snapshot.Config.Memory.Enabled
            ? MemoryStack.Create(stack, snapshot, policy, scope)
            : null;

        // I ricordi entrano nel payload prima della privacy e della policy: da qui in avanti sono
        // dati come gli altri, non istruzioni privilegiate.
        // I turni che stanno per entrare come cronologia: un ricordo nato da uno di loro sarebbe lo
        // stesso testo due volte, con due etichette diverse.
        IReadOnlySet<string> inHistory = new HashSet<string>(
            history.TakeLast(snapshot.Config.Limits.HistoryTurns).Select(turn => turn.ExecutionId),
            StringComparer.Ordinal);

        // Un saluto, un grazie, un «ok» (il pre-check deterministico di ADR-025, senza modelli) non chiedono niente al
        // passato: il 23 settembre 2026 un «ciao» si e' portato dietro quattro ricordi con due IBAN, e' diventato un dato
        // sensibile e il modello ha parlato di «numeri della conversazione precedente». La cronologia resta: la
        // conversazione continua; e' la memoria di altre conversazioni a restare fuori.
        bool trivial = PA.Router.Routing.Demand.DemandProfiler.PreCheck(request.Input, 0, request.ContextItems?.Count ?? 0).Outcome
            is PreCheckOutcome.Trivial;

        // Quanto costa preparare il turno — configurazione, sessione, archivio — prima di qualunque ricerca (24 settembre
        // 2026): al primo turno erano secondi senza nome nel «router ha lavorato».
        request.Observer?.Observe(new TurnPhaseEnded("Avvio", "ok", System.Diagnostics.Stopwatch.GetElapsedTime(turnStart)));

        long memoryStart = System.Diagnostics.Stopwatch.GetTimestamp();

        MemoryAugmentationResult augmentation = memory is null
            ? MemoryAugmentationResult.Unavailable(MemorySearchMode.LexicalOnly)
            : trivial
                // Pronta, e niente da aggiungere: non e' un guasto e non deve sembrarlo a chi legge lo stato.
                ? new MemoryAugmentationResult([], [], [], 0, MemoryIndexStatus.Ready, MemorySearchMode.LexicalOnly)
                : await new MemoryAugmentation(memory.Search, snapshot.Config.Memory.Required, snapshot.Config.Memory.TopK)
                .AugmentAsync(scope, request.Input, cancellationToken, inHistory).ConfigureAwait(false);

        // Quanto costa cercare nella memoria: senza questa fase i secondi finivano nel «router ha lavorato» senza nome.
        request.Observer?.Observe(new TurnPhaseEnded(
            "Memoria",
            "items=" + augmentation.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + (trivial ? ";skipped=trivial" : string.Empty),
            System.Diagnostics.Stopwatch.GetElapsedTime(memoryStart)));

        List<ContextItem> context = [.. augmentation.Items];

        // I contenuti del chiamante entrano qui, dopo la memoria e prima della cronologia: sono
        // dati come gli altri e riattraversano privacy e policy. Un backend che li fornisce non
        // sta dichiarando una policy, sta consegnando del testo.
        context.AddRange(request.ContextItems ?? []);

        // La cronologia entra come contenuto con la propria provenienza, e riattraversa privacy e
        // policy come tutto il resto: un turno precedente non e' un'istruzione privilegiata.
        foreach (SessionTurn turn in history.TakeLast(snapshot.Config.Limits.HistoryTurns))
        {
            context.Add(new ContextItem(
                HistoryItems.InputId(turn.Ordinal),
                turn.Input,
                Provenance.Operator,
                turn.DeclaredClass));

            if (turn.Output is string previous)
            {
                context.Add(new ContextItem(
                    HistoryItems.OutputId(turn.Ordinal),
                    previous,
                    Provenance.AgentOutput,
                    turn.DeclaredClass));
            }
        }

        if (request.SkillId is string skillId)
        {
            // La skill e' una scelta esplicita dell'operatore: entra nel payload come testo
            // selezionato, distinto dalla memoria, e viene rivalutata da privacy e policy come
            // tutto il resto. Non concede permessi.
            if (ExtensionQueries.LoadForTurn(snapshot, stack, skillId) is not ContextItem skillItem)
            {
                return new TurnOutcome(session, null, augmentation, SkillMissing: true);
            }

            context.Insert(0, skillItem);
        }

        // Il contesto allegato entra nel budget del profilo: si tolgono prima i ricordi, poi i
        // turni piu' vecchi. Input e vincoli non si tagliano mai, e infatti non sono qui dentro.
        TrimmedContext trimmed = TurnContext.Trim(context, policy.MaxContextBytes);

        // L'analisi e' locale, deterministica e versionata: stima la dimensione e la banda, e da
        // queste i requisiti che i servizi devono soddisfare. Il profilo puo' stringerli, mai
        // allentarli (03-specs/ROUTING.md).
        // Le capacita' le chiede l'operatore, non si deducono dal testo: «leggi il repository» non
        // e' un permesso, e indovinare un permesso da una frase sarebbe il modo piu' rapido di
        // concederne uno che nessuno ha voluto. La chat c'e' sempre.
        HashSet<Capability> required = [Capability.Chat, .. request.RequestedCapabilities ?? new HashSet<Capability>()];

        // La difficolta' e' la dimensione, alzata dalla classificazione del testo dell'operatore: una
        // modifica di codice non e' banale anche quando la frase e' corta (ADR-019).
        // Le istruzioni dell'applicazione non si tagliano e occupano la finestra del modello: contano
        // nella dimensione come il contesto, anche se non stanno nel suo budget.
        List<ContextItem> measured = [.. trimmed.Items];

        if (request.Instructions is string instructions)
        {
            measured.Add(new ContextItem("instructions", instructions, Provenance.Workspace, null));
        }

        if (request.Tools is ToolSession offered)
        {
            // Descrizioni e schemi degli strumenti occupano la finestra del modello come le istruzioni.
            measured.Add(new ContextItem("tools", PrivacyPipeline.ToolSurface(offered.Gateway.Definitions), Provenance.Workspace, null));
        }

        RequestClassification rules = RequestClassifier.Classify(request.Input);
        RequestProfile profile = RequestAnalyzer.Analyze(request.Input, measured, required, rules.BandFloor);

        // Da che cosa viene la difficolta', per chi guarda il turno: la dimensione, il minimo delle regole e la
        // modifica di file sono le tre voci che RequestAnalyzer confronta, e vince la piu' alta.
        string difficultyReasons = "tokens=" + profile.InputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ";sizeBand=" + RequestAnalyzer.BandOf(profile.InputTokens).ToWire()
            + ";totalTokens=" + profile.EstimatedTokens.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ";windowTokens=" + RequestAnalyzer.WindowTokens(profile).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ";bandWindow=" + RequestAnalyzer.MinimumWindowTokens.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ";rulesFloor=" + rules.BandFloor.ToWire()
            + ";write=" + (profile.Intent is Intent.CodeChange ? "1" : "0");

        // Il solo requisito e' la finestra (ADR-032, fase 3): nessun voto minimo per difficolta'. Nel modo decisivo
        // (ADR-031) la capacita' la misura il profilo di domanda con i livelli dei servizi; altrimenti il ranking, con la
        // qualita' ricavata dall'ordine dei modelli.
        BandRequirements band = RequestAnalyzer.Requirements(profile);
        bool decisive = RequestAnalyzerModel.Decisive(snapshot);

        // Il profilo di domanda (ADR-025, ADR-026): in ombra si calcola e si racconta accanto alla banda di oggi; nel modo
        // decisivo sceglie. Dell'eventuale contesto passano solo dimensioni e numero, mai il testo.
        DemandOutcome? demand = null;

        if (RequestAnalyzerModel.Enabled(snapshot))
        {
            demand = await RequestAnalyzerModel.ProfileAsync(
                snapshot,
                policy,
                request.Input,
                measured.Sum(item => System.Text.Encoding.UTF8.GetByteCount(item.Text)),
                request.ContextItems?.Count ?? 0,
                cancellationToken,
                analyzing).ConfigureAwait(false);

            request.Observer?.Observe(new TurnPhaseEnded(
                "Profilo di domanda",
                PA.Router.Routing.Demand.DemandProfiler.Describe(demand.Profile, demand.Check, profile.Band.ToWire(), demand.Unavailable)
                    + ";prompt=" + RequestAnalyzerModel.PromptVersion
                    + (decisive ? ";decisive=1" : string.Empty)
                    + ";service=" + snapshot.Config.Routing.Analyzer!.ServiceId,
                demand.Elapsed));
        }

        // Una cartella in cui l'agente scrive e' un repository git: se non lo e' lo diventa, e con
        // modifiche che non vengono da un turno l'agente non parte (ADR-019). Vale dove il turno
        // gira, quindi anche nel worker.
        bool writable = request.Workspace is { Writable: true };

        if (writable)
        {
            await WorkspaceRepository.PrepareForWriteAsync(request.Workspace!.RootPath, cancellationToken).ConfigureAwait(false);
        }

        ExecutionResult result = await view.Coordinator().ExecuteAsync(
            new ExecutionRequestContext(
                session,
                request.Input,
                trimmed.Items,
                policy,
                view.Candidates(await WaitedAsync(probing, request.Observer).ConfigureAwait(false)),
                view.Dispatch(),
                required,
                profile.Intent,
                snapshot.Config.PreferSubscription,
                limits,
                snapshot.PolicyDigest,
                snapshot.RegistryDigest,
                snapshot.Digest,
                RequestedTargetId: request.RequestedServiceId,
                Band: band,
                Difficulty: profile.Band.ToWire(),
                DifficultyReasons: difficultyReasons,
                // I token stimati servono al costo: lo stesso numero per tutti i candidati.
                EstimatedTokens: profile.EstimatedTokens,
                // Chiave ed identificatore arrivano dal protocollo del chiamante, quando ne ha
                // uno: l'API promette un executionId nella risposta 202 e deve poterlo mantenere.
                IdempotencyKey: request.IdempotencyKey,
                RequestDigest: request.RequestDigest,
                PreassignedExecutionId: request.ExecutionId,
                // Il workspace lo dichiara l'operatore con il turno: un agente senza directory
                // autorizzata non parte, e nessun percorso ne inventa una.
                // Due grafie della stessa cartella sono lo stesso recinto: la chiave e' canonica.
                WorkspaceKey: request.Workspace is WorkspaceDescriptor fence
                    ? PA.Router.Execution.ExecutionScheduler.WorkspaceKeyFor(fence.RootPath)
                    : null,
                // Il recinto che arriva all'adapter porta anche i comandi di sistema: la concessione la decide il piano
                // del turno, e senza questo passaggio l'agente girerebbe senza sapere di averli.
                Workspace: request.Workspace is WorkspaceDescriptor granted
                    ? granted with { Shell = required.Contains(Capability.Shell) }
                    : null,
                Admission: admission,
                Instructions: request.Instructions,
                // I limiti del giro sono dell'operatore: configurazione ristretta dal profilo.
                Tools: request.Tools is null ? null : request.Tools with { Limits = ToolLimitsFor(snapshot, policy) },
                // Chi guarda il turno lo dichiara con la richiesta. Senza osservatore il motore non
                // costruisce nemmeno il rilascio a blocchi: un turno che nessuno guarda non paga
                // alcuna scansione in piu' di prima.
                Observer: request.Observer)
            {
                // Un agente prosegue la propria sessione solo se il router sa chi ha fatto il turno prima (ADR-022):
                // senza cronologia conservata non lo sa, e allora l'agente non conserva nulla nemmeno lui.
                NativeHistory = snapshot.Config.ContentMode is ContentMode.Encrypted
                    ? new NativeHistory(history.LastOrDefault()?.ServiceId, history.LastOrDefault()?.NativeSessionId)
                    : null,
                // Il minimo sufficiente (ADR-027): in ombra si racconta, nel modo decisivo sceglie (ADR-031).
                Demand = demand?.Profile,
                DemandDecides = decisive && demand is not null,
                ExternalConfirmed = request.ExternalConfirmed,
                CapabilityLevels = demand is null ? null : ServiceCapabilityLevels.For(snapshot),
            },
            cancellationToken).ConfigureAwait(false);

        if (writable)
        {
            // Istantanea di cio' che l'agente ha lasciato: la scrittura successiva parte da qui, e
            // l'operatore puo' tornare indietro con git. Un errore qui non cambia l'esito del turno.
            try
            {
                await WorkspaceRepository.CheckpointAsync(request.Workspace!.RootPath, cancellationToken).ConfigureAwait(false);
            }
            catch (PaRouterException exception)
            {
                stack.Log.Warning("workspace_checkpoint_failed", ("reason", exception.Code.ToWire()));
            }
        }

        // Ciclo di vita sintetico: stato, codice ed esecuzione per la correlazione locale. Nessun
        // frammento del turno passa di qui.
        stack.Log.Info(
            "execution_" + result.State.ToWire(),
            ("state", result.State.ToWire()),
            ("channel", policy.Channel.ToWire()),
            ("reason", result.ErrorCode?.ToWire()),
            ("execution_id", result.ExecutionId));

        if (result.State is ExecutionState.Completed && result.ExecutionId is string completedId)
        {
            // Il turno entra nella cronologia solo se completato: un turno fallito non e' un fatto
            // della conversazione.
            await sessions.AppendTurnAsync(
                session,
                new SessionTurn(history.Count + 1, request.Input, result.Output, completedId, SystemClock.Instance.UtcNow, result.ConversationClass)
                {
                    ServiceId = result.TargetId,
                    NativeSessionId = result.NativeSessionId,
                },
                SystemClock.Instance.UtcNow.AddDays(snapshot.Config.Memory.RetentionDays).ToUnixTimeMilliseconds(),
                cancellationToken).ConfigureAwait(false);
        }

        // Un caso banale del pre-check — un saluto, un grazie — non diventa un ricordo (23 settembre 2026): «Ciao! Sono felice
        // di rivederti» tornava in ogni conversazione dopo, e un modello piccolo lo ripeteva al posto della risposta.
        if (memory is not null && memoryPersisted && result.State is ExecutionState.Completed && !trivial)
        {
            long recordStart = System.Diagnostics.Stopwatch.GetTimestamp();

            // L'osservatore registra il solo risultato gia' filtrato: il turno e' finito, e cio'
            // che entra in memoria e' cio' che il router ha davvero visto.
            await new MemoryObserver(
                memory.Store,
                view.Scanner(),
                stack.Digest,
                GuidIdGenerator.Instance,
                SystemClock.Instance,
                memory.Store)
                .ObserveAsync(
                    snapshot.Config.Api.ProjectId,
                    session,
                    result,
                    TimeSpan.FromDays(policy.MaxMemoryRetentionDays),
                    cancellationToken,
                    // Il ricordo vale almeno il floor del profilo e la classe della conversazione: una risposta che parafrasa
                    // dati personali senza una forma riconoscibile non diventa pubblica.
                    DataClasses.Max(policy.MinimumDataClass, result.ConversationClass ?? DataClass.Public))
                .ConfigureAwait(false);

            request.Observer?.Observe(new TurnPhaseEnded(
                "Registrazione in memoria", "ok", System.Diagnostics.Stopwatch.GetElapsedTime(recordStart)));
        }

        return new TurnOutcome(session, result, augmentation, SkillMissing: false, trimmed.OmittedMemoryIds.Count, trimmed.OmittedTurns);
    }
}
