using System.Globalization;
using Microsoft.Data.Sqlite;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Infrastructure.Storage;

/// <summary>
/// Catalogo dei pacchetti e dei grant su SQLite.
///
/// Il manifesto viene conservato per intero come JSON: e' il documento su cui l'operatore ha
/// deciso, e va poter essere riletto tale e quale. Il grant e' legato a <c>install_digest</c> e a
/// un progetto, quindi un aggiornamento — che ha un digest diverso — non lo eredita.
///
/// La revoca e' immediata per le nuove chiamate: si scrive <c>revoked_at_ms</c> invece di
/// cancellare la riga, cosi' l'audit conserva che quel permesso e' esistito.
/// </summary>
public sealed class SqlitePluginCatalog
{
    private readonly StoreWriter writer;
    private readonly SqliteConnectionFactory factory;
    private readonly IClock clock;

    public SqlitePluginCatalog(StoreWriter writer, SqliteConnectionFactory factory, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(clock);

        this.writer = writer;
        this.factory = factory;
        this.clock = clock;
    }

    /// <summary>Registra un pacchetto appena installato, nello stato <c>installed</c>.</summary>
    public async ValueTask RegisterAsync(
        string pluginId,
        string version,
        string installDigest,
        string manifestJson,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);

        long now = clock.UnixTimeMilliseconds;

        await writer.ExecuteAsync(
            "T-plugin-install",
            (connection, transaction) =>
            {
                Execute(
                    connection,
                    transaction,
                    "INSERT INTO plugins(id, version, install_digest, manifest_json, state, installed_at_ms) "
                    + "VALUES ($id, $version, $digest, $manifest, $state, $now);",
                    ("$id", pluginId),
                    ("$version", version),
                    ("$digest", installDigest),
                    ("$manifest", manifestJson),
                    ("$state", PluginState.Installed.ToWire()),
                    ("$now", now));

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Abilita un pacchetto per un progetto.
    ///
    /// Il grant non porta piu' permessi (S85): registra che l'operatore ha abilitato **questa**
    /// installazione per **questo** progetto, ed e' cio' che una skill pretende per entrare in un
    /// turno. Un pacchetto porta solo testo, quindi non c'e' nulla da concedere oltre a questo.
    /// </summary>
    public async ValueTask EnableAsync(
        string installDigest,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDigest);

        long now = clock.UnixTimeMilliseconds;

        await writer.ExecuteAsync(
            "T-plugin-enable",
            (connection, transaction) =>
            {
                Execute(
                    connection,
                    transaction,
                    "UPDATE plugins SET state = $state WHERE install_digest = $digest;",
                    ("$state", PluginState.Enabled.ToWire()),
                    ("$digest", installDigest));

                Execute(
                    connection,
                    transaction,
                    "INSERT INTO plugin_grants(install_digest, project_id, granted_at_ms, revoked_at_ms) "
                    + "VALUES ($digest, $project, $now, NULL) "
                    + "ON CONFLICT(install_digest, project_id) DO UPDATE SET "
                    + "granted_at_ms = excluded.granted_at_ms, revoked_at_ms = NULL;",
                    ("$digest", installDigest),
                    ("$project", projectId.ToString("D", CultureInfo.InvariantCulture)),
                    ("$now", now));

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revoca i grant di un pacchetto e lo marca disabilitato.</summary>
    public async ValueTask DisableAsync(string installDigest, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDigest);

        long now = clock.UnixTimeMilliseconds;

        await writer.ExecuteAsync(
            "T-plugin-disable",
            (connection, transaction) =>
            {
                Execute(
                    connection,
                    transaction,
                    "UPDATE plugins SET state = $state WHERE install_digest = $digest;",
                    ("$state", PluginState.Disabled.ToWire()),
                    ("$digest", installDigest));

                Execute(
                    connection,
                    transaction,
                    "UPDATE plugin_grants SET revoked_at_ms = $now WHERE install_digest = $digest AND revoked_at_ms IS NULL;",
                    ("$digest", installDigest),
                    ("$now", now));

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rimuove un pacchetto dal catalogo. I grant vengono revocati prima: <c>plugin_grants</c>
    /// referenzia l'installazione con ON DELETE RESTRICT, e la revoca resta comunque nell'audit.
    /// </summary>
    public async ValueTask RemoveAsync(string installDigest, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDigest);

        await DisableAsync(installDigest, cancellationToken).ConfigureAwait(false);

        await writer.ExecuteAsync(
            "T-plugin-remove",
            (connection, transaction) =>
            {
                Execute(
                    connection,
                    transaction,
                    "DELETE FROM plugin_grants WHERE install_digest = $digest;",
                    ("$digest", installDigest));

                Execute(
                    connection,
                    transaction,
                    "DELETE FROM plugins WHERE install_digest = $digest;",
                    ("$digest", installDigest));

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Righe del catalogo, con il manifesto conservato.</summary>
    public IReadOnlyList<PluginRecord> List()
    {
        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT id, version, install_digest, manifest_json, state, installed_at_ms FROM plugins ORDER BY id, version;";

        List<PluginRecord> plugins = [];

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            plugins.Add(new PluginRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                WireEnum.Parse<PluginState>(reader.GetString(4)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
        }

        return plugins;
    }

    /// <summary>Grant attivo di un'installazione per un progetto, se esiste.</summary>
    public PluginGrant? FindGrant(string installDigest, Guid projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDigest);

        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT granted_at_ms, revoked_at_ms FROM plugin_grants "
            + "WHERE install_digest = $digest AND project_id = $project;";

        command.Parameters.AddWithValue("$digest", installDigest);
        command.Parameters.AddWithValue("$project", projectId.ToString("D", CultureInfo.InvariantCulture));

        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        return new PluginGrant(
            installDigest,
            projectId,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
            reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)));
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
