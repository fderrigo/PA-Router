using Microsoft.Data.Sqlite;
using PA.Router.Abstractions;
using PA.Router.Infrastructure.Storage;

namespace PA.Router.Hosting.Runtime;

/// <summary>Una riga della traccia di audit.</summary>
/// <param name="Sequence">Posizione nella sequenza dell'esecuzione.</param>
/// <param name="Code">Codice dell'evento, da un insieme chiuso.</param>
/// <param name="MetadataJson">Metadata gia' scritti, con chiavi da un insieme chiuso.</param>
/// <param name="CreatedAtMs">Istante di scrittura.</param>
public sealed record AuditEntry(long Sequence, string Code, string MetadataJson, long CreatedAtMs);

/// <summary>Un tentativo dell'esecuzione.</summary>
/// <param name="Ordinal">Numero del tentativo.</param>
/// <param name="TargetId">Servizio contattato.</param>
/// <param name="State">Stato finale del tentativo.</param>
/// <param name="SideEffectState">Stato degli effetti: e' la parte che non si deduce dallo stato.</param>
/// <param name="ErrorCode">Codice del guasto, quando c'e'.</param>
public sealed record AuditAttempt(int Ordinal, string TargetId, string State, string SideEffectState, string? ErrorCode);

/// <summary>
/// Una decisione di policy persistita, con le componenti del punteggio quando il candidato era
/// ammesso (S75).
/// </summary>
/// <param name="Ordinal">Tentativo a cui la valutazione appartiene.</param>
/// <param name="TargetId">Candidato valutato.</param>
/// <param name="Allowed">Esito del gate.</param>
/// <param name="ReasonsJson">Codici di negazione, in JSON.</param>
/// <param name="ScoreJson">Componenti del punteggio, in JSON; null per i candidati negati.</param>
public sealed record AuditDecision(int Ordinal, string TargetId, bool Allowed, string ReasonsJson, string? ScoreJson);

/// <summary>Traccia di audit di una esecuzione.</summary>
/// <param name="ExecutionId">Esecuzione richiesta.</param>
/// <param name="Events">Eventi in ordine di sequenza.</param>
/// <param name="Attempts">Tentativi in ordine.</param>
/// <param name="Decisions">
/// Valutazione di ciascun candidato (S75). La tabella veniva scritta e non letta da nessuno: senza
/// queste righe l'audit diceva **quale** servizio era stato scelto e non **perche'** avesse vinto.
/// </param>
public sealed record AuditTrail(
    string ExecutionId,
    IReadOnlyList<AuditEntry> Events,
    IReadOnlyList<AuditAttempt> Attempts,
    IReadOnlyList<AuditDecision> Decisions);

/// <summary>
/// Letture della traccia di audit (S63).
///
/// Erano SQL dentro <c>pa audit show</c>, cioe' dentro un front end: il terminale interattivo non
/// poteva mostrarle senza riscrivere le stesse due query, e due query che divergono raccontano due
/// storie diverse dello stesso turno (INV-22).
///
/// E' una lettura pura: non tocca lo store, non prende il lock e non scrive nulla, quindi convive
/// con un worker vivo che possiede la directory.
/// </summary>
public static class AuditQueries
{
    /// <summary>
    /// L'ultima esecuzione con una traccia, oppure null se non ce n'e' nessuna (24 settembre 2026). `pa audit show` e
    /// `/audit` senza identificatore mostrano questa: l'identificatore di un turno non lo ricorda nessuno.
    /// </summary>
    public static string? Latest(StorePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (!File.Exists(paths.DatabaseFile)) { return null; }

        using SqliteConnection connection = new SqliteConnectionFactory(paths).Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT execution_id FROM audit_events WHERE execution_id IS NOT NULL ORDER BY created_at_ms DESC, sequence DESC LIMIT 1;";

        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Traccia di una esecuzione, oppure null se non ne esiste nessuna.
    ///
    /// Un'esecuzione senza eventi non e' un guasto: e' un identificatore che non appartiene a questo
    /// store, e chi mostra lo dice con le proprie parole.
    /// </summary>
    public static AuditTrail? Read(StorePaths paths, string executionId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        if (!File.Exists(paths.DatabaseFile))
        {
            throw new PaRouterException(ErrorCode.StoreUnavailable, "The store does not exist yet.")
                .WithMetadata("reason", "store_not_initialised");
        }

        List<AuditEntry> events = [];
        List<AuditAttempt> attempts = [];

        using SqliteConnection connection = new SqliteConnectionFactory(paths).Open();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT sequence, code, metadata_json, created_at_ms FROM audit_events "
                + "WHERE execution_id = $id ORDER BY sequence;";

            command.Parameters.AddWithValue("$id", executionId);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                events.Add(new AuditEntry(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
            }
        }

        if (events.Count == 0)
        {
            return null;
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT ordinal, target_id, state, side_effect_state, error_code FROM attempts "
                + "WHERE execution_id = $id ORDER BY ordinal;";

            command.Parameters.AddWithValue("$id", executionId);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                attempts.Add(new AuditAttempt(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        List<AuditDecision> decisions = [];

        using (SqliteCommand command = connection.CreateCommand())
        {
            // La tabella esisteva dal primo schema e non aveva **alcun** lettore in tutto il
            // prodotto: si scriveva e basta. Il motivo per cui un candidato e' stato escluso, e le
            // componenti del punteggio di quelli ammessi, sono cio' che rende una decisione
            // ricostruibile (S75).
            command.CommandText =
                "SELECT attempt_ordinal, target_id, allowed, reasons_json, score_json FROM decisions "
                + "WHERE execution_id = $id ORDER BY attempt_ordinal, target_id;";

            command.Parameters.AddWithValue("$id", executionId);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                decisions.Add(new AuditDecision(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetInt64(2) != 0,
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        return new AuditTrail(executionId, events, attempts, decisions);
    }
}
