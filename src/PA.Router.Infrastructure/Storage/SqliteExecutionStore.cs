using System.Globalization;
using Microsoft.Data.Sqlite;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Abstractions.Observability;

namespace PA.Router.Infrastructure.Storage;

/// <summary>
/// Persistenza delle esecuzioni sopra il writer seriale.
///
/// Ogni transazione contiene solo operazioni di storage e dura quanto basta a scriverle. Fra T2 e
/// T3 il coordinatore chiama il provider: in quel tempo qui non resta nulla aperto, ed e' il
/// motivo per cui quattro esecuzioni possono attendere insieme senza bloccarsi a vicenda
/// (03-specs/LOCAL-RUNTIME.md, AC-19).
/// </summary>
public sealed class SqliteExecutionStore : IExecutionStore
{
    private readonly StoreWriter writer;
    private readonly IClock clock;

    public SqliteExecutionStore(StoreWriter writer, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(clock);

        this.writer = writer;
        this.clock = clock;
    }

    /// <inheritdoc />
    public async ValueTask EnsureProjectAsync(Guid projectId, string name, CancellationToken cancellationToken)
    {
        long now = clock.UnixTimeMilliseconds;

        await writer.ExecuteAsync(
            "T0-project",
            (connection, transaction) =>
            {
                Execute(
                    connection,
                    transaction,
                    "INSERT OR IGNORE INTO projects(id, root_hmac, name, created_at_ms) "
                    + "VALUES ($id, $root, $name, $now);",
                    ("$id", projectId.ToString("D", CultureInfo.InvariantCulture)),
                    ("$root", "local-" + projectId.ToString("N", CultureInfo.InvariantCulture)),
                    ("$name", name),
                    ("$now", now));

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask CreateSessionAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        long now = clock.UnixTimeMilliseconds;

        await writer.ExecuteAsync(
            "T0-session",
            (connection, transaction) =>
            {
                Execute(
                    connection,
                    transaction,
                    "INSERT INTO sessions(id, profile_id, channel, state, content_mode, token_hmac, "
                    + "created_at_ms, expires_at_ms, project_id) "
                    + "VALUES ($id, $profile, $channel, 'active', $mode, $token, $created, $expires, $project);",
                    ("$id", session.SessionId),
                    ("$profile", session.ProfileId),
                    ("$channel", session.Channel.ToWire()),
                    ("$mode", session.ContentMode.ToWire()),
                    ("$token", (object?)session.TokenHmac ?? DBNull.Value),
                    ("$created", now),
                    ("$expires", session.ExpiresAtMs),
                    ("$project", session.ProjectId.ToString("D", CultureInfo.InvariantCulture)));

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask AcceptAsync(AcceptedExecution accepted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accepted);

        long now = clock.UnixTimeMilliseconds;

        await AuditedAsync(
            "T1-accept",
            (connection, transaction) =>
            {
                Execute(
                    connection,
                    transaction,
                    "INSERT INTO executions(id, session_id, state, policy_digest, registry_digest, input_hmac, "
                    + "created_at_ms, updated_at_ms, deadline_at_ms) "
                    + "VALUES ($id, $session, 'accepted', $policy, $registry, $input, $now, $now, $deadline);",
                    ("$id", accepted.ExecutionId),
                    ("$session", accepted.SessionId),
                    ("$policy", accepted.PolicyDigest),
                    ("$registry", accepted.RegistryDigest),
                    ("$input", accepted.InputHmac),
                    ("$now", now),
                    ("$deadline", accepted.DeadlineAtMs));

                if (accepted.IdempotencyKeyHmac is string key)
                {
                    // La chiave vive nella stessa transazione dell'accettazione: senza, una
                    // ripetizione potrebbe creare una seconda esecuzione.
                    Execute(
                        connection,
                        transaction,
                        "INSERT INTO idempotency_keys(session_id, key_hmac, request_hmac, execution_id, "
                        + "created_at_ms, expires_at_ms) "
                        + "VALUES ($session, $key, $request, $execution, $now, $expires);",
                        ("$session", accepted.SessionId),
                        ("$key", key),
                        // Il digest della richiesta intera, quando il chiamante ne ha uno: l'HMAC
                        // del solo input renderebbe uguali due richieste che differiscono per
                        // istruzioni, contesto o strumenti, e la ripetizione dopo un riavvio
                        // verrebbe giudicata con una regola piu' debole di quella usata prima (S74).
                        ("$request", accepted.RequestDigest ?? accepted.InputHmac),
                        ("$execution", accepted.ExecutionId),
                        ("$now", now),
                        ("$expires", now + (24 * 60 * 60 * 1000)));
                }

                AppendAudit(
                    connection,
                    transaction,
                    accepted.AuditEventId,
                    accepted.ExecutionId,
                    attemptId: null,
                    AuditCodes.RequestAccepted,
                    "{}",
                    now);

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask AppendAuditAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!AuditCodes.IsKnown(record.Code))
        {
            // Un codice fuori dall'insieme chiuso e' un difetto del chiamante: l'audit serve a
            // ricostruire una decisione, e voci arbitrarie lo rendono illeggibile.
            throw new ArgumentOutOfRangeException(nameof(record), "The audit code is not known: " + record.Code);
        }

        long now = clock.UnixTimeMilliseconds;

        await writer.ExecuteAsync(
            "T-audit",
            (connection, transaction) =>
            {
                AppendAudit(
                    connection,
                    transaction,
                    record.EventId,
                    record.ExecutionId,
                    record.AttemptId,
                    record.Code,
                    record.MetadataJson,
                    now);

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask CommitDispatchIntentAsync(DispatchIntent intent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);

        long now = clock.UnixTimeMilliseconds;

        await AuditedAsync(
            "T2-dispatch-intent",
            (connection, transaction) =>
            {
                foreach (StoredDecision decision in intent.Decisions)
                {
                    Execute(
                        connection,
                        transaction,
                        "INSERT INTO decisions(execution_id, attempt_ordinal, target_id, allowed, reasons_json, "
                        + "score_json, policy_digest, registry_digest) "
                        + "VALUES ($execution, $ordinal, $target, $allowed, $reasons, $score, "
                        + "(SELECT policy_digest FROM executions WHERE id = $execution), "
                        + "(SELECT registry_digest FROM executions WHERE id = $execution));",
                        ("$execution", intent.ExecutionId),
                        ("$ordinal", intent.Ordinal),
                        ("$target", decision.TargetId),
                        ("$allowed", decision.Allowed ? 1 : 0),
                        ("$reasons", decision.ReasonsJson),
                        ("$score", (object?)decision.ScoreJson ?? DBNull.Value));
                }

                Execute(
                    connection,
                    transaction,
                    "INSERT INTO attempts(id, execution_id, ordinal, target_id, state, side_effect_state, "
                    + "grant_nonce_hmac, payload_hmac, started_at_ms) "
                    + "VALUES ($id, $execution, $ordinal, $target, 'dispatching', $side, $nonce, $payload, $now);",
                    ("$id", intent.AttemptId),
                    ("$execution", intent.ExecutionId),
                    ("$ordinal", intent.Ordinal),
                    ("$target", intent.TargetId),
                    ("$side", intent.SideEffects.ToWire()),
                    ("$nonce", intent.GrantNonceHmac),
                    ("$payload", intent.PayloadHmac),
                    ("$now", now));

                Execute(
                    connection,
                    transaction,
                    "UPDATE executions SET state = 'running', updated_at_ms = $now WHERE id = $execution;",
                    ("$execution", intent.ExecutionId),
                    ("$now", now));

                AppendAudit(
                    connection,
                    transaction,
                    intent.AuditEventId,
                    intent.ExecutionId,
                    intent.AttemptId,
                    AuditCodes.AttemptDispatchIntent,
                    AuditMetadata.ToJson(
                        ("targetId", intent.TargetId),
                        ("ordinal", intent.Ordinal.ToString(CultureInfo.InvariantCulture))),
                    now);

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(ExecutionOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        long now = clock.UnixTimeMilliseconds;

        await AuditedAsync(
            "T3-complete",
            (connection, transaction) =>
            {
                // Decisioni di un percorso che non ha mai creato un tentativo (S83): un turno negato
                // si ferma prima del dispatch, quindi non passa da T2 e i suoi motivi si sarebbero
                // persi. Ordinale 0 = deciso prima di ogni tentativo; il punteggio resta nullo,
                // perche' un candidato negato non e' stato confrontato con nessuno.
                foreach (StoredDecision decision in outcome.Decisions ?? [])
                {
                    Execute(
                        connection,
                        transaction,
                        "INSERT INTO decisions(execution_id, attempt_ordinal, target_id, allowed, reasons_json, "
                        + "score_json, policy_digest, registry_digest) "
                        + "VALUES ($execution, 0, $target, $allowed, $reasons, $score, "
                        + "(SELECT policy_digest FROM executions WHERE id = $execution), "
                        + "(SELECT registry_digest FROM executions WHERE id = $execution));",
                        ("$execution", outcome.ExecutionId),
                        ("$target", decision.TargetId),
                        ("$allowed", decision.Allowed ? 1 : 0),
                        ("$reasons", decision.ReasonsJson),
                        ("$score", (object?)decision.ScoreJson ?? DBNull.Value));
                }

                foreach (StoredBlob blob in outcome.Blobs)
                {
                    Execute(
                        connection,
                        transaction,
                        "INSERT INTO content_blobs(id, session_id, execution_id, kind, relative_path, "
                        + "cipher_sha256, byte_length, created_at_ms, expires_at_ms) "
                        + "VALUES ($id, (SELECT session_id FROM executions WHERE id = $execution), $execution, "
                        + "$kind, $path, $digest, $length, $now, $expires);",
                        ("$id", blob.BlobId),
                        ("$execution", outcome.ExecutionId),
                        ("$kind", blob.Kind.ToWire()),
                        ("$path", blob.RelativePath),
                        ("$digest", blob.CipherSha256),
                        ("$length", blob.ByteLength),
                        ("$now", now),
                        ("$expires", blob.ExpiresAtMs));
                }

                if (outcome.AttemptId is string attemptId)
                {
                    Execute(
                        connection,
                        transaction,
                        "UPDATE attempts SET state = $state, side_effect_state = $side, ended_at_ms = $now, "
                        + "error_code = $error WHERE id = $id;",
                        ("$id", attemptId),
                        ("$state", outcome.AttemptState.ToWire()),
                        ("$side", outcome.SideEffects.ToWire()),
                        ("$now", now),
                        ("$error", (object?)outcome.ErrorCode?.ToWire() ?? DBNull.Value));
                }

                Execute(
                    connection,
                    transaction,
                    "UPDATE executions SET state = $state, updated_at_ms = $now, error_code = $error, "
                    + "usage_json = $usage, content_availability = $availability WHERE id = $id;",
                    ("$id", outcome.ExecutionId),
                    ("$state", outcome.State.ToWire()),
                    ("$now", now),
                    ("$error", (object?)outcome.ErrorCode?.ToWire() ?? DBNull.Value),
                    ("$usage", (object?)outcome.UsageJson ?? DBNull.Value),
                    ("$availability", outcome.ContentAvailability.ToWire()));

                if (outcome.State is ExecutionState.Uncertain)
                {
                    // La sessione resta segnata: nessuna nuova esecuzione finche' l'operatore non
                    // ha riconciliato quello che potrebbe essere successo.
                    Execute(
                        connection,
                        transaction,
                        "UPDATE sessions SET recovery_required = 1, version = version + 1 "
                        + "WHERE id = (SELECT session_id FROM executions WHERE id = $execution);",
                        ("$execution", outcome.ExecutionId));
                }

                AppendAudit(
                    connection,
                    transaction,
                    outcome.AuditEventId,
                    outcome.ExecutionId,
                    outcome.AttemptId,
                    AuditCodes.ExecutionPrefix + outcome.State.ToWire(),
                    AuditMetadata.ToJson(("errorCode", outcome.ErrorCode?.ToWire() ?? "none")),
                    now);

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask AppendNextStateEventAsync(
        string executionId,
        ExecutionState state,
        long expiresAtMs,
        CancellationToken cancellationToken)
    {
        long now = clock.UnixTimeMilliseconds;

        await writer.ExecuteAsync(
            "T-state-event-next",
            (connection, transaction) =>
            {
                // La sequenza viene scelta dentro la transazione: fuori, due scritture vicine
                // sceglierebbero lo stesso numero e la chiave primaria le rifiuterebbe.
                Execute(
                    connection,
                    transaction,
                    "INSERT INTO execution_events(execution_id, sequence, state, created_at_ms, expires_at_ms) "
                    + "SELECT $execution, COALESCE(MAX(sequence), 0) + 1, $state, $now, $expires "
                    + "FROM execution_events WHERE execution_id = $execution;",
                    ("$execution", executionId),
                    ("$state", state.ToWire()),
                    ("$now", now),
                    ("$expires", expiresAtMs));

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask AppendStateEventAsync(
        string executionId,
        int sequence,
        ExecutionState state,
        long expiresAtMs,
        CancellationToken cancellationToken)
    {
        long now = clock.UnixTimeMilliseconds;

        await writer.ExecuteAsync(
            "T-state-event",
            (connection, transaction) =>
            {
                Execute(
                    connection,
                    transaction,
                    "INSERT INTO execution_events(execution_id, sequence, state, created_at_ms, expires_at_ms) "
                    + "VALUES ($execution, $sequence, $state, $now, $expires);",
                    ("$execution", executionId),
                    ("$sequence", sequence),
                    ("$state", state.ToWire()),
                    ("$now", now),
                    ("$expires", expiresAtMs));

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Esegue una transazione che contiene righe di audit. Un guasto qui e' diverso da un guasto
    /// qualunque dello storage: l'audit precede il dispatch, e se non si scrive l'esecuzione non
    /// parte. Il conteggio serve a distinguerlo da un errore del provider.
    /// </summary>
    private async Task AuditedAsync(
        string transactionName,
        Func<SqliteConnection, SqliteTransaction, int> work,
        CancellationToken cancellationToken)
    {
        try
        {
            await writer.ExecuteAsync(transactionName, work, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (Count())
        {
            throw;
        }
    }

    /// <summary>Conta il guasto e lascia passare l'eccezione: il filtro non cattura mai.</summary>
    private static bool Count()
    {
        RouterTelemetry.RecordAuditFailure();
        return false;
    }

    /// <summary>
    /// Metadata di audit come JSON di sole coppie allowlisted. Nessun contenuto, nessun path
    /// completo, nessun messaggio del provider (INV-10).
    /// </summary>
    private static void AppendAudit(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        string executionId,
        string? attemptId,
        string code,
        string metadataJson,
        long now) =>
        Execute(
            connection,
            transaction,
            "INSERT INTO audit_events(event_id, execution_id, attempt_id, code, metadata_json, created_at_ms) "
            + "VALUES ($event, $execution, $attempt, $code, $metadata, $now);",
            ("$event", eventId),
            ("$execution", executionId),
            ("$attempt", (object?)attemptId ?? DBNull.Value),
            ("$code", code),
            ("$metadata", metadataJson),
            ("$now", now));

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
}
