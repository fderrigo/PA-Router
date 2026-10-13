using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Infrastructure.Storage;

/// <summary>Stato amministrativo di una sessione.</summary>
/// <param name="SessionId">Identificatore.</param>
/// <param name="ProfileId">Profilo applicato.</param>
/// <param name="Channel">Canale.</param>
/// <param name="ContentMode">Modo di conservazione dei contenuti.</param>
/// <param name="ProjectId">Progetto.</param>
/// <param name="CreatedAt">Creazione.</param>
/// <param name="ExpiresAt">Scadenza.</param>
/// <param name="Turns">Turni conservati, se la conservazione e' attiva.</param>
/// <param name="UncertainExecutions">Esecuzioni con effetti incerti da risolvere.</param>
public sealed record SessionSummary(
    string SessionId,
    string ProfileId,
    Channel Channel,
    ContentMode ContentMode,
    Guid ProjectId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int Turns,
    IReadOnlyList<string> UncertainExecutions)
{
    /// <summary>
    /// Nome scelto dall'operatore, quando c'e' (S62, migrazione 008).
    ///
    /// Non si deriva: riconoscere una conversazione fra dieci GUID e' impossibile, e un'etichetta
    /// calcolata da profilo e data sarebbe un'altra cosa rispetto a un nome che qualcuno ha scelto.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Quando la sessione e' stata archiviata, se lo e'.
    ///
    /// Archiviare non e' cancellare: cancellare rimuove contenuti ed esecuzioni, archiviare toglie
    /// dalla vista e lascia tutto dov'e'. Due decisioni diverse, due operazioni diverse.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; init; }
}

/// <summary>
/// Cronologia durevole e amministrazione delle sessioni
/// (03-specs/SESSION-HANDOVER.md, «Recovery e retention»).
///
/// La cronologia esiste solo in modalita' <c>encrypted</c>: i turni diventano blob cifrati legati
/// alla sessione. In <c>memory_only</c> non viene scritto nulla, e dopo un riavvio la sessione non
/// ha contesto — la risposta e' CONTEXT_UNAVAILABLE, non una ricostruzione approssimata.
///
/// <c>recover</c> registra una risoluzione amministrativa e nient'altro: non crea un'esecuzione,
/// non ne riavvia una e non dichiara completato il lavoro precedente. Continuare richiede un run
/// esplicito, con un nuovo identificatore.
/// </summary>
public sealed class SqliteSessionService
{
    private const int TranscriptSchemaVersion = 1;

    /// <summary>Lunghezza massima di un nome scelto dall'operatore.</summary>
    public const int MaxDisplayNameLength = 60;

    private readonly StoreWriter writer;
    private readonly SqliteConnectionFactory factory;
    private readonly IContentStore content;
    private readonly IClock clock;
    private readonly IIdGenerator ids;

