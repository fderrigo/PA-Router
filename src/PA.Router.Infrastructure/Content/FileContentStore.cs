using System.Security.Cryptography;
using PA.Router.Abstractions;
using PA.Router.Infrastructure.Crypto;
using PA.Router.Infrastructure.Storage;

namespace PA.Router.Infrastructure.Content;

/// <summary>
/// Blob cifrati su file. La scrittura passa da un file temporaneo con fsync e da un rename,
/// cosi' un crash lascia al massimo un temporaneo, mai un blob referenziato e incompleto.
/// La lettura verifica il digest del file prima di decifrare: un file sostituito viene
/// rifiutato anche prima che il tag GCM entri in gioco.
/// </summary>
public sealed class FileContentStore : IContentStore
{
    private const string TemporarySuffix = ".tmp";

    private readonly StorePaths paths;
    private readonly BlobCipher cipher;

    public FileContentStore(StorePaths paths, BlobCipher cipher)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(cipher);

        this.paths = paths;
        this.cipher = cipher;
    }

    /// <inheritdoc />
    public ContentMode Mode => ContentMode.Encrypted;

    /// <inheritdoc />
    public async ValueTask<ContentBlobReceipt> WriteAsync(
        ContentBlobRequest request,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BlobId);

        if (plaintext.Length == 0)
        {
            // byte_length > 0 e' un CHECK dello schema: un blob vuoto non e' rappresentabile.
            throw new PaRouterException(ErrorCode.InputInvalid, "An empty blob cannot be stored.")
                .WithMetadata("blobId", request.BlobId);
        }

        Directory.CreateDirectory(paths.ContentDirectory);

        string relativePath = StorePaths.RelativeBlobPath(request.BlobId);
        string finalPath = paths.ResolveContentPath(relativePath);
        string temporaryPath = finalPath + TemporarySuffix;

        byte[] encrypted = cipher.Encrypt(request, plaintext.Span);

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                await stream.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, finalPath, overwrite: false);
        }
        catch (IOException ex)
        {
            TryDelete(temporaryPath);
            throw new PaRouterException(
                ErrorCode.StoreUnavailable,
                "The content blob could not be written to the data directory.",
                ex)
                .WithMetadata("blobId", request.BlobId);
        }

        return new ContentBlobReceipt(
            request.BlobId,
            relativePath,
            Convert.ToHexStringLower(SHA256.HashData(encrypted)),
            encrypted.Length);
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> ReadAsync(
        ContentBlobRequest request,
        ContentBlobReceipt receipt,
        CancellationToken cancellationToken)
    {
        string path = paths.ResolveContentPath(receipt.RelativePath);

        byte[] encrypted;
        try
        {
            // Open directly: a purge can remove the file after an Exists check. Only absence
            // becomes "missing"; access errors, cancellation and integrity failures stay distinct.
            encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw Unavailable(request, "missing");
        }

        if (encrypted.LongLength != receipt.ByteLength)
        {
            throw Unavailable(request, "length");
        }

        string digest = Convert.ToHexStringLower(SHA256.HashData(encrypted));

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(digest),
                Convert.FromHexString(receipt.CipherSha256)))
        {
            throw Unavailable(request, "digest");
        }

        return cipher.Decrypt(request, encrypted);
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(ContentBlobReceipt receipt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string path = paths.ResolveContentPath(receipt.RelativePath);
        TryDelete(path);
        TryDelete(path + TemporarySuffix);

        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Il file resta al prossimo sweep: la cancellazione non deve mascherare l'errore
            // che l'ha resa necessaria.
        }
        catch (UnauthorizedAccessException)
        {
            // Come sopra.
        }
    }

    private static PaRouterException Unavailable(ContentBlobRequest request, string reason) =>
        new PaRouterException(
            ErrorCode.ContentUnavailable,
            "The stored content is missing or does not match its recorded digest.")
            .WithMetadata("blobId", request.BlobId)
            .WithMetadata("reason", reason);
}
