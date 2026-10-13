namespace PA.Router.Abstractions.Contracts;

/// <summary>Vettore pronto da salvare per un chunk.</summary>
/// <param name="ChunkId">Chunk indicizzato.</param>
/// <param name="Vector">Vettore normalizzato.</param>
public sealed record ChunkVector(string ChunkId, float[] Vector);

/// <summary>
/// Voce dell'indice caricata in memoria: identita', testo e vettore di un chunk autorizzato.
/// </summary>
/// <param name="ChunkId">Chunk.</param>
/// <param name="ObservationId">Osservazione di origine.</param>
/// <param name="Ordinal">Posizione nel testo dell'osservazione.</param>
/// <param name="Text">Testo del chunk, in memoria e mai su disco in chiaro.</param>
/// <param name="Vector">Vettore, assente se il chunk non e' ancora indicizzato.</param>
public sealed record MemoryIndexEntry(
    string ChunkId,
    string ObservationId,
    int Ordinal,
    string Text,
    float[]? Vector);

/// <summary>
/// Contenuto dell'indice di un ambito, con la generazione che lo ha prodotto.
///
/// La generazione e' il motivo per cui una cache non puo' sopravvivere a una revoca: cambia a ogni
/// transazione che modifica o revoca un ricordo, quindi uno snapshot vecchio non e' piu' valido
/// per rispondere (03-specs/SEMANTIC-SEARCH.md).
/// </summary>
/// <param name="Generation">Generazione della memoria del progetto.</param>
/// <param name="Fingerprint">Fingerprint dei vettori caricati.</param>
/// <param name="Entries">Chunk autorizzati.</param>
/// <param name="Observations">Osservazioni corrispondenti, gia' filtrate per ambito.</param>
/// <param name="Complete">Falso se qualche osservazione autorizzata non e' ancora indicizzata.</param>
public sealed record MemoryIndexSnapshot(
    long Generation,
    string? Fingerprint,
    IReadOnlyList<MemoryIndexEntry> Entries,
    IReadOnlyList<ObservationRecord> Observations,
    bool Complete);

/// <summary>
/// Persistenza dell'indice e dei lavori asincroni.
///
/// E' separata da <see cref="IObservationStore"/> perche' serve a chi indicizza, non a chi
/// ricorda: il modulo di memoria puo' lavorare senza sapere che sotto c'e' SQLite.
/// </summary>
public interface IExpiringMemory
{
    /// <summary>
    /// Cancella i ricordi la cui conservazione dichiarata e' finita: diventano invisibili subito e il loro lavoro di
    /// rimozione entra in coda, come per una cancellazione chiesta dall'operatore. Restituisce quanti ne ha trovati.
    /// </summary>
    ValueTask<int> TombstoneExpiredAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cancella i ricordi nati in una conversazione, con la stessa strada di una cancellazione chiesta dall'operatore:
    /// invisibili subito, rimossi dal lavoro in coda. Restituisce quanti ne ha trovati (23 settembre 2026).
    /// </summary>
    ValueTask<int> TombstoneSessionAsync(string sessionId, CancellationToken cancellationToken);
}

public interface IMemoryIndexStore
{
    /// <summary>
    /// Prende in carico un lavoro disponibile. Un lease scaduto torna disponibile da solo: e' il
    /// modo in cui un worker ucciso non lascia lavoro bloccato per sempre.
    /// </summary>
    ValueTask<MemoryJob?> LeaseJobAsync(TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Chunk di un'osservazione, con il testo decifrato.</summary>
    ValueTask<IReadOnlyList<MemoryChunk>> ReadChunksAsync(string observationId, CancellationToken cancellationToken);

    /// <summary>Salva i vettori di un lavoro e lo chiude nella stessa transazione.</summary>
    ValueTask CompleteIndexAsync(
        MemoryJob job,
        IReadOnlyList<ChunkVector> vectors,
        CancellationToken cancellationToken);

    /// <summary>Rimuove vettori e chunk di un'osservazione cancellata e chiude il lavoro.</summary>
    ValueTask CompletePurgeAsync(MemoryJob job, CancellationToken cancellationToken);

    /// <summary>
    /// Riporta un ricordo all'indice locale e riaccoda il suo lavoro.
    ///
    /// Serve a un lavoro nato per l'indice potenziato, che dal 24 settembre 2026 non esiste piu'
    /// (ADR-032). La destinazione non sale mai, ma **scende**: lasciarlo senza indice lo renderebbe
    /// irraggiungibile per via semantica per sempre.
    /// </summary>
    ValueTask DemoteToLocalAsync(MemoryJob job, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Registra un tentativo fallito. Con tentativi residui il lavoro torna disponibile dopo
    /// l'attesa indicata; esauriti i tentativi resta <c>failed</c> con il motivo, perche' un
    /// errore che sparisce non e' un errore risolto.
    /// </summary>
    ValueTask FailJobAsync(
        MemoryJob job,
        ErrorCode errorCode,
        TimeSpan retryAfter,
        CancellationToken cancellationToken);

    /// <summary>
    /// Generazione corrente della memoria del progetto. E' una lettura minima apposta: serve a
    /// decidere se una cache e' ancora valida senza ricaricare l'indice.
    /// </summary>
    ValueTask<long> ReadGenerationAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>Carica l'indice dell'ambito indicato, per il fingerprint attivo.</summary>
    ValueTask<MemoryIndexSnapshot> LoadIndexAsync(MemoryScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Accoda un lavoro di indicizzazione per ogni osservazione viva che non ha ancora vettori
    /// sotto il fingerprint indicato, e restituisce quanti ne ha accodati.
    ///
    /// E' il percorso esplicito di reindicizzazione dopo un cambio di modello. Riprovare i lavori
    /// vecchi non servirebbe: quelli sono legati al fingerprint di prima e falliscono per
    /// costruzione. I vettori vecchi non vengono toccati qui: restano finche' la retention non li
    /// porta via, e nessuna ricerca li mescola con i nuovi perche' l'indice si carica per un
    /// fingerprint solo.
    /// </summary>
    ValueTask<int> EnqueueReindexAsync(string modelFingerprint, CancellationToken cancellationToken);

    /// <summary>
    /// Rende attivo il fingerprint indicato per i progetti che lo hanno indicizzato per intero, e
    /// restituisce quanti ne ha promossi.
    ///
    /// La promozione avviene solo a coda vuota per quel fingerprint: finche' restano lavori in
    /// attesa o falliti, l'indice nuovo e' parziale e cercarci dentro darebbe risultati peggiori
    /// di quelli di prima. La generazione avanza, cosi' le cache costruite sull'indice vecchio
    /// vengono invalidate.
    /// </summary>
    ValueTask<int> PromoteFingerprintAsync(string modelFingerprint, CancellationToken cancellationToken);
}
