using System.Text;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Abstractions.Observability;

namespace PA.Router.Search.Local;

/// <summary>Opzioni della ricerca locale.</summary>
/// <param name="TitleLength">Caratteri massimi del titolo di un risultato.</param>
/// <param name="MinimumSimilarity">Coseno minimo perche' un frammento sia un risultato.</param>
public sealed record MemorySearchOptions(
    int TitleLength = 160,
    double MinimumSimilarity = MemoryLimits.DefaultMinimumSimilarity);

/// <summary>
/// Ricerca ibrida nella memoria locale (03-specs/SEMANTIC-SEARCH.md, sezione «Ricerca»).
///
/// L'ordine dei passi e' il punto: prima l'ambito, poi il punteggio. I candidati sono solo
/// osservazioni gia' autorizzate, non l'intero corpus filtrato alla fine — un ranking globale con
/// filtro finale, prima o poi, lascia passare qualcosa.
///
/// Prima di rispondere l'ambito viene verificato di nuovo: fra il punteggio e la risposta un
/// ricordo puo' essere stato revocato, e uno snapshot non e' un'autorizzazione.
///
/// Un modello di embedding assente, guasto o di un'altra versione non rende la memoria
/// indisponibile: la ricerca risponde per parole e lo dichiara (<c>lexical_only</c>). Fino alla S38,
/// con <c>memory.required</c>, lo stesso caso faceva fallire il turno — e siccome una configurazione
/// appena creata non ha un modello, falliva il secondo turno di ogni conversazione. La memoria
/// indisponibile resta quella che non si puo' leggere, e quella la decide l'archivio.
/// </summary>
public sealed class LocalMemorySearch : IMemorySearch, IMemoryContextReader, IDisposable
{
    private readonly IMemoryIndexStore index;
    private readonly IObservationStore observations;
    private readonly IEmbeddingProvider? embeddings;
    private readonly MemoryIndexCache cache;
    private readonly MemorySearchOptions options;
    private readonly bool ownsCache;

    /// <param name="index">Persistenza dell'indice.</param>
    /// <param name="observations">Archivio delle osservazioni, per la rivalidazione dell'ambito.</param>
    /// <param name="embeddings">Provider locale, gia' passato dal gate ausiliario.</param>
    /// <param name="cache">Cache degli indici caricati.</param>
    /// <param name="options">Opzioni della ricerca.</param>
    public LocalMemorySearch(
        IMemoryIndexStore index,
        IObservationStore observations,
        IEmbeddingProvider? embeddings,
        MemoryIndexCache? cache = null,
        MemorySearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(observations);

        this.index = index;
        this.observations = observations;
        this.embeddings = embeddings;
        this.cache = cache ?? new MemoryIndexCache();
        ownsCache = cache is null;
        this.options = options ?? new MemorySearchOptions();
    }

    /// <summary>
    /// Provider effettivamente in uso, gia' passato dal gate ausiliario. Serve a chi indicizza:
    /// usare un provider diverso da quello della ricerca produrrebbe due indici incompatibili.
    /// </summary>
    public IEmbeddingProvider? Embeddings => embeddings;

