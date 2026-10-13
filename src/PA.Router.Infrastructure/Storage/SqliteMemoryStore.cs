using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Abstractions.Observability;

namespace PA.Router.Infrastructure.Storage;

/// <summary>
/// Memoria locale su SQLite: osservazioni, chunk, vettori e lavori.
///
/// Il testo non tocca mai il disco in chiaro. Il blob cifrato viene scritto prima, poi una sola
/// transazione registra osservazione, chunk e lavoro di indicizzazione: se la transazione fallisce
/// resta un blob orfano — che lo sweeper raccoglie — e non un ricordo a meta'.
///
/// Ogni modifica incrementa <c>projects.memory_generation</c>. E' cio' che rende impossibile
/// rispondere da una cache costruita prima di una revoca: la generazione fa parte della chiave
/// della cache (03-specs/SEMANTIC-SEARCH.md).
/// </summary>
public sealed class SqliteMemoryStore : IObservationStore, IMemoryIndexStore, IMemoryAudit, IExpiringMemory
{
    private const int ObservationSchemaVersion = 1;

    private readonly StoreWriter writer;
    private readonly SqliteConnectionFactory factory;
    private readonly IContentStore content;
    private readonly IClock clock;
    private readonly IIdGenerator ids;
    private readonly Action? invalidateCache;

    public SqliteMemoryStore(
        StoreWriter writer,
        SqliteConnectionFactory factory,
        IContentStore content,
        IClock clock,
        IIdGenerator ids,
        Action? invalidateCache = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ids);

        this.writer = writer;
        this.factory = factory;
        this.content = content;
        this.clock = clock;
        this.ids = ids;
        this.invalidateCache = invalidateCache;

