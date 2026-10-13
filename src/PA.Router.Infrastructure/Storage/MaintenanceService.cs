using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Infrastructure.Storage;

/// <summary>Conteggi rimossi da una passata di retention.</summary>
/// <param name="Contents">Blob di contenuto scaduti.</param>
/// <param name="Events">Eventi di stato scaduti.</param>
/// <param name="IdempotencyKeys">Chiavi di idempotenza scadute.</param>
/// <param name="AuditEvents">Righe di audit oltre la retention.</param>
/// <param name="OrphanFiles">File di blob senza riga, rimossi dallo sweep.</param>
public sealed record RetentionReport(int Contents, int Events, int IdempotencyKeys, int AuditEvents, int OrphanFiles)
{
    /// <summary>Ricordi la cui conservazione dichiarata e' finita, avviati alla rimozione.</summary>
    public int Memories { get; init; }
}

/// <summary>Esito di un backup.</summary>
/// <param name="Directory">Directory che contiene la copia.</param>
/// <param name="Files">File copiati.</param>
/// <param name="ManifestPath">Manifesto con i digest.</param>
public sealed record BackupReport(string Directory, int Files, string ManifestPath);

/// <summary>
/// Manutenzione dello store: retention e backup (03-specs/STORAGE-AUDIT.md).
///
/// La retention rimuove in ordine di dipendenza — prima i figli, poi cio' che li referenzia — e
/// registra un evento aggregato che dice quanto e' stato tolto, non che cosa. Le sessioni con
/// esecuzioni vive non vengono toccate: il vincolo dello schema lo impedirebbe comunque, e va bene
/// cosi'.
///
/// Il backup copia database, WAL, blob e chiave protetta insieme, con un manifesto di digest.
/// Copiare il solo <c>router.db</c> ignorando il WAL produrrebbe una copia che sembra valida e non
/// lo e'. La copia resta legata a utente e macchina: DPAPI non si sposta, e questa beta non promette
/// una migrazione delle chiavi.
/// </summary>
public sealed class MaintenanceService
{
    /// <summary>Durata dei contenuti di conversazione.</summary>
    public static readonly TimeSpan ContentRetention = TimeSpan.FromDays(7);

    /// <summary>Durata di eventi SSE e chiavi di idempotenza.</summary>
    public static readonly TimeSpan EventRetention = TimeSpan.FromHours(24);

    /// <summary>Durata delle righe di audit.</summary>
    public static readonly TimeSpan AuditRetention = TimeSpan.FromDays(30);

    /// <summary>Attesa prima di considerare orfano un file senza riga.</summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(24);

    private readonly StoreWriter writer;
    private readonly SqliteConnectionFactory factory;
    private readonly StorePaths paths;
    private readonly IClock clock;
    private readonly IIdGenerator ids;

