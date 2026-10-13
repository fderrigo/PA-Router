using System.Globalization;
using Microsoft.Data.Sqlite;
using PA.Router.Abstractions;

namespace PA.Router.Infrastructure.Storage;

/// <summary>
/// Esegue le migrazioni forward-only. Sequenza fissata da 03-specs/STORAGE-AUDIT.md: PRAGMA
/// fuori transazione, BEGIN IMMEDIATE, DDL, inserimento parametrizzato di versione e checksum,
/// COMMIT; ROLLBACK su errore.
/// </summary>
public sealed class MigrationRunner
{
    private readonly SqliteConnectionFactory factory;
    private readonly IClock clock;
    private readonly IReadOnlyList<MigrationScript> scripts;

    public MigrationRunner(SqliteConnectionFactory factory, IClock clock)
        : this(factory, clock, MigrationScript.All)
    {
    }

    /// <summary>Overload per i test: permette di iniettare uno script che fallisce a meta'.</summary>
    public MigrationRunner(SqliteConnectionFactory factory, IClock clock, IReadOnlyList<MigrationScript> scripts)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(scripts);

        this.factory = factory;
        this.clock = clock;
        this.scripts = scripts;
    }

    /// <summary>Applica le migrazioni mancanti e restituisce la versione finale.</summary>
    public int Migrate()
    {
        using SqliteConnection connection = factory.Open();

        IReadOnlyDictionary<int, string> applied = ReadAppliedMigrations(connection);
        VerifyApplied(applied);

        foreach (MigrationScript script in scripts)
        {
            if (applied.ContainsKey(script.Version))
            {
                continue;
            }

            Apply(connection, script);
        }

        return scripts.Count == 0 ? 0 : scripts[^1].Version;
    }

    /// <summary>Versione registrata nello store, 0 se lo store e' nuovo.</summary>
    public int ReadCurrentVersion()
    {
        using SqliteConnection connection = factory.Open();
        IReadOnlyDictionary<int, string> applied = ReadAppliedMigrations(connection);
        return applied.Count == 0 ? 0 : applied.Keys.Max();
    }

    private void VerifyApplied(IReadOnlyDictionary<int, string> applied)
    {
        Dictionary<int, MigrationScript> known = scripts.ToDictionary(script => script.Version);

        foreach (KeyValuePair<int, string> entry in applied)
        {
            string version = entry.Key.ToString(CultureInfo.InvariantCulture);

            if (!known.TryGetValue(entry.Key, out MigrationScript? script))
            {
                // Schema piu' recente di questa build: fermarsi, mai riscrivere all'indietro.
                throw new PaRouterException(
                    ErrorCode.StoreUnavailable,
                    "The store schema is newer than this build supports.")
                    .WithMetadata("schemaVersion", version);
            }

            if (!string.Equals(script.Checksum, entry.Value, StringComparison.Ordinal))
            {
                throw new PaRouterException(
                    ErrorCode.StoreUnavailable,
                    "An applied migration does not match the checksum recorded in the store.")
                    .WithMetadata("schemaVersion", version);
            }
        }
    }

    private void Apply(SqliteConnection connection, MigrationScript script)
    {
        if (script.Pragmas.Length > 0)
        {
            using SqliteCommand pragmaCommand = connection.CreateCommand();
            pragmaCommand.CommandText = script.Pragmas;
            pragmaCommand.ExecuteNonQuery();
        }

        // deferred: false produce BEGIN IMMEDIATE, che riserva subito il lock di scrittura.
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        try
        {
            using (SqliteCommand ddl = connection.CreateCommand())
            {
                ddl.Transaction = transaction;
                ddl.CommandText = script.Body;
                ddl.ExecuteNonQuery();
            }

            using (SqliteCommand record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText =
                    "INSERT INTO schema_migrations(version, checksum, applied_at_ms) " +
                    "VALUES ($version, $checksum, $appliedAt);";
                record.Parameters.AddWithValue("$version", script.Version);
                record.Parameters.AddWithValue("$checksum", script.Checksum);
                record.Parameters.AddWithValue("$appliedAt", clock.UnixTimeMilliseconds);
                record.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (SqliteException ex)
        {
            transaction.Rollback();

            throw new PaRouterException(
                ErrorCode.StoreUnavailable,
                "A migration failed and was rolled back.",
                ex)
                .WithMetadata("migration", script.ResourceName);
        }
    }

    private static IReadOnlyDictionary<int, string> ReadAppliedMigrations(SqliteConnection connection)
    {
        Dictionary<int, string> applied = [];

        using (SqliteCommand exists = connection.CreateCommand())
        {
            exists.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';";

            long count = Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (count == 0)
            {
                return applied;
            }
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT version, checksum FROM schema_migrations ORDER BY version;";

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            applied[reader.GetInt32(0)] = reader.GetString(1);
        }

        return applied;
    }
}
