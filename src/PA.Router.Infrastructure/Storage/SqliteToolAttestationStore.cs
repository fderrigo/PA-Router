using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Infrastructure.Storage;

/// <summary>
/// Attestazioni degli strumenti dichiarate dalle applicazioni (ADR-020), sopra il writer seriale.
///
/// Una riga per progetto, sorgente e strumento: una dichiarazione nuova sostituisce la precedente. La
/// revoca e' dell'operatore e sta nella configurazione, che vale sopra queste righe.
/// </summary>
public sealed class SqliteToolAttestationStore
{
    private readonly StoreWriter writer;
    private readonly SqliteConnectionFactory factory;

    public SqliteToolAttestationStore(StoreWriter writer, SqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(factory);

        this.writer = writer;
        this.factory = factory;
    }

    /// <summary>Registra o sostituisce l'attestazione di un'applicazione.</summary>
    public async ValueTask RecordAsync(Guid projectId, StoredToolAttestation attestation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attestation);

        await writer.ExecuteAsync(
            "T-tool-attestation",
            (connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();

                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO tool_attestations(project_id, source_key, tool_name, fingerprint, accepts, returns, "
                    + "regions_json, declared_by, recorded_at_ms, expires_at_ms) "
                    + "VALUES ($project, $source, $tool, $fingerprint, $accepts, $returns, $regions, 'application', $recorded, $expires) "
                    + "ON CONFLICT(project_id, source_key, tool_name) DO UPDATE SET "
                    + "fingerprint = excluded.fingerprint, accepts = excluded.accepts, returns = excluded.returns, "
                    + "regions_json = excluded.regions_json, recorded_at_ms = excluded.recorded_at_ms, "
                    + "expires_at_ms = excluded.expires_at_ms;";

                command.Parameters.AddWithValue("$project", projectId.ToString("D", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$source", attestation.SourceKey);
                command.Parameters.AddWithValue("$tool", attestation.Tool);
                command.Parameters.AddWithValue("$fingerprint", attestation.Fingerprint);
                command.Parameters.AddWithValue("$accepts", attestation.Accepts.ToWire());
                command.Parameters.AddWithValue("$returns", attestation.Returns.ToWire());
                command.Parameters.AddWithValue(
                    "$regions",
                    JsonSerializer.Serialize(attestation.Regions.Select(region => region.Code).Order(StringComparer.Ordinal).ToArray()));
                command.Parameters.AddWithValue("$recorded", attestation.RecordedAt.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$expires", attestation.ExpiresAt.ToUnixTimeMilliseconds());

                command.ExecuteNonQuery();

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Attestazioni delle applicazioni di un progetto, comprese quelle scadute.</summary>
    public IReadOnlyList<StoredToolAttestation> List(Guid projectId) => ListAt(factory, projectId);

    /// <summary>Lettura senza writer, per chi diagnostica la directory dati senza possederla.</summary>
    public static IReadOnlyList<StoredToolAttestation> ListAt(SqliteConnectionFactory factory, Guid projectId)
    {
        ArgumentNullException.ThrowIfNull(factory);

        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT source_key, tool_name, fingerprint, accepts, returns, regions_json, recorded_at_ms, expires_at_ms "
            + "FROM tool_attestations WHERE project_id = $project ORDER BY source_key, tool_name;";

        command.Parameters.AddWithValue("$project", projectId.ToString("D", CultureInfo.InvariantCulture));

        using SqliteDataReader reader = command.ExecuteReader();
        List<StoredToolAttestation> attestations = [];

        while (reader.Read())
        {
            string[] regions = JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [];

            attestations.Add(new StoredToolAttestation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                WireEnum.Parse<DataClass>(reader.GetString(3)),
                WireEnum.Parse<DataClass>(reader.GetString(4)),
                new HashSet<ProcessingRegion>(regions.Select(Regions.Parse)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7))));
        }

        return attestations;
    }
}
