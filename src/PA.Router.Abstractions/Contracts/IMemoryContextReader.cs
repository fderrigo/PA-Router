namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Rilettura interna dei candidati per l'auto-injection. Distingue il limite di ricerca (20 ID)
/// da quello del comando get (10 ID), senza allargare l'API degli strumenti di memoria.
/// </summary>
public interface IMemoryContextReader
{
    /// <summary>
    /// Rivalida in una sola operazione fino a <see cref="MemoryLimits.MaxSearchLimit"/> ID nello scope corrente.
    /// Restituisce corpi in ordine di richiesta entro <see cref="MemoryLimits.MaxFetchBytes"/> byte UTF-8:
    /// un corpo che non entra viene saltato, mai troncato. Nessun corpo precedentemente letto e' un'autorizzazione.
    /// </summary>
    ValueTask<IReadOnlyList<ObservationRecord>> GetForContextAsync(
        MemoryScope scope,
        IReadOnlyList<string> observationIds,
        CancellationToken cancellationToken);
}