    /// <inheritdoc />
    public async ValueTask<MemorySearchResult> SearchAsync(
        MemoryScope scope,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrWhiteSpace(query))
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "A memory query cannot be empty.");
        }

        if (limit is < 1 or > MemoryLimits.MaxSearchLimit)
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "The requested limit is outside the allowed range.")
                .WithMetadata("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithMetadata("maxLimit", MemoryLimits.MaxSearchLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        (string cacheKey, MemoryIndexSnapshot snapshot) = await LoadAsync(scope, cancellationToken).ConfigureAwait(false);

        if (snapshot.Entries.Count == 0)
        {
            MemorySearchMode empty = Mode(snapshot);

            RouterTelemetry.RecordMemorySearch(
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, empty.ToWire());

            return new MemorySearchResult([], empty, Status(snapshot), snapshot.Fingerprint);
        }

        float[]? queryVector = await EmbedQueryAsync(query, snapshot, cancellationToken).ConfigureAwait(false);
        snapshot = cache.FilterExpired(snapshot);
        IReadOnlyList<string> vectorRanked = queryVector is null
            ? []
            : VectorSearch.Rank(snapshot.Entries, queryVector, MemoryLimits.CandidatesPerList, options.MinimumSimilarity);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> lexicalRanked = cache.SearchLexical(cacheKey, snapshot, query, MemoryLimits.CandidatesPerList);
        MemorySearchMode mode = queryVector is null ? MemorySearchMode.LexicalOnly : MemorySearchMode.Hybrid;

        // Con i vettori disponibili il lessicale spareggia, non governa: alla pari peggiorerebbe
        // l'ordine. Senza vettori resta l'unica lista e mantiene peso pieno.
        IReadOnlyList<(string ChunkId, double Score)> fused = RankFusion.Fuse(
            vectorRanked,
            lexicalRanked,
            mode is MemorySearchMode.LexicalOnly ? 1.0 : MemoryLimits.HybridLexicalWeight);

        Dictionary<string, string> owner = snapshot.Entries.ToDictionary(
            entry => entry.ChunkId,
            entry => entry.ObservationId,
            StringComparer.Ordinal);

        // Piu' chunk della stessa osservazione non valgono piu' ricordi: conta il migliore.
        List<(string ObservationId, double Score)> ranked = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach ((string chunkId, double score) in fused)
        {
            if (!owner.TryGetValue(chunkId, out string? observationId) || !seen.Add(observationId))
            {
                continue;
            }

            ranked.Add((observationId, score));
        }

        IReadOnlyList<MemoryHit> hits = await BuildHitsAsync(scope, ranked, limit, cancellationToken).ConfigureAwait(false);

        // Durata e modo, niente query: la misura serve a sapere se l'indice regge, non che cosa e'
        // stato cercato.
        RouterTelemetry.RecordMemorySearch(
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, mode.ToWire());

        return new MemorySearchResult(
            hits,
            mode,
            Status(snapshot),
            queryVector is null ? null : snapshot.Fingerprint);
    }

    /// <summary>
    /// Retrieves bounded candidate evidence before the cosine floor, using the same authorized
    /// snapshot, embedding call and lexical retrieval as search. Results are revalidated before
    /// returning; candidate IDs remain insufficient authorization for a later body read.
    /// This internal method does not alter the public search or auto-injection policy.
    /// </summary>
    internal async ValueTask<MemoryCandidateResult> GetCandidatesAsync(
        MemoryScope scope,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "A memory query cannot be empty.");
        }

        (string cacheKey, MemoryIndexSnapshot snapshot) = await LoadAsync(scope, cancellationToken).ConfigureAwait(false);
        if (snapshot.Entries.Count == 0)
        {
            return new MemoryCandidateResult([], Mode(snapshot), Status(snapshot), snapshot.Fingerprint);
        }

        MemoryCandidateResult result = await ScoreCandidatesAsync(cacheKey, snapshot, query, cancellationToken).ConfigureAwait(false);
        if (result.Candidates.Count == 0) { return result; }

        IReadOnlyList<string> ids = [.. result.Candidates.Select(candidate => candidate.ObservationId).Distinct(StringComparer.Ordinal)];
        IReadOnlyList<ObservationRecord> authorized = await observations.ReadAuthorizedAsync(scope, ids, cancellationToken).ConfigureAwait(false);
        HashSet<string> visible = authorized.Select(observation => observation.Id).ToHashSet(StringComparer.Ordinal);

        return result with { Candidates = [.. result.Candidates.Where(candidate => visible.Contains(candidate.ObservationId))] };
    }

    private async ValueTask<MemoryCandidateResult> ScoreCandidatesAsync(
        string cacheKey,
        MemoryIndexSnapshot snapshot,
        string query,
        CancellationToken cancellationToken)
    {
        float[]? queryVector = await EmbedQueryAsync(query, snapshot, cancellationToken).ConfigureAwait(false);
        snapshot = cache.FilterExpired(snapshot);
        IReadOnlyList<VectorChunkScore> scored = queryVector is null ? [] : VectorSearch.Score(snapshot.Entries, queryVector);
        IReadOnlyList<string> vectorRanked = [.. scored.Take(MemoryLimits.CandidatesPerList).Select(item => item.ChunkId)];

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> lexicalRanked = cache.SearchLexical(cacheKey, snapshot, query, MemoryLimits.CandidatesPerList);
        IReadOnlyList<ChunkFusionEvidence> fused = RankFusion.FuseWithEvidence(
            vectorRanked, lexicalRanked, queryVector is null ? 1.0 : MemoryLimits.HybridLexicalWeight);
        Dictionary<string, double> cosines = scored.ToDictionary(item => item.ChunkId, item => item.Cosine, StringComparer.Ordinal);
        Dictionary<string, MemoryIndexEntry> entries = snapshot.Entries.ToDictionary(item => item.ChunkId, StringComparer.Ordinal);
        List<MemoryCandidate> candidates = [];
        foreach (ChunkFusionEvidence item in fused)
        {
            MemoryIndexEntry entry = entries[item.ChunkId];
            double? cosine = cosines.TryGetValue(item.ChunkId, out double value) ? value : null;
            candidates.Add(new MemoryCandidate(
                item.ChunkId, entry.ObservationId, entry.Ordinal, cosine, item.VectorRank, item.LexicalRank, item.Score,
                cosine.HasValue && !(cosine.Value < options.MinimumSimilarity)));
        }

        return new MemoryCandidateResult(candidates,
            queryVector is null ? MemorySearchMode.LexicalOnly : MemorySearchMode.Hybrid,
            Status(snapshot), queryVector is null ? null : snapshot.Fingerprint);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MemoryHit>> TimelineAsync(
        MemoryScope scope,
        string observationId,
        int before,
        int after,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(observationId);

        if (before is < 0 or > MemoryLimits.MaxTimelineNeighbours || after is < 0 or > MemoryLimits.MaxTimelineNeighbours)
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "The requested timeline window is outside the allowed range.");
        }

        IReadOnlyList<ObservationRecord> authorized =
            await observations.ReadAuthorizedAsync(scope, cancellationToken).ConfigureAwait(false);

        List<ObservationRecord> ordered = [.. authorized
            .OrderBy(record => record.CreatedAt)
            .ThenBy(record => record.Id, StringComparer.Ordinal)];

        int anchor = ordered.FindIndex(record => string.Equals(record.Id, observationId, StringComparison.Ordinal));

        if (anchor < 0)
        {
            // Fuori ambito o inesistente: la risposta e' la stessa, e non rivela quale dei due.
            throw new PaRouterException(ErrorCode.MemoryScopeDenied, "That memory is not available in this scope.");
        }

        int start = Math.Max(0, anchor - before);
        int end = Math.Min(ordered.Count - 1, anchor + after);

        return [.. ordered.GetRange(start, end - start + 1).Select(record => Hit(record, score: 0))];
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ObservationRecord>> GetAsync(
        MemoryScope scope,
        IReadOnlyList<string> observationIds,
        CancellationToken cancellationToken) =>
        GetBodiesAsync(scope, observationIds, MemoryLimits.MaxFetchIds, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ObservationRecord>> GetForContextAsync(
        MemoryScope scope,
        IReadOnlyList<string> observationIds,
        CancellationToken cancellationToken) =>
        GetBodiesAsync(scope, observationIds, MemoryLimits.MaxSearchLimit, cancellationToken);

    private async ValueTask<IReadOnlyList<ObservationRecord>> GetBodiesAsync(
        MemoryScope scope,
        IReadOnlyList<string> observationIds,
        int maxIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(observationIds);

        if (observationIds.Count == 0 || observationIds.Count > maxIds)
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "The number of requested memories is outside the allowed range.")
                .WithMetadata("maxIds", maxIds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Nuova verifica, non riuso del controllo della ricerca: un ID trovato prima non e' un
        // permesso di leggerne il corpo adesso.
        IReadOnlyList<string> requested = [.. observationIds.Distinct(StringComparer.Ordinal)];
        IReadOnlyList<ObservationRecord> found =
            await observations.ReadAuthorizedAsync(scope, requested, cancellationToken).ConfigureAwait(false);
        Dictionary<string, ObservationRecord> byId = found.ToDictionary(record => record.Id, StringComparer.Ordinal);

        List<ObservationRecord> result = [];
        int bytes = 0;

        // Preserve the caller's ranked ID order, not the store's chronological order. A body
        // that does not fit the remaining byte budget is skipped without truncating it; smaller
        // later bodies may still fit. Missing/revoked/duplicate IDs never consume the budget.
        foreach (string observationId in requested)
        {
            if (!byId.TryGetValue(observationId, out ObservationRecord? record)) { continue; }
            int size = Encoding.UTF8.GetByteCount(record.Content);

            if (bytes + size > MemoryLimits.MaxFetchBytes)
            {
                continue;
            }

            bytes += size;
            result.Add(record);
        }

        return result;
    }

    private async ValueTask<(string Key, MemoryIndexSnapshot Snapshot)> LoadAsync(MemoryScope scope, CancellationToken cancellationToken)
    {
        long cacheEpoch = cache.Epoch;
        long generation = await index.ReadGenerationAsync(scope.ProjectId, cancellationToken).ConfigureAwait(false);

        // La generazione entra nella chiave: uno snapshot di una generazione precedente non viene
        // nemmeno cercato, quindi non puo' essere restituito dopo una revoca.
        string fingerprintless = MemoryIndexCache.Key(scope, generation, embeddings?.Fingerprint.ToString());

        if (cache.TryGet(fingerprintless) is MemoryIndexSnapshot cached)
        {
            return (fingerprintless, cached);
        }

        MemoryIndexSnapshot snapshot = await index.LoadIndexAsync(scope, cancellationToken).ConfigureAwait(false);
        snapshot = cache.FilterExpired(snapshot);

        // The store may have advanced while loading: label the actual snapshot generation,
        // never the generation observed before the load.
        fingerprintless = MemoryIndexCache.Key(scope, snapshot.Generation, embeddings?.Fingerprint.ToString());
        cache.Set(fingerprintless, snapshot, cacheEpoch);

        return (fingerprintless, snapshot);
    }

    private async ValueTask<float[]?> EmbedQueryAsync(
        string query,
        MemoryIndexSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (embeddings is null)
        {
            // Nessun modello configurato: la ricerca resta lessicale e lo dichiara.
            return null;
        }

        if (snapshot.Fingerprint is null)
        {
            // L'indice locale non ha ancora vettori: succede all'inizio, e succede quando ogni
            // ricordo autorizzato e' nato per l'indice potenziato, che non esiste piu' (ADR-032). Non e' un guasto del modello — non c'e'
            // nulla con cui confrontare la domanda — e trattarlo come tale bloccherebbe il turno
            // con `memory.required` per una memoria che sta funzionando.
            return null;
        }

        if (!string.Equals(snapshot.Fingerprint, embeddings.Fingerprint.ToString(), StringComparison.Ordinal))
        {
            // Vettori di fingerprint diversi non sono confrontabili: meglio una ricerca lessicale
            // dichiarata che una semantica falsa.
            return null;
        }

        try
        {
            EmbeddingBatch batch = await embeddings.EmbedAsync([query], cancellationToken).ConfigureAwait(false);

            return batch.Vectors.Count == 1 ? batch.Vectors[0] : null;
        }
        catch (PaRouterException)
        {
            // Il modello non risponde: stessa risposta di un modello assente, dichiarata.
            return null;
        }
    }

    private async ValueTask<IReadOnlyList<MemoryHit>> BuildHitsAsync(
        MemoryScope scope,
        IReadOnlyList<(string ObservationId, double Score)> ranked,
        int limit,
        CancellationToken cancellationToken)
    {
        if (ranked.Count == 0)
        {
            return [];
        }

        // One authorized read covers the bounded union of both retrieval lists. Formatting
        // then stays synchronous: no earlier body survives a revocation in a later batch.
        // MaxFetchIds belongs to the public get contract, not this internal store read.
        IReadOnlyList<(string ObservationId, double Score)> candidates = [.. ranked
            .Take(MemoryLimits.CandidatesPerList * 2)
            .DistinctBy(candidate => candidate.ObservationId, StringComparer.Ordinal)];
        IReadOnlyList<ObservationRecord> revalidated = await observations.ReadAuthorizedAsync(
            scope, [.. candidates.Select(candidate => candidate.ObservationId)], cancellationToken).ConfigureAwait(false);
        Dictionary<string, ObservationRecord> byId = revalidated.ToDictionary(record => record.Id, StringComparer.Ordinal);
        List<MemoryHit> hits = [];
        int bytes = 0;

        foreach ((string observationId, double score) in candidates)
        {
            if (hits.Count >= limit) { break; }
            if (!byId.TryGetValue(observationId, out ObservationRecord? record)) { continue; }
            MemoryHit hit = Hit(record, score);
            int size = Encoding.UTF8.GetByteCount(hit.Title) + 96;

            if (bytes + size > MemoryLimits.MaxSearchResultBytes) { break; }
            bytes += size;
            hits.Add(hit);
        }

        return hits;
    }

    private MemoryHit Hit(ObservationRecord record, double score) =>
        new(record.Id, record.Kind, record.Verification, record.CreatedAt, Title(record.Content), score);

    /// <summary>
    /// Titolo breve: la prima riga del testo gia' filtrato, troncata. Non e' uno snippet costruito
    /// attorno alla query, che potrebbe rivelare porzioni scelte dal testo.
    /// </summary>
    private string Title(string content)
    {
        int newline = content.IndexOf('\n', StringComparison.Ordinal);
        string line = (newline < 0 ? content : content[..newline]).Trim();

        return line.Length <= options.TitleLength ? line : line[..options.TitleLength];
    }

    private MemorySearchMode Mode(MemoryIndexSnapshot snapshot) =>
        embeddings is not null && snapshot.Fingerprint is not null
            ? MemorySearchMode.Hybrid
            : MemorySearchMode.LexicalOnly;

    private static MemoryIndexStatus Status(MemoryIndexSnapshot snapshot) =>
        snapshot.Complete ? MemoryIndexStatus.Ready : MemoryIndexStatus.Rebuilding;

    /// <summary>Rilascia la cache creata internamente; una cache fornita resta del chiamante.</summary>
    public void Dispose()
    {
        if (ownsCache) { cache.Dispose(); }
    }
}
