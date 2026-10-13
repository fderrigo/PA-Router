using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Core;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Policy;
using PA.Router.Registry.Loading;
using PA.Router.Runtime.Ipc;

namespace PA.Router.Cli.Repl;

/// <summary>
/// Esito di un turno del REPL, nella forma che serve a mostrarlo.
///
/// E' la stessa per il turno eseguito qui e per quello eseguito dal worker: il REPL non deve
/// sapere dove e' girato, altrimenti finirebbe per mostrare due cose diverse a seconda del caso.
/// </summary>
/// <param name="SessionId">Sessione usata: la prima volta viene creata, poi resta.</param>
/// <param name="State">Stato finale dell'esecuzione, in forma wire.</param>
/// <param name="Output">Risposta del provider, quando esiste. **Dato non fidato.**</param>
/// <param name="ErrorCode">Codice di errore, in forma wire, quando lo stato non e' completato.</param>
/// <param name="Reasons">Ragioni della policy, quando il turno e' stato negato.</param>
/// <param name="Denial">Perche' nessun servizio ha preso la richiesta, con i numeri (S116).</param>
/// <param name="MemoryInjected">Ricordi entrati nel contesto.</param>
/// <param name="MemoryBytes">Byte occupati dai ricordi.</param>
internal sealed record ReplTurnResult(
    string SessionId,
    string State,
    string? Output,
    string? ErrorCode,
    IReadOnlyList<string> Reasons,
    int MemoryInjected,
    int MemoryBytes,
    DenialDetail? Denial = null,
    bool WeakPerimeter = false,
    bool BelowDemand = false);

/// <summary>
/// Dove gira il turno del REPL.
///
/// La regola e' quella di <c>pa run</c> e non cambia perche' l'invito e' interattivo: se un worker
/// possiede gia' la directory dati, il turno gira li'; altrimenti lo esegue questo processo, che
/// per tutta la durata del REPL possiede lo store. La scelta si fa **una volta sola**, all'avvio:
/// deciderla a ogni riga significherebbe prendere e rilasciare il lock dello store a ogni domanda.
/// </summary>
internal abstract class ReplChannel : IAsyncDisposable
{
    /// <summary>Descrizione per l'intestazione: dove gira il turno.</summary>
    internal abstract string Location { get; }

    /// <summary>Vero se il canale puo' analizzare localmente senza eseguire.</summary>
    internal abstract bool CanExplain { get; }

    /// <summary>Configurazione in uso, per cio' che il REPL mostra e verifica.</summary>
    internal abstract ConfigurationSnapshot Snapshot { get; }

    /// <summary>
    /// Confronta la configurazione con i file sul disco. E' la stessa regola che applica chi esegue
    /// i turni: il REPL la usa per mostrare cio' che vale adesso e per dire quando i file non sono
    /// validi.
    /// </summary>
    internal abstract ConfigurationRefresh Refresh();

    /// <summary>
    /// Esegue un turno.
    ///
    /// <paramref name="observer"/> riceve fasi, strumenti e blocchi di risposta gia' esaminati
    /// (S58). Vale quando il turno gira in questo processo: il canale del worker non trasporta
    /// eventi, e lo dichiara invece di fingere che li perda per caso.
    /// </summary>
    /// <param name="attachments">
    /// Contenuti allegati dall'operatore con <c>@percorso</c> (S63). Sono **dati**: entrano nel
    /// payload con la loro provenienza e riattraversano privacy e policy come ogni altro testo.
    /// </param>
    internal abstract Task<ReplTurnResult> RunAsync(
        string input,
        string? sessionId,
        string? targetId,
        string? skillId,
        IReadOnlySet<Capability> capabilities,
        WorkspaceDescriptor? workspace,
        CancellationToken cancellationToken,
        ITurnObserver? observer = null,
        IReadOnlyList<ContextItem>? attachments = null,
        bool externalConfirmed = false);

    /// <summary>Ricerca nei ricordi dell'ambito della sessione.</summary>
    internal abstract Task<MemorySearchPayload> SearchMemoryAsync(
        string query,
        string sessionId,
        CancellationToken cancellationToken);

    /// <summary>Stato della memoria visibile dalla sessione, con la coda.</summary>
    internal abstract Task<MemoryOverviewPayload> MemoryOverviewAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Pacchetti installati.</summary>
    internal abstract Task<PluginListPayload> ListPluginsAsync(CancellationToken cancellationToken);

