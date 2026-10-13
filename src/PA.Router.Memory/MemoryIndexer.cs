using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Memory;

/// <summary>
/// Esecutore dei lavori di memoria: indicizzazione e rimozione.
///
/// Prende un lavoro alla volta con un lease. Se il processo muore mentre lo tiene, il lease scade
/// e il lavoro torna prendibile: e' il recovery, e non richiede che qualcuno se ne accorga.
///
/// Un errore transitorio del modello locale non consuma il ricordo: il lavoro torna disponibile
/// dopo l'attesa prevista, fino a tre tentativi. Esauriti, resta <c>failed</c> con il motivo
/// visibile, perche' un'osservazione che non si indicizza deve risultare tale e non sparire
/// silenziosamente dall'indice (03-specs/MEMORY.md).
/// </summary>
public sealed class MemoryIndexer
{
    /// <summary>Attese fra i tentativi, come da specifica.</summary>
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)];

    /// <summary>Durata del lease di un lavoro.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    /// <summary>Fingerprint segnaposto usato finche' nessun modello ha indicizzato il progetto.</summary>
    public const string PendingFingerprint = "pending";

    private readonly IMemoryIndexStore store;
    private readonly IEmbeddingProvider? embeddings;

    /// <param name="store">Persistenza dei lavori e dei vettori.</param>
    /// <param name="embeddings">
    /// Provider locale. Se manca, i lavori di indicizzazione falliscono con
    /// EMBEDDING_UNAVAILABLE: non esiste un ripiego cloud.
    /// </param>
    public MemoryIndexer(IMemoryIndexStore store, IEmbeddingProvider? embeddings)
    {
        ArgumentNullException.ThrowIfNull(store);

        this.store = store;
        this.embeddings = embeddings;
    }

    /// <summary>
    /// Esegue un solo lavoro, se ce n'e' uno disponibile. Restituisce falso quando la coda e'
    /// vuota, cosi' il chiamante decide lui quando riprovare.
    /// </summary>
    public async ValueTask<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        MemoryJob? job = await store.LeaseJobAsync(LeaseDuration, cancellationToken).ConfigureAwait(false);

        if (job is null)
        {
            return false;
        }

        if (job.Kind is MemoryJobKind.Purge)
        {
            await store.CompletePurgeAsync(job, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (job.Destination is MemoryDestination.Enhanced)
        {
            // Un lavoro nato per l'indice potenziato, che non esiste piu' (ADR-032): il ricordo scende all'indice locale,
            // l'unica direzione che lo schema ammette, e il prossimo giro lo indicizza qui. Lasciarlo in coda lo terrebbe
            // senza vettori per sempre.
            await store.DemoteToLocalAsync(job, ErrorCode.EmbeddingUnavailable.ToWire(), cancellationToken).ConfigureAwait(false);
            return true;
        }

        IEmbeddingProvider? provider = embeddings;

        if (provider is null)
        {
            await FailAsync(job, ErrorCode.EmbeddingUnavailable, cancellationToken).ConfigureAwait(false);
            return true;
        }

        IReadOnlyList<MemoryChunk> chunks =
            await store.ReadChunksAsync(job.ObservationId, cancellationToken).ConfigureAwait(false);

        if (chunks.Count == 0)
        {
            // Cancellata mentre il lavoro attendeva: non c'e' nulla da indicizzare, e non e' un
            // errore da ritentare.
            await store.CompletePurgeAsync(job, cancellationToken).ConfigureAwait(false);
            return true;
        }

        string fingerprint = provider.Fingerprint.ToString();

        if (job.ModelFingerprint is not PendingFingerprint
            && !string.Equals(job.ModelFingerprint, fingerprint, StringComparison.Ordinal))
        {
            // Il modello e' cambiato sotto il lavoro: mescolare i vettori sarebbe peggio che
            // fallire, perche' le distanze non sarebbero piu' confrontabili.
            await FailAsync(job, ErrorCode.EmbeddingVersionMismatch, cancellationToken).ConfigureAwait(false);
            return true;
        }

        List<ChunkVector> vectors = [];

        try
        {
            foreach (MemoryChunk[] batch in Batches(chunks, provider.MaxBatchSize))
            {
                EmbeddingBatch produced = await provider
                    .EmbedAsync([.. batch.Select(chunk => chunk.Text)], cancellationToken)
                    .ConfigureAwait(false);

                if (produced.Vectors.Count != batch.Length)
                {
                    await FailAsync(job, ErrorCode.MemoryIndexFailed, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                for (int index = 0; index < batch.Length; index++)
                {
                    vectors.Add(new ChunkVector(batch[index].Id, produced.Vectors[index]));
                }
            }
        }
        catch (PaRouterException ex)
        {
            await FailAsync(job, ex.Code, cancellationToken).ConfigureAwait(false);
            return true;
        }

        await store
            .CompleteIndexAsync(job with { ModelFingerprint = fingerprint }, vectors, cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>Svuota la coda, fino al numero di lavori indicato.</summary>
    /// <summary>
    /// Reindicizza sotto il modello attivo: accoda cio' che manca e poi svuota la coda. E' il
    /// percorso che l'operatore usa dopo aver cambiato servizio di embedding, ed e' esplicito
    /// perche' rifare gli embedding di tutta la memoria costa tempo e non va deciso da solo.
    /// </summary>
    /// <returns>Lavori accodati e lavori eseguiti.</returns>
    public async ValueTask<(int Enqueued, int Executed)> ReindexAsync(int maxJobs, CancellationToken cancellationToken)
    {
        if (embeddings is null)
        {
            // Nessun ripiego cloud: senza modello locale non si indicizza.
            throw new PaRouterException(ErrorCode.EmbeddingUnavailable, "No embedding provider is configured.");
        }

        int enqueued = await store
            .EnqueueReindexAsync(embeddings.Fingerprint.ToString(), cancellationToken)
            .ConfigureAwait(false);

        int executed = await DrainAsync(maxJobs, cancellationToken).ConfigureAwait(false);

        // Il fingerprint nuovo diventa attivo solo quando la sua coda e' vuota: finche' l'indice e'
        // parziale, cercarci dentro darebbe risultati peggiori di quelli di prima.
        await store
            .PromoteFingerprintAsync(embeddings.Fingerprint.ToString(), cancellationToken)
            .ConfigureAwait(false);

        return (enqueued, executed);
    }

    /// <inheritdoc cref="DrainAsync" />
    public async ValueTask<int> DrainAsync(int maxJobs, CancellationToken cancellationToken)
    {
        if (maxJobs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxJobs));
        }

        int executed = 0;

        while (executed < maxJobs && await RunOnceAsync(cancellationToken).ConfigureAwait(false))
        {
            executed++;
        }

        return executed;
    }

    private ValueTask FailAsync(MemoryJob job, ErrorCode code, CancellationToken cancellationToken)
    {
        TimeSpan retryAfter = job.Attempts <= Backoff.Length ? Backoff[job.Attempts - 1] : TimeSpan.Zero;

        // Un disallineamento di versione non si risolve aspettando: non lo si ritenta.
        if (code is ErrorCode.EmbeddingVersionMismatch)
        {
            retryAfter = TimeSpan.Zero;
        }

        return store.FailJobAsync(job, code, retryAfter, cancellationToken);
    }

    private static IEnumerable<MemoryChunk[]> Batches(IReadOnlyList<MemoryChunk> chunks, int size)
    {
        for (int index = 0; index < chunks.Count; index += size)
        {
            yield return [.. chunks.Skip(index).Take(size)];
        }
    }
}
