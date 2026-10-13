namespace PA.Router.Infrastructure.Storage;

/// <summary>
/// Layout della directory dati dell'operatore descritto in 03-specs/STORAGE-AUDIT.md.
/// Tutti i path derivano dalla radice: nessun componente arriva da HTTP o da un adapter.
/// </summary>
public sealed class StorePaths
{
    /// <summary>Prefisso relativo dei blob, verificato anche dal CHECK di content_blobs.</summary>
    public const string ContentPrefix = "content/";

    private StorePaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        DatabaseFile = Path.Combine(dataDirectory, "router.db");
        LockFile = Path.Combine(dataDirectory, "store.lock");
        ContentDirectory = Path.Combine(dataDirectory, "content");
        KeysDirectory = Path.Combine(dataDirectory, "keys");
        MasterKeyFile = Path.Combine(KeysDirectory, "master.dpapi");
        LogsDirectory = Path.Combine(dataDirectory, "logs");
        PoliciesDirectory = Path.Combine(dataDirectory, "policies");
        ModelsDirectory = Path.Combine(dataDirectory, "models");
    }

    /// <summary>Radice della directory dati, in forma canonica.</summary>
    public string DataDirectory { get; }

    public string DatabaseFile { get; }

    public string LockFile { get; }

    public string ContentDirectory { get; }

    public string KeysDirectory { get; }

    public string MasterKeyFile { get; }

    public string LogsDirectory { get; }

    public string PoliciesDirectory { get; }

    /// <summary>
    /// Modelli locali installati dall'operatore, uno per sottodirectory. Il registro ne dichiara
    /// il nome; il percorso non passa mai dalla configurazione.
    /// </summary>
    public string ModelsDirectory { get; }

    /// <summary>Costruisce il layout a partire dalla radice, senza toccare il disco.</summary>
    public static StorePaths For(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return new StorePaths(Path.GetFullPath(dataDirectory));
    }

    /// <summary>Crea le directory mancanti. Idempotente.</summary>
    public StorePaths EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ContentDirectory);
        Directory.CreateDirectory(KeysDirectory);
        Directory.CreateDirectory(LogsDirectory);
        return this;
    }

    /// <summary>Path relativo di un blob, nella forma accettata dallo schema.</summary>
    public static string RelativeBlobPath(string blobId) => ContentPrefix + blobId + ".bin";

    /// <summary>
    /// Risolve un path relativo del DB in un path assoluto, rifiutando qualunque forma che
    /// esca dalla directory dei contenuti.
    /// </summary>
    public string ResolveContentPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (!relativePath.StartsWith(ContentPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Blob paths must start with the content prefix.", nameof(relativePath));
        }

        string candidate = Path.GetFullPath(Path.Combine(DataDirectory, relativePath));
        string root = ContentDirectory + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(root, StringComparison.Ordinal))
        {
            throw new ArgumentException("Blob paths must stay inside the content directory.", nameof(relativePath));
        }

        return candidate;
    }
}
