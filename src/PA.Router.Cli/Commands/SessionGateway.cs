using PA.Router.Abstractions;
using PA.Router.Hosting.Runtime;
using PA.Router.Registry.Loading;
using PA.Router.Runtime.Ipc;

namespace PA.Router.Cli.Commands;

/// <summary>
/// Da dove i comandi <c>pa session</c> leggono e scrivono le sessioni (S62).
///
/// Vale la regola di <c>pa run</c>: se un worker possiede la directory dati, il lavoro passa da lui;
/// altrimenti lo fa questo processo, che per la durata del comando possiede lo store. Senza questo,
/// dare un nome o archiviare una conversazione funzionerebbe **solo a worker spento** — e fallirebbe
/// con «store occupato» proprio nella configurazione in cui il router e' piu' usato.
///
/// La scelta del trasporto compare anche in <c>ReplChannel</c>, e non e' una copia: il REPL la fa una
/// volta all'avvio e tiene aperta la propria maniglia per tutta la sessione, un comando vive un
/// istante e la fa per invocazione. Cio' che **non** si duplica e' l'operazione: da entrambe le parti
/// e' <see cref="SessionRequests"/>, quindi la risposta non dipende da chi possiede la directory
/// (INV-22).
/// </summary>
internal abstract class SessionGateway : IAsyncDisposable
{
    /// <summary>Descrizione di dove l'operazione e' avvenuta, per le diagnosi.</summary>
    internal abstract string Location { get; }

    /// <summary>Configurazione con cui il comando e' stato aperto: serve alla resa, per nominare i profili.</summary>
    internal ConfigurationSnapshot? Snapshot { get; private set; }

    /// <summary>Sessioni locali, dalla piu' recente.</summary>
    internal abstract Task<SessionListPayload> ListAsync(bool includeArchived, CancellationToken cancellationToken);

    /// <summary>Da' un nome a una sessione, o lo toglie con null.</summary>
    internal abstract Task<SessionAckPayload> NameAsync(string sessionId, string? name, CancellationToken cancellationToken);

    /// <summary>Archivia una sessione, o la riporta in vista. Non cancella nulla.</summary>
    internal abstract Task<SessionAckPayload> ArchiveAsync(string sessionId, bool archived, CancellationToken cancellationToken);

    /// <summary>Ultimi turni conservati di una sessione.</summary>
    internal abstract Task<SessionTranscriptPayload> TranscriptAsync(string sessionId, int limit, CancellationToken cancellationToken);

    /// <summary>Biforca una sessione: l'originale non viene toccato.</summary>
    internal abstract Task<SessionAckPayload> ForkAsync(string sessionId, CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    /// <summary>
    /// Apre il trasporto: worker se ce n'e' uno in ascolto, altrimenti questo processo.
    /// </summary>
    /// <param name="writable">
    /// Vero per i comandi che scrivono. Senza worker decide se prendere la proprieta' dello store: un
    /// elenco non deve occupare la directory dati, una scrittura deve.
    /// </param>
    internal static async Task<SessionGateway> OpenAsync(
        ConfigurationSnapshot snapshot,
        bool writable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            IpcClient client = await IpcClient
                .ConnectAsync(snapshot.DataDirectory, TimeSpan.FromMilliseconds(300), cancellationToken)
                .ConfigureAwait(false);

            return new WorkerSessionGateway(client) { Snapshot = snapshot };
        }
        catch (PaRouterException)
        {
            // Nessun worker in ascolto: lo store lo apre questo processo.
            return new LocalSessionGateway(RouterStack.Open(snapshot, ownsStore: writable)) { Snapshot = snapshot };
        }
    }
}

/// <summary>Sessioni lette e scritte da questo processo, che possiede la directory dati.</summary>
internal sealed class LocalSessionGateway(RouterStack stack) : SessionGateway
{
    /// <inheritdoc />
    internal override string Location => Ui.Text("questo processo");

    /// <inheritdoc />
    internal override Task<SessionListPayload> ListAsync(bool includeArchived, CancellationToken cancellationToken) =>
        Task.FromResult(SessionRequests.List(stack, new SessionListRequest(includeArchived)));

    /// <inheritdoc />
    internal override Task<SessionAckPayload> NameAsync(string sessionId, string? name, CancellationToken cancellationToken) =>
        SessionRequests.NameAsync(stack, new SessionNameRequest(sessionId, name), cancellationToken);

    /// <inheritdoc />
    internal override Task<SessionAckPayload> ArchiveAsync(string sessionId, bool archived, CancellationToken cancellationToken) =>
        SessionRequests.ArchiveAsync(stack, new SessionArchiveRequest(sessionId, archived), cancellationToken);

    /// <inheritdoc />
    internal override Task<SessionTranscriptPayload> TranscriptAsync(string sessionId, int limit, CancellationToken cancellationToken) =>
        SessionRequests.TranscriptAsync(stack, new SessionTranscriptRequest(sessionId, limit), cancellationToken);

    /// <inheritdoc />
    internal override Task<SessionAckPayload> ForkAsync(string sessionId, CancellationToken cancellationToken) =>
        SessionRequests.ForkAsync(stack, new SessionForkRequest(sessionId), cancellationToken);

    /// <inheritdoc />
    public override async ValueTask DisposeAsync() => await stack.DisposeAsync().ConfigureAwait(false);
}

/// <summary>Sessioni lette e scritte dal worker che possiede la directory dati.</summary>
internal sealed class WorkerSessionGateway(IpcClient client) : SessionGateway
{
    /// <inheritdoc />
    internal override string Location => Ui.Text("worker locale");

    /// <inheritdoc />
    internal override Task<SessionListPayload> ListAsync(bool includeArchived, CancellationToken cancellationToken) =>
        client.ListSessionsAsync(new SessionListRequest(includeArchived), cancellationToken);

    /// <inheritdoc />
    internal override Task<SessionAckPayload> NameAsync(string sessionId, string? name, CancellationToken cancellationToken) =>
        client.NameSessionAsync(new SessionNameRequest(sessionId, name), cancellationToken);

    /// <inheritdoc />
    internal override Task<SessionAckPayload> ArchiveAsync(string sessionId, bool archived, CancellationToken cancellationToken) =>
        client.ArchiveSessionAsync(new SessionArchiveRequest(sessionId, archived), cancellationToken);

    /// <inheritdoc />
    internal override Task<SessionTranscriptPayload> TranscriptAsync(string sessionId, int limit, CancellationToken cancellationToken) =>
        client.TranscriptAsync(new SessionTranscriptRequest(sessionId, limit), cancellationToken);

    /// <inheritdoc />
    internal override Task<SessionAckPayload> ForkAsync(string sessionId, CancellationToken cancellationToken) =>
        client.ForkSessionAsync(new SessionForkRequest(sessionId), cancellationToken);

    /// <inheritdoc />
    public override async ValueTask DisposeAsync() => await client.DisposeAsync().ConfigureAwait(false);
}
