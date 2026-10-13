namespace PA.Router.Abstractions;

/// <summary>
/// Riferimento a un blob da scrivere. L'id e' un UUID generato dall'applicazione: mai un path
/// ricevuto da HTTP o da un adapter.
/// </summary>
/// <param name="BlobId">UUID del blob.</param>
/// <param name="SessionId">Sessione proprietaria, usata come AAD della cifratura.</param>
/// <param name="Kind">Tipo di contenuto.</param>
/// <param name="SchemaVersion">Versione del formato del contenuto in chiaro.</param>
public readonly record struct ContentBlobRequest(
    string BlobId,
    string SessionId,
    BlobKind Kind,
    int SchemaVersion);

/// <summary>
/// Blob scritto. I valori corrispondono alle colonne di <c>content_blobs</c> e permettono di
/// verificare il file prima di decifrarlo.
/// </summary>
/// <param name="BlobId">UUID del blob.</param>
/// <param name="RelativePath">Path relativo alla directory dati, sempre sotto <c>content/</c>.</param>
/// <param name="CipherSha256">SHA-256 esadecimale del file cifrato.</param>
/// <param name="ByteLength">Lunghezza del file cifrato in byte.</param>
public readonly record struct ContentBlobReceipt(
    string BlobId,
    string RelativePath,
    string CipherSha256,
    long ByteLength);
