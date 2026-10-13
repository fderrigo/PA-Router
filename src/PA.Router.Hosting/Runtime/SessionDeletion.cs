using PA.Router.Infrastructure.Storage;

namespace PA.Router.Hosting.Runtime;

/// <summary>Che cosa ha tolto la cancellazione di una conversazione.</summary>
/// <param name="ContentsRemoved">Contenuti cifrati dei turni rimossi.</param>
/// <param name="MemoriesRemoved">Ricordi nati nella conversazione, resi invisibili e messi in coda per la rimozione.</param>
public sealed record SessionDeletionResult(int ContentsRemoved, int MemoriesRemoved);

/// <summary>
/// Cancellare una conversazione (23 settembre 2026). Fino ad allora `pa session delete` e l'SDK toglievano i turni e
/// lasciavano i ricordi nati da quei turni: le risposte restavano ricercabili e rientravano in altre conversazioni.
/// «Cancellare vuol dire togliere» (S50): con la conversazione se ne vanno anche i suoi ricordi, con la stessa strada di
/// `pa memory forget`. Un punto solo per terminale e SDK (INV-22).
/// </summary>
public static class SessionDeletion
{
    /// <summary>Toglie i contenuti della conversazione e i ricordi nati in lei.</summary>
    public static async ValueTask<SessionDeletionResult> DeleteAsync(
        RouterStack stack,
        SqliteSessionService sessions,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        int contents = await sessions.DeleteContentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        int memories = await stack.MemoryStore().TombstoneSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);

        return new SessionDeletionResult(contents, memories);
    }
}
