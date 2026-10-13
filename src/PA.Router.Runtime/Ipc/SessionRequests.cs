// ContentMode non e' un enum con valori wire per attributo: la sua conversione vive in
// `ContentModes`, qui. Senza questa direttiva `ToWire()` si risolve sull'estensione generica di
// WireEnum, che compila e fallisce a tempo di esecuzione.
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure.Storage;

namespace PA.Router.Runtime.Ipc;

/// <summary>
/// Sessioni dell'operatore — elenco, nome, archivio, trascritto, biforca — nella forma in cui
/// viaggiano sul canale del worker (S62).
///
/// Vale la regola di <see cref="ReadRequests"/>, con una differenza: qui ci sono anche scritture.
/// Un client non possiede lo store quando un worker e' vivo, quindi dare un nome a una sessione
/// senza passare dal canale sarebbe un comando che funziona solo quando il worker e' spento. Chi
/// possiede lo store chiama la stessa funzione direttamente, e l'operazione e'
/// <see cref="SessionCatalog"/> in entrambi i casi (INV-22).
/// </summary>
public static class SessionRequests
{
    /// <summary>
    /// Turni riportati da un trascritto quando il chiamante non ne chiede un numero.
    ///
    /// Un frame del canale ha un tetto: una conversazione di trecento turni non deve diventare un
    /// errore di protocollo, e chi vuole l'intera cronologia la legge dove e' conservata.
    /// </summary>
    public const int DefaultTranscriptTurns = 20;

    /// <summary>Sessioni locali, dalla piu' recente.</summary>
    public static SessionListPayload List(RouterStack stack, SessionListRequest request)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<SessionSummary> sessions = SessionCatalog.List(stack, request.IncludeArchived, request.Limit);

        return new SessionListPayload(
            [.. sessions.Select(session => new SessionPayload(
                session.SessionId,
                session.ProfileId,
                session.Channel.ToWire(),
                session.ContentMode.ToWire(),
                session.CreatedAt,
                session.Turns,
                session.DisplayName,
                session.ArchivedAt is not null))],
            stack.Sessions().PersistsHistory);
    }

    /// <summary>Da' un nome a una sessione, o lo toglie.</summary>
    public static async Task<SessionAckPayload> NameAsync(
        RouterStack stack,
        SessionNameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(request);

        await SessionCatalog.NameAsync(stack, request.SessionId, request.Name, cancellationToken).ConfigureAwait(false);

        return new SessionAckPayload(request.SessionId);
    }

    /// <summary>Archivia una sessione, o la riporta in vista.</summary>
    public static async Task<SessionAckPayload> ArchiveAsync(
        RouterStack stack,
        SessionArchiveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(request);

        await SessionCatalog
            .ArchiveAsync(stack, request.SessionId, request.Archived, cancellationToken)
            .ConfigureAwait(false);

        return new SessionAckPayload(request.SessionId);
    }

    /// <summary>Ultimi turni conservati di una sessione.</summary>
    public static async Task<SessionTranscriptPayload> TranscriptAsync(
        RouterStack stack,
        SessionTranscriptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<SessionTurn> turns = await SessionCatalog
            .TranscriptAsync(stack, request.SessionId, cancellationToken)
            .ConfigureAwait(false);

        int limit = request.Limit <= 0 ? DefaultTranscriptTurns : request.Limit;

        return new SessionTranscriptPayload(
            [.. turns
                .Skip(Math.Max(0, turns.Count - limit))
                .Select(turn => new SessionTurnPayload(turn.Ordinal, turn.Input, turn.Output, turn.CreatedAt))],
            turns.Count);
    }

    /// <summary>Biforca una sessione: l'originale non viene toccato.</summary>
    public static async Task<SessionAckPayload> ForkAsync(
        RouterStack stack,
        SessionForkRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(request);

        string forkId = await SessionCatalog
            .ForkAsync(stack, request.SessionId, cancellationToken)
            .ConfigureAwait(false);

        return new SessionAckPayload(forkId);
    }
}
