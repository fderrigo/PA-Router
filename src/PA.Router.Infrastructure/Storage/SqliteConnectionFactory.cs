using Microsoft.Data.Sqlite;

namespace PA.Router.Infrastructure.Storage;

/// <summary>
/// Apre connessioni al file store applicando i PRAGMA richiesti da 03-specs/STORAGE-AUDIT.md.
/// I PRAGMA vengono impostati su ogni connessione: SQLite non li eredita fra connessioni.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string connectionString;

    public SqliteConnectionFactory(StorePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Pool disattivato: il writer possiede la sua connessione e i lettori la chiudono
            // davvero, cosi' il rilascio del file e' osservabile nei test.
            Pooling = false,
            ForeignKeys = true,
        };

        connectionString = builder.ConnectionString;
    }

    /// <summary>Apre una connessione gia' configurata.</summary>
    public SqliteConnection Open()
    {
        SqliteConnection connection = new(connectionString);
        try
        {
            connection.Open();
            ApplyPragmas(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }

    /// <summary>
    /// PRAGMA obbligatori. Vanno eseguiti fuori da qualunque transazione: journal_mode e
    /// foreign_keys sono ignorati o rifiutati all'interno di una transazione aperta.
    /// </summary>
    public static void ApplyPragmas(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA synchronous = FULL;");
        Execute(connection, "PRAGMA busy_timeout = 5000;");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