    /// <summary>Skill offerte dai pacchetti installati.</summary>
    internal abstract Task<SkillListPayload> ListSkillsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sessioni locali, per sceglierne una (S62).
    ///
    /// Passa dal canale come le altre letture: un selettore che leggesse lo store da se' funzionerebbe
    /// solo a worker spento, e con un worker vivo mostrerebbe un elenco vuoto invece di dirlo.
    /// </summary>
    internal abstract Task<SessionListPayload> ListSessionsAsync(bool includeArchived, CancellationToken cancellationToken);

    /// <summary>Da' un nome a una sessione, o lo toglie con null.</summary>
    internal abstract Task<SessionAckPayload> NameSessionAsync(string sessionId, string? name, CancellationToken cancellationToken);

    /// <summary>Archivia una sessione, o la riporta in vista. Non cancella nulla.</summary>
    internal abstract Task<SessionAckPayload> ArchiveSessionAsync(string sessionId, bool archived, CancellationToken cancellationToken);

    /// <summary>Ultimi turni conservati di una sessione.</summary>
    internal abstract Task<SessionTranscriptPayload> TranscriptAsync(string sessionId, int limit, CancellationToken cancellationToken);

    /// <summary>Biforca una sessione: l'originale non viene toccato.</summary>
    internal abstract Task<SessionAckPayload> ForkSessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    /// <summary>
    /// Apre il canale: worker se ce n'e' uno in ascolto, altrimenti esecuzione in questo processo.
    /// </summary>
    internal static async Task<ReplChannel> OpenAsync(
        ConfigurationSnapshot snapshot,
        PolicyDocument profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            IpcClient client = await IpcClient
                .ConnectAsync(snapshot.DataDirectory, TimeSpan.FromMilliseconds(300), cancellationToken)
                .ConfigureAwait(false);

            // Il worker rilegge i file per conto proprio a ogni richiesta; il REPL applica la stessa
            // regola a cio' che mostra, cosi' le due viste non si separano.
            return new WorkerReplChannel(client, profile, new ConfigurationWatch(snapshot, SystemClock.Instance));
        }
        catch (PaRouterException)
        {
            // Nessun worker in ascolto: il turno gira qui, e questo processo possiede lo store
            // finche' il REPL e' aperto.
            return new LocalReplChannel(RouterStack.Open(snapshot), profile);
        }
    }
}

