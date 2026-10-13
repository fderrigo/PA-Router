using System.Collections.Concurrent;
using System.Security.Cryptography;
using PA.Router.Abstractions;

namespace PA.Router.Infrastructure.Content;

/// <summary>
/// Archivio effimero per il modo memory_only: nessuna riga e nessun blob toccano il disco e il
/// contenuto sparisce con il worker.
///
/// Il path restituito usa il prefisso <c>memory/</c> proprio per non essere inseribile: il CHECK
/// di <c>content_blobs</c> accetta solo <c>content/%</c>, quindi un tentativo di persistere un
/// blob effimero viene respinto dal database invece di passare inosservato.
/// </summary>
public sealed class EphemeralContentStore : IContentStore, IDisposable
{
    /// <summary>Prefisso dei riferimenti effimeri, volutamente non persistibile.</summary>
    public const string MemoryPrefix = "memory/";

    private readonly ConcurrentDictionary<string, byte[]> blobs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ContentMode Mode => ContentMode.MemoryOnly;

    /// <summary>Blob attualmente in memoria.</summary>
    public int Count => blobs.Count;

    /// <inheritdoc />
    public ValueTask<ContentBlobReceipt> WriteAsync(
        ContentBlobRequest request,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BlobId);
        cancellationToken.ThrowIfCancellationRequested();

        if (plaintext.Length == 0)
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "An empty blob cannot be stored.")
                .WithMetadata("blobId", request.BlobId);
        }

        byte[] copy = plaintext.ToArray();
        blobs[request.BlobId] = copy;

        return ValueTask.FromResult(new ContentBlobReceipt(
            request.BlobId,
            MemoryPrefix + request.BlobId,
            Convert.ToHexStringLower(SHA256.HashData(copy)),
            copy.LongLength));
    }

    /// <inheritdoc />
    public ValueTask<byte[]> ReadAsync(
        ContentBlobRequest request,
        ContentBlobReceipt receipt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!blobs.TryGetValue(request.BlobId, out byte[]? stored))
        {
            throw new PaRouterException(
                ErrorCode.ContentUnavailable,
                "The ephemeral content is no longer available.")
                .WithMetadata("blobId", request.BlobId)
                .WithMetadata("reason", "missing");
        }

        return ValueTask.FromResult(stored.ToArray());
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(ContentBlobReceipt receipt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (blobs.TryRemove(receipt.BlobId, out byte[]? removed))
        {
            CryptographicOperations.ZeroMemory(removed);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (byte[] blob in blobs.Values)
        {
            CryptographicOperations.ZeroMemory(blob);
        }

        blobs.Clear();
    }
}