    public SqliteSessionService(
        StoreWriter writer,
        SqliteConnectionFactory factory,
        IContentStore content,
        IClock clock,
        IIdGenerator ids)
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
    }

    /// <summary>Vero se questa istanza conserva davvero i turni.</summary>
    public bool PersistsHistory => content.Mode is ContentMode.Encrypted;

    /// <summary>
    /// Registra un turno. In <c>memory_only</c> non scrive: restituisce falso, cosi' il chiamante
    /// sa che dopo un riavvio quel turno non esistera'.
    /// </summary>
    public async ValueTask<bool> AppendTurnAsync(
        string sessionId,
        SessionTurn turn,
        long expiresAtMs,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(turn);

        if (!PersistsHistory)
        {
            return false;
        }

        string payload = Serialize(turn);
        ContentBlobRequest request = new(ids.NewId(), sessionId, BlobKind.Transcript, TranscriptSchemaVersion);

        ContentBlobReceipt receipt = await content
            .WriteAsync(request, Encoding.UTF8.GetBytes(payload), cancellationToken)
            .ConfigureAwait(false);

        long now = clock.UnixTimeMilliseconds;

        await writer.ExecuteAsync(
            "T-session-turn",
            (connection, transaction) =>
            {
                // Il riferimento all'esecuzione si scrive solo se quella riga esiste ancora: la
                // retention puo' aver rimosso l'esecuzione, e la cronologia della conversazione non
                // deve sparire con lei.
                bool executionExists = ScalarString(
                    connection,
                    transaction,
                    "SELECT id FROM executions WHERE id = $id;",
                    ("$id", turn.ExecutionId)) is not null;

                Execute(
                    connection,
                    transaction,
                    "INSERT INTO content_blobs(id, session_id, execution_id, kind, schema_version, relative_path, "
                    + "cipher_sha256, byte_length, created_at_ms, expires_at_ms) "
                    + "VALUES ($id, $session, $execution, $kind, $schema, $path, $digest, $length, $now, $expires);",
                    ("$id", receipt.BlobId),
                    ("$session", sessionId),
                    ("$execution", executionExists ? turn.ExecutionId : (object)DBNull.Value),
                    ("$kind", BlobKind.Transcript.ToWire()),
                    ("$schema", TranscriptSchemaVersion),
                    ("$path", receipt.RelativePath),
                    ("$digest", receipt.CipherSha256),
                    ("$length", receipt.ByteLength),
                    ("$now", now),
                    ("$expires", expiresAtMs));

                return 0;
            },
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Rilegge i turni conservati. Senza conservazione l'elenco e' vuoto: il chiamante deve
    /// distinguere «nessun turno» da «contesto non disponibile», e <see cref="PersistsHistory"/> e'
    /// cio' che glielo permette.
    /// </summary>
    public async ValueTask<IReadOnlyList<SessionTurn>> ReadTurnsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (!PersistsHistory)
        {
            return [];
        }

        List<ContentBlobReceipt> receipts = [];

        using (SqliteConnection connection = factory.Open())
        {
            using SqliteCommand command = connection.CreateCommand();

            command.CommandText =
                "SELECT id, relative_path, cipher_sha256, byte_length FROM content_blobs "
                + "WHERE session_id = $session AND kind = $kind AND expires_at_ms > $now ORDER BY created_at_ms, id;";

            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$kind", BlobKind.Transcript.ToWire());
            command.Parameters.AddWithValue("$now", clock.UnixTimeMilliseconds);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                receipts.Add(new ContentBlobReceipt(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3)));
            }
        }

        List<SessionTurn> turns = [];

        foreach (ContentBlobReceipt receipt in receipts)
        {
            byte[] plaintext = await content
                .ReadAsync(
                    new ContentBlobRequest(receipt.BlobId, sessionId, BlobKind.Transcript, TranscriptSchemaVersion),
                    receipt,
                    cancellationToken)
                .ConfigureAwait(false);

            turns.Add(Deserialize(Encoding.UTF8.GetString(plaintext)));
        }

        return [.. turns.OrderBy(turn => turn.Ordinal)];
    }

    /// <summary>Stato della sessione, con i turni conservati e le esecuzioni incerte.</summary>
    public async ValueTask<SessionSummary?> ReadAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT profile_id, channel, content_mode, project_id, created_at_ms, expires_at_ms, "
            + "display_name, archived_at_ms "
            + "FROM sessions WHERE id = $id;";

        command.Parameters.AddWithValue("$id", sessionId);

        SessionSummary? summary = null;

        using (SqliteDataReader reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                summary = new SessionSummary(
                    sessionId,
                    reader.GetString(0),
                    WireEnum.Parse<Channel>(reader.GetString(1)),
                    ContentModes.Parse(reader.GetString(2)),
                    Guid.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                    Turns: 0,
                    UncertainExecutions: [])
                {
                    DisplayName = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ArchivedAt = reader.IsDBNull(7)
                        ? null
                        : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
                };
            }
        }

        if (summary is null)
        {
            return null;
        }

        IReadOnlyList<SessionTurn> turns = await ReadTurnsAsync(sessionId, cancellationToken).ConfigureAwait(false);

        return summary with
        {
            Turns = turns.Count,
            UncertainExecutions = ReadUncertain(sessionId),
        };
    }

    /// <summary>
    /// Registra la risoluzione amministrativa di un'esecuzione incerta.
    ///
    /// Non riavvia nulla e non dichiara completato il lavoro: chiude soltanto l'incertezza, con la
    /// dichiarazione dell'operatore che ha guardato diff e processi.
    /// </summary>
    public async ValueTask<bool> ResolveUncertainAsync(
        string executionId,
        string resolution,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolution);

        long now = clock.UnixTimeMilliseconds;
        string eventId = ids.NewId();

        return await writer.ExecuteAsync(
            "T-session-recover",
            (connection, transaction) =>
            {
                string? state = ScalarString(
                    connection,
                    transaction,
                    "SELECT state FROM executions WHERE id = $id;",
                    ("$id", executionId));

                if (state is not "uncertain")
                {
                    return false;
                }

                Execute(
                    connection,
                    transaction,
                    "INSERT INTO audit_events(event_id, execution_id, attempt_id, code, metadata_json, created_at_ms) "
                    + "VALUES ($event, $execution, NULL, 'recovery_recorded', $metadata, $now);",
                    ("$event", eventId),
                    ("$execution", executionId),
                    ("$metadata", "{\"resolution\":\"" + resolution + "\"}"),
                    ("$now", now));

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancella i contenuti di una sessione: token, blob e file. I metadati restano per l'audit,
    /// e nulla viene toccato nel repository dell'utente.
    /// </summary>
    public async ValueTask<int> DeleteContentAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        List<ContentBlobReceipt> receipts = [];

        using (SqliteConnection connection = factory.Open())
        {
            using SqliteCommand command = connection.CreateCommand();

            // **Solo i transcript**, come la cancellazione qui sotto (difetto trovato dall'audit, S72).
            //
            // La lettura non filtrava per tipo mentre la DELETE lo faceva: i file di osservazioni,
            // embedding, capsule, checkpoint, richieste e uscite venivano cancellati dal disco
            // mentre le loro righe restavano in `content_blobs`. Il commento qui sotto diceva gia'
            // che quei blob non si toccano, e il codice li toccava: un ricordo spariva in silenzio,
            // perche' la lettura autorizzata salta i blob il cui esito e' `missing`, e lo sweeper
            // rimuove i file non referenziati, mai le righe che puntano a file assenti.
            command.CommandText =
                "SELECT id, relative_path, cipher_sha256, byte_length FROM content_blobs "
                + "WHERE session_id = $session AND kind = $kind;";

            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$kind", BlobKind.Transcript.ToWire());

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                receipts.Add(new ContentBlobReceipt(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3)));
            }
        }

        await writer.ExecuteAsync(
            "T-session-delete",
            (connection, transaction) =>
            {
                // I blob referenziati da osservazioni non si toccano: quelli seguono la memoria.
                Execute(
                    connection,
                    transaction,
                    "DELETE FROM content_blobs WHERE session_id = $session AND kind = $kind;",
                    ("$session", sessionId),
                    ("$kind", BlobKind.Transcript.ToWire()));

                // La capability si revoca **sostituendola**, non annullandola: una sessione di servizio ha sempre un
                // HMAC (vincolo dello schema), e un valore casuale non e' l'HMAC di nessun token esistente. Metterlo a
                // NULL violava il vincolo e faceva fallire la cancellazione di ogni sessione del chatbot.
                Execute(
                    connection,
                    transaction,
                    "UPDATE sessions SET token_hmac = $revoked, state = 'closed' WHERE id = $session;",
                    ("$revoked", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))),
                    ("$session", sessionId));

                // Una conversazione cancellata non si propone piu' (23 settembre 2026): restava nell'elenco «attiva», con
                // il suo nome e zero turni, e il nome restava occupato. Esce dalla proposta come un'archiviata — i
                // metadati restano per l'audit — e il nome si libera.
                Execute(
                    connection,
                    transaction,
                    "UPDATE sessions SET display_name = NULL, archived_at_ms = COALESCE(archived_at_ms, $now) WHERE id = $session;",
                    ("$now", clock.UtcNow.ToUnixTimeMilliseconds()),
                    ("$session", sessionId));

                return 0;
            },
            cancellationToken).ConfigureAwait(false);

        int removed = 0;

        foreach (ContentBlobReceipt receipt in receipts)
        {
            await content.DeleteAsync(receipt, cancellationToken).ConfigureAwait(false);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Riassunti delle sessioni, dalla piu' recente (S62).
    ///
    /// Serve a chi deve **scegliere**: un selettore che elenca GUID non aiuta nessuno. I turni e le
    /// esecuzioni incerte non vengono contati qui — costerebbero una lettura per riga — e restano
    /// quelli di <see cref="ReadAsync"/>, che si chiama sulla sessione scelta.
    /// </summary>
    /// <param name="includeArchived">Vero per vedere anche quelle archiviate.</param>
    /// <param name="limit">Quante riportarne al massimo.</param>
    public IReadOnlyList<SessionSummary> Summaries(bool includeArchived = false, int limit = 50)
    {
        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        // I turni si contano dalle righe dei blob, non leggendoli: un selettore che decifrasse la
        // cronologia di cinquanta sessioni per mostrare un numero aprirebbe cinquanta conversazioni
        // per sceglierne una. Un blob di transcript per turno e' l'invariante di AppendTurnAsync.
        command.CommandText =
            "SELECT s.id, s.profile_id, s.channel, s.content_mode, s.project_id, s.created_at_ms, "
            + "s.expires_at_ms, s.display_name, s.archived_at_ms, "
            + "(SELECT COUNT(*) FROM content_blobs b WHERE b.session_id = s.id AND b.kind = $kind) "
            + "FROM sessions s "
            + (includeArchived ? string.Empty : "WHERE s.archived_at_ms IS NULL ")
            + "ORDER BY s.created_at_ms DESC LIMIT $limit;";

        command.Parameters.AddWithValue("$kind", BlobKind.Transcript.ToWire());
        command.Parameters.AddWithValue("$limit", limit);

        List<SessionSummary> sessions = [];

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            sessions.Add(new SessionSummary(
                reader.GetString(0),
                reader.GetString(1),
                WireEnum.Parse<Channel>(reader.GetString(2)),
                ContentModes.Parse(reader.GetString(3)),
                Guid.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                Turns: reader.GetInt32(9),
                UncertainExecutions: [])
            {
                DisplayName = reader.IsDBNull(7) ? null : reader.GetString(7),
                ArchivedAt = reader.IsDBNull(8)
                    ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
            });
        }

        return sessions;
    }

    /// <summary>
    /// Da' un nome a una sessione, oppure lo toglie con null (S62).
    ///
    /// L'unicita' la garantisce l'indice parziale della migrazione 008: riprendere «rifattorizzazione»
    /// deve scegliere una conversazione sola, quindi un nome gia' in uso viene rifiutato invece di
    /// spostarsi in silenzio da una sessione all'altra.
    /// </summary>
    public async ValueTask NameAsync(string sessionId, string? name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        string? trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        if (trimmed is not null && (trimmed.Length > MaxDisplayNameLength || trimmed.Any(char.IsControl)))
        {
            // Il nome finisce in un elenco: una riga di sequenze di controllo o lunga un paragrafo
            // romperebbe la resa di tutte le altre. La sanificazione della superficie resta, ma non
            // e' una scusa per scrivere nello store qualcosa che nessuno vorra' mai leggere.
            throw new PaRouterException(ErrorCode.InputInvalid, "The session name is too long or contains control characters.")
                .WithMetadata("reason", "name_invalid");
        }

        await writer.ExecuteAsync(
            "T-session-name",
            (connection, transaction) =>
            {
                // Una sessione del terminale esiste nello store solo dopo il primo turno: e' il
                // turno a crearne la riga. Dare un nome a una conversazione non ancora cominciata
                // aggiornerebbe zero righe, e tacerlo farebbe credere a un nome che non c'e'.
                if (ScalarString(connection, transaction, "SELECT id FROM sessions WHERE id = $id;", ("$id", sessionId)) is null)
                {
                    throw new PaRouterException(ErrorCode.InputInvalid, "That session does not exist yet.")
                        .WithMetadata("reason", "session_unknown");
                }

                // L'indice unico della migrazione 008 garantisce l'unicita' nello store; qui serve
                // il **rifiuto giusto**. Senza questo controllo la violazione del vincolo tornerebbe
                // come STORE_UNAVAILABLE, cioe' un guasto, mentre e' un nome gia' preso.
                if (trimmed is not null
                    && ScalarString(
                        connection,
                        transaction,
                        "SELECT id FROM sessions WHERE display_name = $name;",
                        ("$name", trimmed)) is string owner
                    && !string.Equals(owner, sessionId, StringComparison.Ordinal))
                {
                    throw new PaRouterException(ErrorCode.InputInvalid, "Another session already uses that name.")
                        .WithMetadata("reason", "name_taken");
                }

                Execute(
                    connection,
                    transaction,
                    "UPDATE sessions SET display_name = $name WHERE id = $id;",
                    ("$name", (object?)trimmed ?? DBNull.Value),
                    ("$id", sessionId));

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Archivia una sessione, o la riporta in vista (S62).
    ///
    /// Non cancella nulla: contenuti, esecuzioni e cronologia restano dove sono. E' la differenza
    /// fra «non mostrarmela piu'» e «fai sparire il lavoro», e vanno tenute distinte.
    /// </summary>
    public async ValueTask ArchiveAsync(string sessionId, bool archived, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await writer.ExecuteAsync(
            "T-session-archive",
            (connection, transaction) =>
            {
                if (ScalarString(connection, transaction, "SELECT id FROM sessions WHERE id = $id;", ("$id", sessionId)) is null)
                {
                    throw new PaRouterException(ErrorCode.InputInvalid, "That session does not exist yet.")
                        .WithMetadata("reason", "session_unknown");
                }

                Execute(
                    connection,
                    transaction,
                    "UPDATE sessions SET archived_at_ms = $at WHERE id = $id;",
                    ("$at", archived ? clock.UnixTimeMilliseconds : (object)DBNull.Value),
                    ("$id", sessionId));

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sessioni conosciute, per la diagnostica locale.</summary>
    public IReadOnlyList<string> List()
    {
        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT id FROM sessions ORDER BY created_at_ms DESC LIMIT 100;";

        List<string> sessions = [];

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            sessions.Add(reader.GetString(0));
        }

        return sessions;
    }

    private IReadOnlyList<string> ReadUncertain(string sessionId)
    {
        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT id FROM executions WHERE session_id = $session AND state = 'uncertain' ORDER BY created_at_ms;";

        command.Parameters.AddWithValue("$session", sessionId);

        List<string> executions = [];

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            executions.Add(reader.GetString(0));
        }

        return executions;
    }

    private static string Serialize(SessionTurn turn)
    {
        System.Text.Json.Nodes.JsonObject payload = new()
        {
            ["schemaVersion"] = TranscriptSchemaVersion,
            ["ordinal"] = turn.Ordinal,
            ["input"] = turn.Input,
            ["output"] = turn.Output,
            ["executionId"] = turn.ExecutionId,
            ["createdAt"] = turn.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["dataClass"] = turn.DeclaredClass?.ToWire(),

            // Chi ha fatto il turno e con quale sessione nativa (ADR-022). Facoltativi: un turno scritto prima di
            // S116 non li ha, e si legge lo stesso.
            ["serviceId"] = turn.ServiceId,
            ["nativeSessionId"] = turn.NativeSessionId,
        };

        return payload.ToJsonString();
    }

    private static SessionTurn Deserialize(string payload)
    {
        System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(payload)
            ?? throw new PaRouterException(ErrorCode.ContentUnavailable, "The stored turn is not readable.");

        return new SessionTurn(
            node["ordinal"]!.GetValue<int>(),
            node["input"]!.GetValue<string>(),
            node["output"]?.GetValue<string>(),
            node["executionId"]!.GetValue<string>(),
            DateTimeOffset.Parse(node["createdAt"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            node["dataClass"]?.GetValue<string>() is string declared && WireEnum.TryParse(declared, out DataClass parsed) ? parsed : null)
        {
            ServiceId = node["serviceId"]?.GetValue<string>(),
            NativeSessionId = node["nativeSessionId"]?.GetValue<string>(),
        };
    }

    private static string? ScalarString(
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

        return result is null or DBNull ? null : (string)result;
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
}