/// <summary>Turno eseguito in questo processo, che possiede la directory dati.</summary>
internal sealed class LocalReplChannel(
    RouterStack stack,
    PolicyDocument profile) : ReplChannel
{
    /// <summary>Composizione aperta, condivisa da tutti i turni della sessione.</summary>
    internal RouterStack Stack => stack;

    /// <summary>
    /// Lo svuotamento della memoria del turno precedente (23 settembre 2026). Prima girava prima di restituire il prompt:
    /// l'operatore aspettava i vettori di una risposta che aveva gia' letto. Ora gira mentre scrive, e il turno dopo — o
    /// una lettura della memoria, o la chiusura — lo aspetta prima di cominciare: la ricerca trova sempre il ricordo nuovo.
    /// </summary>
    private Task pendingDrain = Task.CompletedTask;

    private async Task SettleAsync()
    {
        try
        {
            await pendingDrain.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PaRouterException or OperationCanceledException)
        {
            // Un lavoro di memoria fallito resta nella coda con il suo codice: lo dicono /memory e doctor.
        }
    }

    /// <inheritdoc />
    internal override string Location => Ui.Text("questo processo");

    /// <inheritdoc />
    internal override bool CanExplain => true;

    /// <inheritdoc />
    internal override ConfigurationSnapshot Snapshot => stack.Snapshot;

    /// <inheritdoc />
    internal override ConfigurationRefresh Refresh() => stack.Refresh();

    /// <inheritdoc />
    internal override async Task<ReplTurnResult> RunAsync(
        string input,
        string? sessionId,
        string? targetId,
        string? skillId,
        IReadOnlySet<Capability> capabilities,
        WorkspaceDescriptor? workspace,
        CancellationToken cancellationToken,
        ITurnObserver? observer = null,
        IReadOnlyList<ContextItem>? attachments = null,
        bool externalConfirmed = false)
    {
        await SettleAsync().ConfigureAwait(false);

        TurnOutcome outcome = await TurnRunner.RunAsync(
            stack,
            new TurnRequest(profile, input, sessionId, targetId, skillId, capabilities, workspace, attachments)
            {
                Observer = observer,
                ExternalConfirmed = externalConfirmed,
            },
            cancellationToken).ConfigureAwait(false);

        if (outcome.Result is not ExecutionResult result)
        {
            // La skill scelta non puo' entrare nel turno: il turno non parte, come in `pa run`.
            return new ReplTurnResult(
                outcome.SessionId,
                ExecutionState.Denied.ToWire(),
                null,
                ErrorCode.ExtensionPermissionDenied.ToWire(),
                [Ui.Text("skill non disponibile: ") + skillId],
                0,
                0);
        }

        // Una sola implementazione dello svuotamento, condivisa con `pa run` e con il worker; qui gira mentre l'operatore
        // legge la risposta.
        string drained = outcome.SessionId;
        pendingDrain = Task.Run(() => TurnMemory.DrainAsync(stack, profile, drained, CancellationToken.None));

        return new ReplTurnResult(
            outcome.SessionId,
            result.State.ToWire(),
            result.Output,
            result.ErrorCode?.ToWire(),
            [.. result.Decisions
                .Where(decision => !decision.Allowed)
                .Select(decision => decision.ToString())],
            outcome.Memory.Selected.Count,
            outcome.Memory.UsedBytes,
            result.Denial,
            result.WeakPerimeter,
            result.BelowDemand);
    }

    // Le letture passano da ReadRequests anche qui: e' la stessa funzione che il worker chiama per
    // un client, con la stessa richiesta. La risposta non dipende da chi possiede lo store.

    /// <inheritdoc />
    internal override async Task<MemorySearchPayload> SearchMemoryAsync(
        string query,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await SettleAsync().ConfigureAwait(false);
        return await ReadRequests.SearchMemoryAsync(stack, new MemoryQueryPayload(profile.ProfileId, sessionId, query), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    internal override async Task<MemoryOverviewPayload> MemoryOverviewAsync(string sessionId, CancellationToken cancellationToken)
    {
        await SettleAsync().ConfigureAwait(false);
        return await ReadRequests.MemoryOverviewAsync(stack, new MemoryQueryPayload(profile.ProfileId, sessionId), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    internal override Task<PluginListPayload> ListPluginsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ReadRequests.ListPlugins(stack));

    /// <inheritdoc />
    internal override Task<SkillListPayload> ListSkillsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ReadRequests.ListSkills(stack));

    /// <inheritdoc />
    internal override Task<SessionListPayload> ListSessionsAsync(bool includeArchived, CancellationToken cancellationToken) =>
        Task.FromResult(SessionRequests.List(stack, new SessionListRequest(includeArchived)));

    /// <inheritdoc />
    internal override Task<SessionAckPayload> NameSessionAsync(string sessionId, string? name, CancellationToken cancellationToken) =>
        SessionRequests.NameAsync(stack, new SessionNameRequest(sessionId, name), cancellationToken);

    /// <inheritdoc />
    internal override Task<SessionAckPayload> ArchiveSessionAsync(string sessionId, bool archived, CancellationToken cancellationToken) =>
        SessionRequests.ArchiveAsync(stack, new SessionArchiveRequest(sessionId, archived), cancellationToken);

    /// <inheritdoc />
    internal override Task<SessionTranscriptPayload> TranscriptAsync(string sessionId, int limit, CancellationToken cancellationToken) =>
        SessionRequests.TranscriptAsync(stack, new SessionTranscriptRequest(sessionId, limit), cancellationToken);

    /// <inheritdoc />
    internal override Task<SessionAckPayload> ForkSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        SessionRequests.ForkAsync(stack, new SessionForkRequest(sessionId), cancellationToken);

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await SettleAsync().ConfigureAwait(false);
        await stack.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Turno eseguito dal worker che possiede la directory dati.
///
/// Il worker restituisce cio' che la CLI stampa e nient'altro: nessun payload inviato al modello,
/// nessuna osservazione. Il REPL non ne sa di piu' di quanto ne sappia <c>pa run</c>.
/// </summary>
internal sealed class WorkerReplChannel(IpcClient client, PolicyDocument profile, ConfigurationWatch watch) : ReplChannel
{
    /// <inheritdoc />
    internal override string Location => Ui.Text("worker locale");

    /// <inheritdoc />
    internal override ConfigurationSnapshot Snapshot => watch.Current;

    /// <inheritdoc />
    internal override ConfigurationRefresh Refresh() => watch.Refresh();

    /// <summary>
    /// L'analisi locale ha bisogno dello scanner e dei candidati, che stanno nella composizione di
    /// chi possiede lo store. Con un worker vivo il REPL non li ha, e lo dichiara invece di
    /// ricostruire una catena diversa da quella che decidera' davvero.
    /// </summary>
    internal override bool CanExplain => false;

    /// <inheritdoc />
    internal override async Task<ReplTurnResult> RunAsync(
        string input,
        string? sessionId,
        string? targetId,
        string? skillId,
        IReadOnlySet<Capability> capabilities,
        WorkspaceDescriptor? workspace,
        CancellationToken cancellationToken,
        ITurnObserver? observer = null,
        IReadOnlyList<ContextItem>? attachments = null,
        bool externalConfirmed = false)
    {
        // Il canale non trasporta eventi: con un worker vivo fasi e blocchi restano quelli di prima
        // di S58. Portarli attraverso l'IPC e' S65.
        _ = observer;

        // La richiesta nasce dallo stesso TurnRequest del percorso locale: il worker riceve la skill
        // e gli allegati come li riceverebbe questo processo (protocollo 3, S63).
        RunResultPayload remote = await client
            .RunAsync(
                TurnRequests.ToPayload(
                    new TurnRequest(profile, input, sessionId, targetId, skillId, capabilities, workspace, attachments)
                    {
                        ExternalConfirmed = externalConfirmed,
                    }),
                cancellationToken)
            .ConfigureAwait(false);

        return new ReplTurnResult(
            // Il worker non restituisce la sessione. Non serve: il REPL la sceglie prima del primo
            // turno e la invia sempre, cosi' il primo turno la crea e i successivi la continuano.
            // Lasciarla decidere all'altro capo avrebbe significato non sapere quale sessione si
            // sta continuando.
            sessionId!,
            remote.Status,
            remote.Output,
            remote.ErrorCode,
            remote.Reasons,
            0,
            0,
            remote.Denial,
            remote.WeakPerimeter,
            remote.BelowDemand);
    }

    /// <inheritdoc />
    internal override Task<MemorySearchPayload> SearchMemoryAsync(
        string query,
        string sessionId,
        CancellationToken cancellationToken) =>
        client.SearchMemoryAsync(new MemoryQueryPayload(profile.ProfileId, sessionId, query), cancellationToken);

    /// <inheritdoc />
    internal override Task<MemoryOverviewPayload> MemoryOverviewAsync(string sessionId, CancellationToken cancellationToken) =>
        client.MemoryOverviewAsync(new MemoryQueryPayload(profile.ProfileId, sessionId), cancellationToken);

    /// <inheritdoc />
    internal override Task<PluginListPayload> ListPluginsAsync(CancellationToken cancellationToken) =>
        client.ListPluginsAsync(cancellationToken);

    /// <inheritdoc />
    internal override Task<SkillListPayload> ListSkillsAsync(CancellationToken cancellationToken) =>
        client.ListSkillsAsync(cancellationToken);

    /// <inheritdoc />
    internal override Task<SessionListPayload> ListSessionsAsync(bool includeArchived, CancellationToken cancellationToken) =>
        client.ListSessionsAsync(new SessionListRequest(includeArchived), cancellationToken);

    /// <inheritdoc />
    internal override Task<SessionAckPayload> NameSessionAsync(string sessionId, string? name, CancellationToken cancellationToken) =>
        client.NameSessionAsync(new SessionNameRequest(sessionId, name), cancellationToken);

    /// <inheritdoc />
    internal override Task<SessionAckPayload> ArchiveSessionAsync(string sessionId, bool archived, CancellationToken cancellationToken) =>
        client.ArchiveSessionAsync(new SessionArchiveRequest(sessionId, archived), cancellationToken);

    /// <inheritdoc />
    internal override Task<SessionTranscriptPayload> TranscriptAsync(string sessionId, int limit, CancellationToken cancellationToken) =>
        client.TranscriptAsync(new SessionTranscriptRequest(sessionId, limit), cancellationToken);

    /// <inheritdoc />
    internal override Task<SessionAckPayload> ForkSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        client.ForkSessionAsync(new SessionForkRequest(sessionId), cancellationToken);

    /// <inheritdoc />
    public override async ValueTask DisposeAsync() => await client.DisposeAsync().ConfigureAwait(false);
}