        // La coda dei lavori e' visibile solo da qui: la telemetria la legge quando raccoglie, cosi'
        // nessuno tiene un conteggio parallelo che puo' divergere dal database.
        RouterTelemetry.BindGauge("memory_job_pending", () => CountJobs(MemoryJobState.Pending));
        RouterTelemetry.BindGauge("memory_job_failed", () => CountJobs(MemoryJobState.Failed));
    }

    /// <inheritdoc />
    public async ValueTask<ObservationRecord> AppendAsync(
        ObservationRecord observation,
        IReadOnlyList<MemoryChunk> chunks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count == 0)
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "An observation must produce at least one chunk.");
        }

        ObservationRecord? existing = await FindByEventKeyAsync(
            observation.ProjectId,
            observation.EventKey,
            cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            // Un replay dello stesso evento non e' un fatto nuovo: si restituisce quello che c'e'.
            return existing;
        }

        string payload = Payload(observation);
        ContentBlobRequest request = new(ids.NewId(), observation.SessionId, BlobKind.Observation, ObservationSchemaVersion);

        ContentBlobReceipt receipt = await content
            .WriteAsync(request, Encoding.UTF8.GetBytes(payload), cancellationToken)
            .ConfigureAwait(false);

        long now = clock.UnixTimeMilliseconds;
        long expires = observation.ExpiresAt.ToUnixTimeMilliseconds();
        string projectId = observation.ProjectId.ToString("D", CultureInfo.InvariantCulture);

        bool duplicate = await writer.ExecuteAsync(
            "T-memory-append",
            (connection, transaction) =>
            {
                // La deduplica va decisa qui dentro, non fuori: due ingest simultanei dello stesso
                // evento vedrebbero entrambi «assente» e il secondo violerebbe il vincolo UNIQUE,
                // trasformando un replay in un errore di storage.
                if (ScalarStringOn(
                        connection,
                        transaction,
                        "SELECT id FROM observations WHERE project_id = $project AND event_key = $key;",
                        ("$project", projectId),
                        ("$key", observation.EventKey)) is not null)
                {
                    return true;
                }

                Execute(
                    connection,
                    transaction,
                    "INSERT INTO content_blobs(id, session_id, execution_id, kind, schema_version, relative_path, "
                    + "cipher_sha256, byte_length, created_at_ms, expires_at_ms) "
                    + "VALUES ($id, $session, $execution, $kind, $schema, $path, $digest, $length, $now, $expires);",
                    ("$id", receipt.BlobId),
                    ("$session", observation.SessionId),
                    ("$execution", (object?)observation.ExecutionId ?? DBNull.Value),
                    ("$kind", BlobKind.Observation.ToWire()),
                    ("$schema", ObservationSchemaVersion),
                    ("$path", receipt.RelativePath),
                    ("$digest", receipt.CipherSha256),
                    ("$length", receipt.ByteLength),
                    ("$now", now),
                    ("$expires", expires));

                Execute(
                    connection,
                    transaction,
                    "INSERT INTO observations(id, project_id, session_id, execution_id, event_key, kind, verification, "
                    + "classification, scope, published_for_chatbot, version, content_blob_id, created_at_ms, expires_at_ms, "
                    + "index_destination) "
                    + "VALUES ($id, $project, $session, $execution, $key, $kind, $verification, $classification, $scope, "
                    + "$published, $version, $blob, $created, $expires, $destination);",
                    ("$id", observation.Id),
                    ("$project", projectId),
                    ("$session", observation.SessionId),
                    ("$execution", (object?)observation.ExecutionId ?? DBNull.Value),
                    ("$key", observation.EventKey),
                    ("$kind", observation.Kind.ToWire()),
                    ("$verification", observation.Verification.ToWire()),
                    ("$classification", observation.Classification.ToWire()),
                    ("$scope", observation.Scope.ToWire()),
                    ("$published", observation.PublishedForChatbot ? 1 : 0),
                    ("$version", observation.Version),
                    ("$blob", receipt.BlobId),
                    ("$created", observation.CreatedAt.ToUnixTimeMilliseconds()),
                    ("$expires", expires),
                    // La destinazione si scrive una volta sola, con l'osservazione: i trigger
                    // dello schema la rendono immutabile e vietano `enhanced` a una classe
                    // diversa da `public`. Il divieto vive nello store, non solo nel codice che
                    // lo chiama.
                    ("$destination", observation.Destination.ToWire()));

                foreach (MemoryChunk chunk in chunks)
                {
                    Execute(
                        connection,
                        transaction,
                        "INSERT INTO memory_chunks(id, observation_id, ordinal, content_hmac, start_byte, byte_length) "
                        + "VALUES ($id, $observation, $ordinal, $hmac, $start, $length);",
                        ("$id", chunk.Id),
                        ("$observation", observation.Id),
                        ("$ordinal", chunk.Ordinal),
                        ("$hmac", chunk.ContentHmac),
                        ("$start", chunk.StartByte),
                        ("$length", chunk.ByteLength));
                }

                EnqueueJob(connection, transaction, ids.NewId(), observation, MemoryJobKind.Index, now);
                BumpGeneration(connection, transaction, projectId);

                return false;
            },
            cancellationToken).ConfigureAwait(false);

        if (!duplicate)
        {
            return observation;
        }

        // Il blob era gia' stato scritto: senza riga che lo referenzi va rimosso subito, invece di
        // restare in attesa dello sweeper.
        await content.DeleteAsync(receipt, cancellationToken).ConfigureAwait(false);

        return await FindByEventKeyAsync(observation.ProjectId, observation.EventKey, cancellationToken)
            .ConfigureAwait(false)
            ?? observation;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ObservationRecord>> ReadAuthorizedAsync(
        MemoryScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        List<(ObservationRow Row, ContentBlobReceipt Receipt)> rows = ReadRows(scope, observationIds: null);
        List<ObservationRecord> observations = new(rows.Count);

        foreach ((ObservationRow row, ContentBlobReceipt receipt) in rows)
        {
            // Fra la lettura delle righe e quella dei blob puo' passare una cancellazione: il purge
            // toglie la riga e poi il file, e questa enumerazione ha gia' in mano il riferimento.
            // Un ricordo sparito sotto la lettura e' esattamente cio' che la cancellazione voleva:
            // si salta, non si fa fallire chi stava leggendo.
            //
            // Vale **solo** per il contenuto assente. Un blob presente ma di lunghezza o digest
            // diversi e' corruzione, non cancellazione, e continua a risalire: confondere i due casi
            // significherebbe nascondere un guasto del disco dietro una tolleranza.
            ObservationRecord? materialised;

            try
            {
                materialised = await MaterialiseAsync(row, receipt, cancellationToken).ConfigureAwait(false);
            }
            catch (PaRouterException ex)
                when (ex.Code is ErrorCode.ContentUnavailable
                    && ex.Metadata.TryGetValue("reason", out string? reason)
                    && string.Equals(reason, "missing", StringComparison.Ordinal))
            {
                continue;
            }

            observations.Add(materialised);
        }

        return RevalidateMaterialised(scope, observationIds: null, rows, observations, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ObservationRecord>> ReadAuthorizedAsync(
        MemoryScope scope,
        IReadOnlyList<string> observationIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(observationIds);

        if (observationIds.Count == 0)
        {
            return [];
        }

        List<(ObservationRow Row, ContentBlobReceipt Receipt)> rows = ReadRows(scope, observationIds);
        List<ObservationRecord> observations = new(rows.Count);

        foreach ((ObservationRow row, ContentBlobReceipt receipt) in rows)
        {
            try
            {
                observations.Add(await MaterialiseAsync(row, receipt, cancellationToken).ConfigureAwait(false));
            }
            catch (PaRouterException ex)
                when (ex.Code is ErrorCode.ContentUnavailable
                    && ex.Metadata.TryGetValue("reason", out string? reason)
                    && string.Equals(reason, "missing", StringComparison.Ordinal))
            {
                // A purge during blob I/O is an absent memory, just as in the scoped enumeration.
                // Integrity failures for a present blob still propagate.
            }
        }

        return RevalidateMaterialised(scope, observationIds, rows, observations, cancellationToken);
    }

    private IReadOnlyList<ObservationRecord> RevalidateMaterialised(
        MemoryScope scope,
        IReadOnlyList<string>? observationIds,
        IReadOnlyList<(ObservationRow Row, ContentBlobReceipt Receipt)> originalRows,
        IReadOnlyList<ObservationRecord> materialised,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (materialised.Count == 0) { return []; }

        Dictionary<string, (ObservationRow Row, ContentBlobReceipt Receipt)> original =
            originalRows.ToDictionary(item => item.Row.Id, StringComparer.Ordinal);
        // No await after this authorization check. A blob read may have yielded while an earlier
        // observation was revoked, expired or replaced. Generation alone cannot detect TTL expiry.
        HashSet<string> visible = ReadRows(scope, observationIds)
            .Where(item => original.TryGetValue(item.Row.Id, out var before) && before == item)
            .Select(item => item.Row.Id)
            .ToHashSet(StringComparer.Ordinal);
        return [.. materialised.Where(observation => visible.Contains(observation.Id))];
    }

    /// <inheritdoc />
    public async ValueTask TombstoneAsync(string observationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationId);

        long now = clock.UnixTimeMilliseconds;

        await writer.ExecuteAsync(
            "T-memory-tombstone",
            (connection, transaction) =>
            {
                string? projectId = ScalarStringOn(
                    connection,
                    transaction,
                    "SELECT project_id FROM observations WHERE id = $id AND deleted_at_ms IS NULL;",
                    ("$id", observationId));

                if (projectId is null)
                {
                    // Gia' cancellata o inesistente: la rimozione e' idempotente e non rivela quale
                    // dei due casi sia.
                    return 0;
                }

                Execute(
                    connection,
                    transaction,
                    "UPDATE observations SET deleted_at_ms = $now WHERE id = $id;",
                    ("$id", observationId),
                    ("$now", now));

                // Il vettore smette di essere utilizzabile subito, anche se il purge deve ancora
                // girare: un tombstone che aspettasse il job lascerebbe risuscitare il ricordo.
                Execute(
                    connection,
                    transaction,
                    "UPDATE embeddings SET state = 'superseded' WHERE chunk_id IN "
                    + "(SELECT id FROM memory_chunks WHERE observation_id = $id);",
                    ("$id", observationId));

                int version = Scalar(
                    connection,
                    transaction,
                    "SELECT version FROM observations WHERE id = $id;",
                    ("$id", observationId));

                EnqueuePurge(connection, transaction, ids.NewId(), observationId, version, now);
                BumpGeneration(connection, transaction, projectId);

                return 0;
            },
            cancellationToken).ConfigureAwait(false);

        // The commit succeeded: release retained plaintext/indices before reporting success.
        invalidateCache?.Invoke();
    }

    /// <inheritdoc />
    public async ValueTask<int> EnqueueReindexAsync(string modelFingerprint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFingerprint);

        long now = clock.UnixTimeMilliseconds;

        return await writer.ExecuteAsync(
            "T-memory-reindex",
            (connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;

                // Una riga per osservazione viva che non ha gia' un lavoro con questo fingerprint.
                // Il vincolo UNIQUE dello schema copre la ripetizione: eseguire due volte il
                // comando non crea due code.
                command.CommandText =
                    "INSERT OR IGNORE INTO memory_jobs("
                    + "id, observation_id, observation_version, model_fingerprint, kind, state, attempts, available_at_ms) "
                    + "SELECT lower(hex(randomblob(16))), o.id, o.version, $fingerprint, 'index', 'pending', 0, $now "
                    + "FROM observations o "
                    + "WHERE o.deleted_at_ms IS NULL "
                    // I ricordi instradati alla memoria potenziata hanno un indice loro, con
                    // il proprio modello: accodarli qui produrrebbe lavori che falliscono per
                    // disallineamento di fingerprint e bloccherebbero la promozione di quello
                    // locale.
                    + "AND o.index_destination = 'local' "
                    + "AND NOT EXISTS ("
                    + "  SELECT 1 FROM memory_jobs j "
                    + "  WHERE j.observation_id = o.id AND j.observation_version = o.version "
                    + "  AND j.model_fingerprint = $fingerprint AND j.kind = 'index');";

                command.Parameters.AddWithValue("$fingerprint", modelFingerprint);
                command.Parameters.AddWithValue("$now", now);

                return command.ExecuteNonQuery();
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<int> PromoteFingerprintAsync(string modelFingerprint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFingerprint);

        return await writer.ExecuteAsync(
            "T-memory-promote",
            (connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;

                // Un progetto si promuove quando ha almeno un'osservazione indicizzata con il
                // fingerprint nuovo e non ha piu' nulla in sospeso per quello stesso fingerprint.
                command.CommandText =
                    "UPDATE projects SET active_embedding_fingerprint = $fingerprint, "
                    + "memory_generation = memory_generation + 1 "
                    + "WHERE active_embedding_fingerprint IS NOT $fingerprint "
                    + "AND EXISTS (SELECT 1 FROM observations o JOIN memory_jobs j ON j.observation_id = o.id "
                    + "  WHERE o.project_id = projects.id AND j.model_fingerprint = $fingerprint "
                    + "  AND j.kind = 'index' AND j.state = 'completed') "
                    + "AND NOT EXISTS (SELECT 1 FROM observations o JOIN memory_jobs j ON j.observation_id = o.id "
                    + "  WHERE o.project_id = projects.id AND j.model_fingerprint = $fingerprint "
                    + "  AND j.kind = 'index' AND j.state IN ('pending','running','failed'));";

                command.Parameters.AddWithValue("$fingerprint", modelFingerprint);

                return command.ExecuteNonQuery();
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MemoryJob?> LeaseJobAsync(TimeSpan lease, CancellationToken cancellationToken)
    {
        if (lease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lease));
        }

        long now = clock.UnixTimeMilliseconds;
        long expires = now + (long)lease.TotalMilliseconds;

        return await writer.ExecuteAsync(
            "T-memory-lease",
            (connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;

                // Un lavoro in esecuzione con lease scaduto e' un worker morto: torna prendibile,
                // altrimenti resterebbe running per sempre.
                command.CommandText =
                    "SELECT j.id, j.observation_id, j.observation_version, j.model_fingerprint, j.kind, j.state, "
                    + "j.attempts, j.available_at_ms, j.lease_expires_at_ms, j.error_code, o.index_destination "
                    + "FROM memory_jobs j JOIN observations o ON o.id = j.observation_id "
                    + "WHERE j.available_at_ms <= $now AND (j.state = 'pending' "
                    + "OR (j.state = 'running' AND j.lease_expires_at_ms IS NOT NULL AND j.lease_expires_at_ms <= $now)) "
                    + "ORDER BY j.available_at_ms, j.id LIMIT 1;";

                command.Parameters.AddWithValue("$now", now);

                MemoryJob? job = null;

                using (SqliteDataReader reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        job = ReadJob(reader);
                    }
                }

                if (job is null)
                {
                    return null;
                }

                int attempts = job.Attempts + 1;

                if (attempts > MemoryLimits.MaxJobAttempts)
                {
                    // Il CHECK dello schema si ferma a 3: oltre, il lavoro e' fallito e resta tale.
                    Execute(
                        connection,
                        transaction,
                        "UPDATE memory_jobs SET state = 'failed', lease_expires_at_ms = NULL, "
                        + "error_code = COALESCE(error_code, $code) WHERE id = $id;",
                        ("$id", job.Id),
                        ("$code", ErrorCode.MemoryIndexFailed.ToWire()));

                    return null;
                }

                Execute(
                    connection,
                    transaction,
                    "UPDATE memory_jobs SET state = 'running', attempts = $attempts, lease_expires_at_ms = $lease "
                    + "WHERE id = $id;",
                    ("$id", job.Id),
                    ("$attempts", attempts),
                    ("$lease", expires));

                return job with
                {
                    State = MemoryJobState.Running,
                    Attempts = attempts,
                    LeaseExpiresAtMs = expires,
                };
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MemoryChunk>> ReadChunksAsync(
        string observationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationId);

        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT c.id, c.ordinal, c.content_hmac, c.start_byte, c.byte_length, o.session_id, "
            + "b.id, b.relative_path, b.cipher_sha256, b.byte_length "
            + "FROM memory_chunks c JOIN observations o ON o.id = c.observation_id "
            + "JOIN content_blobs b ON b.id = o.content_blob_id "
            + "WHERE c.observation_id = $id AND o.deleted_at_ms IS NULL ORDER BY c.ordinal;";

        command.Parameters.AddWithValue("$id", observationId);

        List<(string Id, int Ordinal, string Hmac, int Start, int Length)> rows = [];
        string? sessionId = null;
        ContentBlobReceipt receipt = default;

        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4)));
                sessionId = reader.GetString(5);
                receipt = new ContentBlobReceipt(reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetInt64(9));
            }
        }

        if (rows.Count == 0 || sessionId is null)
        {
            return [];
        }

        string payload = await ReadContentAsync(sessionId, receipt, cancellationToken).ConfigureAwait(false);

        // Il blob contiene il documento dell'osservazione; gli offset dei chunk si riferiscono al
        // testo, non al JSON che lo trasporta. Confonderli significherebbe indicizzare le virgolette.
        byte[] bytes = Encoding.UTF8.GetBytes(ParsePayload(payload).Content);

        List<MemoryChunk> chunks = new(rows.Count);

        foreach ((string id, int ordinal, string hmac, int start, int length) in rows)
        {
            chunks.Add(new MemoryChunk(
                id,
                observationId,
                ordinal,
                hmac,
                start,
                length,
                Encoding.UTF8.GetString(bytes, start, Math.Min(length, bytes.Length - start))));
        }

        return chunks;
    }

    /// <inheritdoc />
    public async ValueTask CompleteIndexAsync(
        MemoryJob job,
        IReadOnlyList<ChunkVector> vectors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(vectors);

        long now = clock.UnixTimeMilliseconds;
        List<(ChunkVector Vector, ContentBlobRequest Request, ContentBlobReceipt Receipt)> written = [];

        string? sessionId = SessionOf(job.ObservationId);

        if (sessionId is null)
        {
            // L'osservazione e' sparita mentre il lavoro girava: non si scrive un vettore orfano.
            await FailJobAsync(job, ErrorCode.MemoryIndexFailed, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (ChunkVector vector in vectors)
        {
            ContentBlobRequest request = new(ids.NewId(), sessionId, BlobKind.Embedding, ObservationSchemaVersion);

            ContentBlobReceipt receipt = await content
                .WriteAsync(request, VectorBytes(vector.Vector), cancellationToken)
                .ConfigureAwait(false);

            written.Add((vector, request, receipt));
        }

        long expires = ObservationExpiry(job.ObservationId) ?? (now + (long)TimeSpan.FromDays(1).TotalMilliseconds);

        await writer.ExecuteAsync(
            "T-memory-index",
            (connection, transaction) =>
            {
                foreach ((ChunkVector vector, ContentBlobRequest request, ContentBlobReceipt receipt) in written)
                {
                    Execute(
                        connection,
                        transaction,
                        "INSERT INTO content_blobs(id, session_id, execution_id, kind, schema_version, relative_path, "
                        + "cipher_sha256, byte_length, created_at_ms, expires_at_ms) "
                        + "VALUES ($id, $session, NULL, $kind, $schema, $path, $digest, $length, $now, $expires);",
                        ("$id", receipt.BlobId),
                        ("$session", request.SessionId),
                        ("$kind", BlobKind.Embedding.ToWire()),
                        ("$schema", ObservationSchemaVersion),
                        ("$path", receipt.RelativePath),
                        ("$digest", receipt.CipherSha256),
                        ("$length", receipt.ByteLength),
                        ("$now", now),
                        ("$expires", expires));

                    Execute(
                        connection,
                        transaction,
                        "INSERT INTO embeddings(chunk_id, model_fingerprint, dimension, vector_blob_id, state) "
                        + "VALUES ($chunk, $fingerprint, $dimension, $blob, 'ready') "
                        + "ON CONFLICT(chunk_id, model_fingerprint) DO UPDATE SET "
                        + "dimension = excluded.dimension, vector_blob_id = excluded.vector_blob_id, state = 'ready';",
                        ("$chunk", vector.ChunkId),
                        ("$fingerprint", job.ModelFingerprint),
                        ("$dimension", vector.Vector.Length),
                        ("$blob", receipt.BlobId));
                }

                Execute(
                    connection,
                    transaction,
                    "UPDATE memory_jobs SET state = 'completed', lease_expires_at_ms = NULL, error_code = NULL "
                    + "WHERE id = $id;",
                    ("$id", job.Id));

                // Il fingerprint attivo identifica l'indice **locale**: e' quello con cui la
                // ricerca confronta la domanda. Un lavoro della memoria potenziata appartiene a un
                // altro indice e non deve fissarlo — se lo facesse, la ricerca locale troverebbe
                // un fingerprint che il proprio provider non produce e fallirebbe per
                // disallineamento, bloccando il turno con `memory.required`.
                Execute(
                    connection,
                    transaction,
                    "UPDATE projects SET active_embedding_fingerprint = COALESCE(active_embedding_fingerprint, $fingerprint) "
                    + "WHERE id = (SELECT project_id FROM observations WHERE id = $observation) "
                    + "AND (SELECT index_destination FROM observations WHERE id = $observation) = 'local';",
                    ("$fingerprint", job.ModelFingerprint),
                    ("$observation", job.ObservationId));

                BumpGenerationOf(connection, transaction, job.ObservationId);

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DemoteToLocalAsync(MemoryJob job, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        long now = clock.UnixTimeMilliseconds;
        string eventId = ids.NewId();

        await writer.ExecuteAsync(
            "T-memory-demote",
            (connection, transaction) =>
            {
                // La destinazione scende: il trigger dello schema ammette questa sola direzione.
                Execute(
                    connection,
                    transaction,
                    "UPDATE observations SET index_destination = 'local' WHERE id = $observation;",
                    ("$observation", job.ObservationId));

                // Lo stesso lavoro torna disponibile con i tentativi azzerati: adesso la sua
                // osservazione e' locale, quindi il prossimo giro lo esegue il provider locale.
                // Inserirne uno nuovo non funzionerebbe: la chiave di unicita' e' la stessa, e
                // l'inserimento verrebbe ignorato lasciando il ricordo senza indice.
                Execute(
                    connection,
                    transaction,
                    "UPDATE memory_jobs SET state = 'pending', attempts = 0, lease_expires_at_ms = NULL, "
                    + "available_at_ms = $now, model_fingerprint = 'pending', error_code = $code WHERE id = $id;",
                    ("$id", job.Id),
                    ("$now", now),
                    ("$code", reason));

                // La destinazione cambia: l'audit lo registra, altrimenti resterebbe un ricordo
                // che ha cambiato indice senza che nessuno possa vederlo.
                Execute(
                    connection,
                    transaction,
                    "INSERT INTO audit_events(event_id, execution_id, attempt_id, code, metadata_json, created_at_ms) "
                    + "VALUES ($event, NULL, NULL, $code, $metadata, $now);",
                    ("$event", eventId),
                    ("$code", MemoryAuditCodes.Routed),
                    ("$metadata", MetadataJson(new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["destination"] = MemoryDestination.Local.ToWire(),
                        ["reason"] = reason,
                        ["observationId"] = job.ObservationId,
                    })),
                    ("$now", now));

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask CompletePurgeAsync(MemoryJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        List<ContentBlobReceipt> vectorBlobs = VectorBlobsOf(job.ObservationId);
        ContentBlobReceipt? textBlob = TextBlobOf(job.ObservationId);

        await writer.ExecuteAsync(
            "T-memory-purge",
            (connection, transaction) =>
            {
                Execute(
                    connection,
                    transaction,
                    "DELETE FROM embeddings WHERE chunk_id IN (SELECT id FROM memory_chunks WHERE observation_id = $id);",
                    ("$id", job.ObservationId));

                Execute(
                    connection,
                    transaction,
                    "DELETE FROM memory_chunks WHERE observation_id = $id;",
                    ("$id", job.ObservationId));

                foreach (ContentBlobReceipt receipt in vectorBlobs)
                {
                    Execute(
                        connection,
                        transaction,
                        "DELETE FROM content_blobs WHERE id = $id;",
                        ("$id", receipt.BlobId));
                }

                // Cancellare un ricordo vuol dire toglierlo: i suoi lavori, la sua riga e il suo testo cifrato. Prima
                // restavano, e un ricordo «rimosso» continuava a occupare il disco con il proprio contenuto.
                Execute(
                    connection,
                    transaction,
                    "DELETE FROM memory_jobs WHERE observation_id = $id;",
                    ("$id", job.ObservationId));

                Execute(
                    connection,
                    transaction,
                    "DELETE FROM observations WHERE id = $id;",
                    ("$id", job.ObservationId));

                if (textBlob is ContentBlobReceipt text)
                {
                    Execute(
                        connection,
                        transaction,
                        "DELETE FROM content_blobs WHERE id = $id;",
                        ("$id", text.BlobId));
                }

                return 0;
            },
            cancellationToken).ConfigureAwait(false);

        // I file spariscono dopo il commit: se il processo muore qui restano orfani, che lo sweeper raccoglie.
        // L'ordine inverso lascerebbe righe che puntano a file assenti.
        invalidateCache?.Invoke();
        foreach (ContentBlobReceipt receipt in vectorBlobs)
        {
            await content.DeleteAsync(receipt, cancellationToken).ConfigureAwait(false);
        }

        if (textBlob is ContentBlobReceipt body)
        {
            await content.DeleteAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> TombstoneExpiredAsync(CancellationToken cancellationToken)
    {
        long now = clock.UnixTimeMilliseconds;

        List<string> expired = [];

        using (SqliteConnection connection = factory.Open())
        {
            using SqliteCommand command = connection.CreateCommand();

            command.CommandText =
                "SELECT id FROM observations WHERE deleted_at_ms IS NULL AND expires_at_ms <= $now ORDER BY id;";

            command.Parameters.AddWithValue("$now", now);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                expired.Add(reader.GetString(0));
            }
        }

        foreach (string observationId in expired)
        {
            // Stessa strada di una cancellazione chiesta dall'operatore: invisibile subito, rimosso dal lavoro in coda.
            await TombstoneAsync(observationId, cancellationToken).ConfigureAwait(false);
        }

        return expired.Count;
    }

    /// <inheritdoc />
    public async ValueTask<int> TombstoneSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        List<string> born = [];

        using (SqliteConnection connection = factory.Open())
        {
            using SqliteCommand command = connection.CreateCommand();

            command.CommandText =
                "SELECT id FROM observations WHERE deleted_at_ms IS NULL AND session_id = $session ORDER BY id;";

            command.Parameters.AddWithValue("$session", sessionId);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                born.Add(reader.GetString(0));
            }
        }

        foreach (string observationId in born)
        {
            await TombstoneAsync(observationId, cancellationToken).ConfigureAwait(false);
        }

        return born.Count;
    }

    /// <inheritdoc />
    public async ValueTask FailJobAsync(
        MemoryJob job,
        ErrorCode errorCode,
        TimeSpan retryAfter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        long now = clock.UnixTimeMilliseconds;
        bool retryable = job.Attempts < MemoryLimits.MaxJobAttempts && retryAfter > TimeSpan.Zero;

        await writer.ExecuteAsync(
            "T-memory-job-failed",
            (connection, transaction) =>
            {
                Execute(
                    connection,
                    transaction,
                    "UPDATE memory_jobs SET state = $state, lease_expires_at_ms = NULL, available_at_ms = $available, "
                    + "error_code = $code WHERE id = $id;",
                    ("$id", job.Id),
                    ("$state", retryable ? MemoryJobState.Pending.ToWire() : MemoryJobState.Failed.ToWire()),
                    ("$available", retryable ? now + (long)retryAfter.TotalMilliseconds : now),
                    ("$code", errorCode.ToWire()));

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MemoryIndexSnapshot> LoadIndexAsync(MemoryScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        string projectId = scope.ProjectId.ToString("D", CultureInfo.InvariantCulture);
        long generation;
        string? fingerprint;

        using (SqliteConnection connection = factory.Open())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT memory_generation, active_embedding_fingerprint FROM projects WHERE id = $id;";
            command.Parameters.AddWithValue("$id", projectId);

            using SqliteDataReader reader = command.ExecuteReader();

            if (!reader.Read())
            {
                return new MemoryIndexSnapshot(0, null, [], [], Complete: true);
            }

            generation = reader.GetInt64(0);
            fingerprint = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        IReadOnlyList<ObservationRecord> observations =
            await ReadAuthorizedAsync(scope, cancellationToken).ConfigureAwait(false);

        if (observations.Count == 0)
        {
            return new MemoryIndexSnapshot(generation, fingerprint, [], [], Complete: true);
        }

        List<MemoryIndexEntry> entries = [];
        bool complete = true;

        foreach (ObservationRecord observation in observations)
        {
            IReadOnlyList<MemoryChunk> chunks =
                await ReadChunksAsync(observation.Id, cancellationToken).ConfigureAwait(false);

            // I ricordi instradati alla memoria potenziata hanno i vettori sotto un altro modello:
            // entrano comunque nell'indice, perche' il loro testo e' qui e resta cercabile in modo
            // lessicale, ma la loro assenza di vettori **locali** non rende incompleto questo
            // indice — non ci sono mai stati e non ci si aspetta che ci siano.
            bool elsewhere = observation.Destination is MemoryDestination.Enhanced;

            foreach (MemoryChunk chunk in chunks)
            {
                float[]? vector = fingerprint is null || elsewhere
                    ? null
                    : await ReadVectorAsync(chunk.Id, fingerprint, observation.SessionId, cancellationToken)
                        .ConfigureAwait(false);

                complete &= vector is not null || elsewhere;

                entries.Add(new MemoryIndexEntry(chunk.Id, observation.Id, chunk.Ordinal, chunk.Text, vector));
            }
        }

        if (entries.Count > MemoryLimits.MaxChunksPerProject)
        {
            // Nessuna cancellazione automatica per far entrare i nuovi dati: e' manutenzione.
            throw new PaRouterException(ErrorCode.MemoryLimit, "The project exceeds the declared chunk budget.")
                .WithMetadata("chunks", entries.Count.ToString(CultureInfo.InvariantCulture))
                .WithMetadata("maxChunks", MemoryLimits.MaxChunksPerProject.ToString(CultureInfo.InvariantCulture));
        }

        return new MemoryIndexSnapshot(generation, fingerprint, entries, observations, complete);
    }

    /// <inheritdoc />
    public async ValueTask RecordAsync(
        string code,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(metadata);

        long now = clock.UnixTimeMilliseconds;
        string eventId = ids.NewId();
        string json = MetadataJson(metadata);

        await writer.ExecuteAsync(
            "T-memory-audit",
            (connection, transaction) =>
            {
                // execution_id resta NULL: l'elaborazione ausiliaria non appartiene a un turno, ma
                // resta un'invocazione di un modello su dati dell'utente e va tracciata.
                Execute(
                    connection,
                    transaction,
                    "INSERT INTO audit_events(event_id, execution_id, attempt_id, code, metadata_json, created_at_ms) "
                    + "VALUES ($event, NULL, NULL, $code, $metadata, $now);",
                    ("$event", eventId),
                    ("$code", code),
                    ("$metadata", json),
                    ("$now", now));

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Eventi di memoria registrati, per diagnosi e prove.</summary>
    public IReadOnlyList<(string Code, string MetadataJson)> ReadMemoryEvents()
    {
        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT code, metadata_json FROM audit_events WHERE execution_id IS NULL ORDER BY sequence;";

        List<(string, string)> events = [];

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            events.Add((reader.GetString(0), reader.GetString(1)));
        }

        return events;
    }

    /// <summary>
    /// Metadati come JSON di sole coppie allowlisted dal chiamante: nessun testo, nessun vettore.
    /// </summary>
    private static string MetadataJson(IReadOnlyDictionary<string, string> metadata)
    {
        System.Text.Json.Nodes.JsonObject payload = [];

        foreach ((string key, string value) in metadata)
        {
            payload[key] = value;
        }

        return payload.ToJsonString();
    }

    /// <summary>Numero di lavori in uno stato, per le diagnosi e per i test di recovery.</summary>
    /// <summary>
    /// Ultimo codice d'errore registrato su un lavoro di indicizzazione non concluso, se esiste.
    ///
    /// Un lavoro che fallisce e riprova resta in attesa: contarlo fra quelli pendenti fa dire «indice in
    /// ricostruzione» a un indice che non si sta costruendo affatto. Il codice dice perche', e senza di esso
    /// l'operatore vede solo una parola ottimista.
    ///
    /// **Insieme al codice torna il momento**, che e' la meta' che mancava: un guasto registrato prima che
    /// l'operatore correggesse la configurazione letto come se fosse di adesso fa credere rotto cio' che e' gia'
    /// stato sistemato. La coda riprova al turno successivo, non alla lettura.
    /// </summary>
    public (string Code, DateTimeOffset At)? LastJobError()
    {
        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT error_code, available_at_ms FROM memory_jobs WHERE error_code IS NOT NULL AND state <> $done ORDER BY available_at_ms DESC LIMIT 1;";
        command.Parameters.AddWithValue("$done", MemoryJobState.Completed.ToWire());

        using SqliteDataReader reader = command.ExecuteReader();

        return reader.Read()
            ? (reader.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)))
            : null;
    }

    /// <inheritdoc />
    public int CountJobs(MemoryJobState state)
    {
        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM memory_jobs WHERE state = $state;";
        command.Parameters.AddWithValue("$state", state.ToWire());

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public ValueTask<long> ReadGenerationAsync(Guid projectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(ReadGeneration(projectId));
    }

    /// <summary>Generazione corrente della memoria di un progetto.</summary>
    public long ReadGeneration(Guid projectId)
    {
        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT memory_generation FROM projects WHERE id = $id;";
        command.Parameters.AddWithValue("$id", projectId.ToString("D", CultureInfo.InvariantCulture));

        object? value = command.ExecuteScalar();

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async ValueTask<ObservationRecord?> FindByEventKeyAsync(
        Guid projectId,
        string eventKey,
        CancellationToken cancellationToken)
    {
        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = Selection + "WHERE o.project_id = $project AND o.event_key = $key;";
        command.Parameters.AddWithValue("$project", projectId.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$key", eventKey);

        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        (ObservationRow row, ContentBlobReceipt receipt) = ReadRow(reader);

        return await MaterialiseAsync(row, receipt, cancellationToken).ConfigureAwait(false);
    }

    private const string Selection =
        "SELECT o.id, o.project_id, o.session_id, o.execution_id, o.event_key, o.kind, o.verification, "
        + "o.classification, o.scope, o.published_for_chatbot, o.version, o.created_at_ms, o.expires_at_ms, "
        + "b.id, b.relative_path, b.cipher_sha256, b.byte_length, o.index_destination "
        + "FROM observations o JOIN content_blobs b ON b.id = o.content_blob_id ";

    private List<(ObservationRow Row, ContentBlobReceipt Receipt)> ReadRows(
        MemoryScope scope,
        IReadOnlyList<string>? observationIds)
    {
        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        StringBuilder sql = new(Selection);

        // Il filtro d'ambito e' parte della query, non un controllo successivo: un ranking globale
        // seguito da un filtro finale lascerebbe passare troppo, prima o poi.
        sql.Append("WHERE o.project_id = $project AND o.deleted_at_ms IS NULL AND o.expires_at_ms > $now ");
        sql.Append("AND (o.session_id = $session");

        if (scope.IncludeProjectScope)
        {
            sql.Append(" OR (o.scope = 'project'");

            if (scope.RequirePublishedForChatbot)
            {
                sql.Append(" AND o.published_for_chatbot = 1");
            }

            sql.Append(')');
        }

        sql.Append(") AND o.classification IN (");
        sql.Append(string.Join(',', AllowedClassifications(scope).Select(value => "'" + value + "'")));
        sql.Append(") ");

        if (observationIds is not null)
        {
            sql.Append("AND o.id IN (");
            sql.Append(string.Join(',', Enumerable.Range(0, observationIds.Count).Select(index => "$id" + index.ToString(CultureInfo.InvariantCulture))));
            sql.Append(") ");

            for (int index = 0; index < observationIds.Count; index++)
            {
                command.Parameters.AddWithValue("$id" + index.ToString(CultureInfo.InvariantCulture), observationIds[index]);
            }
        }

        sql.Append("ORDER BY o.created_at_ms, o.id;");

        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$project", scope.ProjectId.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$session", scope.SessionId);
        command.Parameters.AddWithValue("$now", clock.UnixTimeMilliseconds);

        List<(ObservationRow, ContentBlobReceipt)> rows = [];

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    /// <summary>
    /// Classi leggibili nell'ambito. Il chatbot non vede personal e sensitive nemmeno della propria
    /// sessione: la sua memoria e' l'eccezione, non il caso normale.
    /// </summary>
    private static IReadOnlyList<string> AllowedClassifications(MemoryScope scope) =>
        [.. Enum.GetValues<DataClass>()
            .Where(value => value <= scope.MaxClassification && value is not (DataClass.Secret or DataClass.Unknown))
            .Select(value => value.ToWire())];

    private static (ObservationRow Row, ContentBlobReceipt Receipt) ReadRow(SqliteDataReader reader)
    {
        ObservationRow row = new(
            reader.GetString(0),
            Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            WireEnum.Parse<ObservationKind>(reader.GetString(5)),
            WireEnum.Parse<Verification>(reader.GetString(6)),
            WireEnum.Parse<DataClass>(reader.GetString(7)),
            WireEnum.Parse<MemoryScopeKind>(reader.GetString(8)),
            reader.GetInt32(9) == 1,
            reader.GetInt32(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            WireEnum.Parse<MemoryDestination>(reader.GetString(17)));

        ContentBlobReceipt receipt = new(
            reader.GetString(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetInt64(16));

        return (row, receipt);
    }

    private async ValueTask<ObservationRecord> MaterialiseAsync(
        ObservationRow row,
        ContentBlobReceipt receipt,
        CancellationToken cancellationToken)
    {
        string payload = await ReadContentAsync(row.SessionId, receipt, cancellationToken).ConfigureAwait(false);
        (string content, IReadOnlyList<string> evidence, string? supersedes) = ParsePayload(payload);

        return new ObservationRecord(
            row.Id,
            row.ProjectId,
            row.SessionId,
            row.ExecutionId,
            row.EventKey,
            row.Kind,
            row.Verification,
            row.Classification,
            row.Scope,
            row.PublishedForChatbot,
            row.Version,
            content,
            evidence,
            supersedes,
            DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtMs),
            DateTimeOffset.FromUnixTimeMilliseconds(row.ExpiresAtMs),
            row.Destination);
    }

    private async ValueTask<string> ReadContentAsync(
        string sessionId,
        ContentBlobReceipt receipt,
        CancellationToken cancellationToken)
    {
        byte[] plaintext = await content
            .ReadAsync(
                new ContentBlobRequest(receipt.BlobId, sessionId, BlobKind.Observation, ObservationSchemaVersion),
                receipt,
                cancellationToken)
            .ConfigureAwait(false);

        return Encoding.UTF8.GetString(plaintext);
    }

    private async ValueTask<float[]?> ReadVectorAsync(
        string chunkId,
        string fingerprint,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ContentBlobReceipt receipt;
        int dimension;

        using (SqliteConnection connection = factory.Open())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT e.dimension, b.id, b.relative_path, b.cipher_sha256, b.byte_length "
                + "FROM embeddings e JOIN content_blobs b ON b.id = e.vector_blob_id "
                + "WHERE e.chunk_id = $chunk AND e.model_fingerprint = $fingerprint AND e.state = 'ready';";

            command.Parameters.AddWithValue("$chunk", chunkId);
            command.Parameters.AddWithValue("$fingerprint", fingerprint);

            using SqliteDataReader reader = command.ExecuteReader();

            if (!reader.Read())
            {
                return null;
            }

            dimension = reader.GetInt32(0);
            receipt = new ContentBlobReceipt(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4));
        }

        byte[] plaintext = await content
            .ReadAsync(
                new ContentBlobRequest(receipt.BlobId, sessionId, BlobKind.Embedding, ObservationSchemaVersion),
                receipt,
                cancellationToken)
            .ConfigureAwait(false);

        float[] vector = new float[dimension];
        Buffer.BlockCopy(plaintext, 0, vector, 0, Math.Min(plaintext.Length, dimension * sizeof(float)));

        return vector;
    }

    private string? SessionOf(string observationId) =>
        ScalarString("SELECT session_id FROM observations WHERE id = $id;", ("$id", observationId));

    private long? ObservationExpiry(string observationId)
    {
        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT expires_at_ms FROM observations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", observationId);

        object? value = command.ExecuteScalar();

        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Blob del testo di un'osservazione, se la riga esiste ancora.</summary>
    private ContentBlobReceipt? TextBlobOf(string observationId)
    {
        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT b.id, b.relative_path, b.cipher_sha256, b.byte_length "
            + "FROM observations o JOIN content_blobs b ON b.id = o.content_blob_id WHERE o.id = $id;";

        command.Parameters.AddWithValue("$id", observationId);

        using SqliteDataReader reader = command.ExecuteReader();

        return reader.Read()
            ? new ContentBlobReceipt(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3))
            : null;
    }

    private List<ContentBlobReceipt> VectorBlobsOf(string observationId)
    {
        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT b.id, b.relative_path, b.cipher_sha256, b.byte_length "
            + "FROM embeddings e JOIN content_blobs b ON b.id = e.vector_blob_id "
            + "JOIN memory_chunks c ON c.id = e.chunk_id WHERE c.observation_id = $id;";

        command.Parameters.AddWithValue("$id", observationId);

        List<ContentBlobReceipt> receipts = [];

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            receipts.Add(new ContentBlobReceipt(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        }

        return receipts;
    }

    private static byte[] VectorBytes(float[] vector)
    {
        byte[] bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);

        return bytes;
    }

    /// <summary>
    /// Corpo cifrato dell'osservazione. Contiene anche <c>supersedes</c>, che lo schema non ha
    /// come colonna: <c>observations</c> ammette una sola riga per chiave di evento, quindi la
    /// catena delle versioni vive nel payload invece che in una colonna inventata.
    /// </summary>
    private static string Payload(ObservationRecord observation)
    {
        System.Text.Json.Nodes.JsonObject payload = new()
        {
            ["schemaVersion"] = ObservationSchemaVersion,
            ["content"] = observation.Content,
            ["supersedes"] = observation.Supersedes,
        };

        System.Text.Json.Nodes.JsonArray evidence = [];

        foreach (string reference in observation.EvidenceRefs)
        {
            evidence.Add(reference);
        }

        payload["evidenceRefs"] = evidence;

        return payload.ToJsonString();
    }

    private static (string Content, IReadOnlyList<string> Evidence, string? Supersedes) ParsePayload(string payload)
    {
        System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(payload)
            ?? throw new PaRouterException(ErrorCode.ContentUnavailable, "The stored observation is not readable.");

        string content = node["content"]?.GetValue<string>()
            ?? throw new PaRouterException(ErrorCode.ContentUnavailable, "The stored observation has no content.");

        List<string> evidence = [];

        if (node["evidenceRefs"] is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (System.Text.Json.Nodes.JsonNode? item in array)
            {
                if (item is not null)
                {
                    evidence.Add(item.GetValue<string>());
                }
            }
        }

        return (content, evidence, node["supersedes"]?.GetValue<string>());
    }

    private static void EnqueueJob(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        ObservationRecord observation,
        MemoryJobKind kind,
        long now) =>
        Execute(
            connection,
            transaction,
            "INSERT OR IGNORE INTO memory_jobs(id, observation_id, observation_version, model_fingerprint, kind, "
            + "state, attempts, available_at_ms) VALUES ($id, $observation, $version, $fingerprint, $kind, 'pending', 0, $now);",
            ("$id", jobId),
            ("$observation", observation.Id),
            ("$version", observation.Version),
            // Il fingerprint attivo del progetto, o il segnaposto finche' nessun modello ha ancora
            // indicizzato: il lavoro esiste comunque, cosi' l'osservazione risulta pending e non
            // silenziosamente assente.
            //
            // Un ricordo instradato alla memoria potenziata non appartiene all'indice locale: il
            // suo fingerprint e' quello dell'altro modello e qui non e' noto. Parte come
            // segnaposto, e il lavoro lo chiude con il fingerprint del provider che lo esegue.
            // Ereditare quello locale lo farebbe fallire per disallineamento senza rimedio.
            ("$fingerprint", observation.Destination is MemoryDestination.Enhanced
                ? "pending"
                : ActiveFingerprint(connection, transaction, observation.ProjectId) ?? "pending"),
            ("$kind", kind.ToWire()),
            ("$now", now));

    private static void EnqueuePurge(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        string observationId,
        int version,
        long now) =>
        Execute(
            connection,
            transaction,
            "INSERT OR IGNORE INTO memory_jobs(id, observation_id, observation_version, model_fingerprint, kind, "
            + "state, attempts, available_at_ms) SELECT $id, $observation, $version, "
            + "COALESCE((SELECT active_embedding_fingerprint FROM projects p "
            + "JOIN observations o ON o.project_id = p.id WHERE o.id = $observation), 'pending'), 'purge', 'pending', 0, $now;",
            ("$id", jobId),
            ("$observation", observationId),
            ("$version", version),
            ("$now", now));

    private static string? ActiveFingerprint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId) =>
        ScalarStringOn(
            connection,
            transaction,
            "SELECT active_embedding_fingerprint FROM projects WHERE id = $id;",
            ("$id", projectId.ToString("D", CultureInfo.InvariantCulture)));

    private static void BumpGeneration(SqliteConnection connection, SqliteTransaction transaction, string projectId) =>
        Execute(
            connection,
            transaction,
            "UPDATE projects SET memory_generation = memory_generation + 1 WHERE id = $id;",
            ("$id", projectId));

    private static void BumpGenerationOf(SqliteConnection connection, SqliteTransaction transaction, string observationId) =>
        Execute(
            connection,
            transaction,
            "UPDATE projects SET memory_generation = memory_generation + 1 "
            + "WHERE id = (SELECT project_id FROM observations WHERE id = $id);",
            ("$id", observationId));

    private static MemoryJob ReadJob(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            WireEnum.Parse<MemoryJobKind>(reader.GetString(4)),
            WireEnum.Parse<MemoryJobState>(reader.GetString(5)),
            reader.GetInt32(6),
            reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.FieldCount > 10 ? WireEnum.Parse<MemoryDestination>(reader.GetString(10)) : MemoryDestination.Local);

    /// <summary>Legge una stringa aprendo una connessione di sola lettura.</summary>
    private string? ScalarString(string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection connection = factory.Open();

        return ScalarStringOn(connection, null, sql, parameters);
    }

    private static string? ScalarStringOn(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        object? result = command.ExecuteScalar();

        return result is null or DBNull ? null : (string)result;
    }

    private static int Scalar(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        object? result = command.ExecuteScalar();

        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private sealed record ObservationRow(
        string Id,
        Guid ProjectId,
        string SessionId,
        string? ExecutionId,
        string EventKey,
        ObservationKind Kind,
        Verification Verification,
        DataClass Classification,
        MemoryScopeKind Scope,
        bool PublishedForChatbot,
        int Version,
        long CreatedAtMs,
        long ExpiresAtMs,
        MemoryDestination Destination);
}