    public MaintenanceService(
        StoreWriter writer,
        SqliteConnectionFactory factory,
        StorePaths paths,
        IClock clock,
        IIdGenerator ids)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ids);

        this.writer = writer;
        this.factory = factory;
        this.paths = paths;
        this.clock = clock;
        this.ids = ids;
    }

    /// <summary>
    /// Applica la retention. Restituisce i conteggi e scrive un evento aggregato; i file dei blob
    /// rimossi vengono cancellati dopo il commit, cosi' una interruzione lascia al massimo orfani e
    /// mai righe che puntano a file assenti.
    /// </summary>
    public ValueTask<RetentionReport> ApplyRetentionAsync(IContentStore content, CancellationToken cancellationToken) =>
        ApplyRetentionAsync(content, memory: null, cancellationToken);

    /// <summary>
    /// Come sopra, con la memoria: i ricordi la cui conservazione e' finita vengono avviati alla rimozione. Senza
    /// questa chiamata resterebbero sul disco per sempre, perche' i loro blob non seguono la scadenza dei transcript.
    /// </summary>
    public async ValueTask<RetentionReport> ApplyRetentionAsync(
        IContentStore content,
        IExpiringMemory? memory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        int memories = memory is null
            ? 0
            : await memory.TombstoneExpiredAsync(cancellationToken).ConfigureAwait(false);

        long now = clock.UnixTimeMilliseconds;
        long auditCutoff = clock.UtcNow.Subtract(AuditRetention).ToUnixTimeMilliseconds();

        List<ContentBlobReceipt> expired = [];

        using (SqliteConnection connection = factory.Open())
        {
            using SqliteCommand command = connection.CreateCommand();

            // I blob di osservazioni ed embedding seguono la scadenza della memoria, non quella dei
            // transcript: qui non si toccano.
            command.CommandText =
                "SELECT id, relative_path, cipher_sha256, byte_length FROM content_blobs "
                + "WHERE expires_at_ms <= $now AND kind NOT IN ('observation','embedding');";

            command.Parameters.AddWithValue("$now", now);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                expired.Add(new ContentBlobReceipt(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3)));
            }
        }

        RetentionReport report = await writer.ExecuteAsync(
            "T-retention",
            (connection, transaction) =>
            {
                int contents = Execute(
                    connection,
                    transaction,
                    "DELETE FROM content_blobs WHERE expires_at_ms <= $now AND kind NOT IN ('observation','embedding');",
                    ("$now", now));

                int events = Execute(
                    connection,
                    transaction,
                    "DELETE FROM execution_events WHERE expires_at_ms <= $now;",
                    ("$now", now));

                int keys = Execute(
                    connection,
                    transaction,
                    "DELETE FROM idempotency_keys WHERE expires_at_ms <= $now;",
                    ("$now", now));

                int audits = Execute(
                    connection,
                    transaction,
                    "DELETE FROM audit_events WHERE created_at_ms <= $cutoff;",
                    ("$cutoff", auditCutoff));

                // L'evento aggregato dice quanto, mai che cosa.
                Execute(
                    connection,
                    transaction,
                    "INSERT INTO audit_events(event_id, execution_id, attempt_id, code, metadata_json, created_at_ms) "
                    + "VALUES ($event, NULL, NULL, 'retention_applied', $metadata, $now);",
                    ("$event", ids.NewId()),
                    ("$metadata",
                        "{\"contents\":" + contents.ToString(CultureInfo.InvariantCulture)
                        + ",\"events\":" + events.ToString(CultureInfo.InvariantCulture)
                        + ",\"idempotencyKeys\":" + keys.ToString(CultureInfo.InvariantCulture)
                        + ",\"auditEvents\":" + audits.ToString(CultureInfo.InvariantCulture) + "}"),
                    ("$now", now));

                return new RetentionReport(contents, events, keys, audits, 0);
            },
            cancellationToken).ConfigureAwait(false);

        foreach (ContentBlobReceipt receipt in expired)
        {
            await content.DeleteAsync(receipt, cancellationToken).ConfigureAwait(false);
        }

        int orphans = SweepOrphanFiles(now);

        Checkpoint();

        return report with { OrphanFiles = orphans, Memories = memories };
    }


    /// <summary>
    /// Rimuove i file di blob che nessuna riga referenzia e che sono piu' vecchi della finestra di
    /// grazia. La grazia serve: un blob viene scritto prima della transazione che lo registra, e
    /// cancellarlo subito significherebbe togliere il contenuto a un'esecuzione in corso.
    /// </summary>
    private int SweepOrphanFiles(long now)
    {
        if (!Directory.Exists(paths.ContentDirectory))
        {
            return 0;
        }

        HashSet<string> referenced = new(StringComparer.OrdinalIgnoreCase);

        using (SqliteConnection connection = factory.Open())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT relative_path FROM content_blobs;";

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                referenced.Add(paths.ResolveContentPath(reader.GetString(0)));
            }
        }

        long cutoff = now - (long)OrphanGrace.TotalMilliseconds;
        int removed = 0;

        foreach (string file in Directory.EnumerateFiles(paths.ContentDirectory, "*", SearchOption.AllDirectories))
        {
            if (referenced.Contains(file))
            {
                continue;
            }

            try
            {
                if (new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero).ToUnixTimeMilliseconds() > cutoff)
                {
                    continue;
                }

                File.Delete(file);
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    /// <summary>
    /// Checkpoint del WAL a riposo. Senza, il file laterale cresce e una copia presa altrove
    /// conterrebbe meno di quanto il database ha gia' accettato.
    /// </summary>
    public void Checkpoint()
    {
        using SqliteConnection connection = factory.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Copia lo store in una directory nuova: database con l'API di backup di SQLite — che include
    /// il WAL — piu' blob e chiave protetta, con un manifesto di digest.
    /// </summary>
    public BackupReport Backup(string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        string destination = Path.GetFullPath(destinationDirectory);

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            // Sovrascrivere un backup esistente renderebbe impossibile dire quale copia si ha.
            throw new PaRouterException(ErrorCode.InputInvalid, "The backup directory is not empty.")
                .WithMetadata("reason", "backup_directory_not_empty");
        }

        Directory.CreateDirectory(destination);

        Checkpoint();

        string databaseCopy = Path.Combine(destination, Path.GetFileName(paths.DatabaseFile));

        using (SqliteConnection source = factory.Open())
        {
            using SqliteConnection target = new("Data Source=" + databaseCopy);
            target.Open();

            // L'API di backup legge uno stato coerente: copiare il file a mano ignorando il WAL
            // produrrebbe un database che sembra valido e non lo e'.
            source.BackupDatabase(target);
            target.Close();

            // Il pool terrebbe aperto il file appena scritto e il digest non potrebbe leggerlo.
            SqliteConnection.ClearPool(target);
        }

        List<string> copied = [databaseCopy];

        copied.AddRange(CopyTree(paths.ContentDirectory, Path.Combine(destination, "content")));

        // Chiave dati **e** segreti dei servizi: una copia con la sola chiave si ripristina e poi non autentica nulla,
        // e il guasto si scoprirebbe al primo turno.
        if (Directory.Exists(paths.KeysDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(paths.KeysDirectory).Order(StringComparer.Ordinal))
            {
                string keyCopy = Path.Combine(destination, "keys", Path.GetFileName(file));

                Directory.CreateDirectory(Path.GetDirectoryName(keyCopy)!);
                File.Copy(file, keyCopy);
                copied.Add(keyCopy);
            }
        }

        string manifestPath = Path.Combine(destination, "backup-manifest.txt");

        StringBuilder manifest = new();

        manifest.Append("# PA Router backup ")
            .Append(clock.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            .Append('\n');

        // La chiave protetta resta legata a utente e macchina: una copia portata altrove non si
        // apre, e va detto qui invece di scoprirlo al restore.
        manifest.Append("# La chiave protetta vale solo per questo utente e questa macchina.\n");

        foreach (string file in copied.OrderBy(path => path, StringComparer.Ordinal))
        {
            manifest.Append(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))))
                .Append("  ")
                .Append(Path.GetRelativePath(destination, file).Replace('\\', '/'))
                .Append('\n');
        }

        File.WriteAllText(manifestPath, manifest.ToString());

        return new BackupReport(destination, copied.Count, manifestPath);
    }

    /// <summary>
    /// Verifica una copia: ogni file elencato dal manifesto deve esistere con il proprio digest.
    /// Non ripristina e non avvia nulla — il ripristino e' una decisione dell'operatore, e un
    /// riavvio automatico dopo un restore riprenderebbe un lavoro di cui nessuno conosce lo stato.
    /// </summary>
    public static bool Verify(string backupDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);

        string manifestPath = Path.Combine(backupDirectory, "backup-manifest.txt");

        if (!File.Exists(manifestPath))
        {
            return false;
        }

        foreach (string line in File.ReadAllLines(manifestPath))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] parts = line.Split("  ", 2, StringSplitOptions.None);

            if (parts.Length != 2)
            {
                return false;
            }

            string file = Path.Combine(backupDirectory, parts[1].Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(file)
                || !string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))),
                    parts[0],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<string> CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return [];
        }

        List<string> copied = [];

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);

            copied.Add(target);
        }

        return copied;
    }

    private static int Execute(
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

        return command.ExecuteNonQuery();
    }
}
