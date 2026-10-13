using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PA.Router.Abstractions;

namespace PA.Router.Infrastructure.Crypto;

/// <summary>
/// Cifratura dei blob con AES-256-GCM: nonce casuale di 96 bit unico per chiave, tag di 128 bit
/// e AAD composta da sessione, blob e versione di schema. Il formato e' versionato in testa,
/// cosi' un cambio di schema del blob non viene interpretato con le regole vecchie.
///
/// Legare l'AAD ai tre identificatori impedisce di spostare un blob valido su un'altra sessione
/// o su un altro id mantenendo il tag valido.
/// </summary>
public sealed class BlobCipher : IDisposable
{
    /// <summary>Versione del formato del blob cifrato.</summary>
    public const byte FormatVersion = 1;

    /// <summary>Dimensione del nonce in byte.</summary>
    public const int NonceSize = 12;

    /// <summary>Dimensione del tag di autenticazione in byte.</summary>
    public const int TagSize = 16;

    private const int HeaderSize = 1 + NonceSize;

    private readonly byte[] key;

    /// <param name="key">Chiave dati di 32 byte. Il chiamante ne conserva la proprieta'.</param>
    public BlobCipher(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("The data key must be 32 bytes long.", nameof(key));
        }

        this.key = key.ToArray();
    }

    /// <summary>Cifra il contenuto e produce il file completo di intestazione e tag.</summary>
    public byte[] Encrypt(ContentBlobRequest request, ReadOnlySpan<byte> plaintext)
    {
        byte[] associatedData = BuildAssociatedData(request);
        byte[] output = new byte[HeaderSize + plaintext.Length + TagSize];

        output[0] = FormatVersion;
        Span<byte> nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        Span<byte> ciphertext = output.AsSpan(HeaderSize, plaintext.Length);
        Span<byte> tag = output.AsSpan(HeaderSize + plaintext.Length, TagSize);

        using AesGcm aes = new(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        return output;
    }

    /// <summary>
    /// Decifra il file. Un formato ignoto, una lunghezza impossibile o un tag non valido
    /// producono CONTENT_UNAVAILABLE: il contenuto non e' leggibile, e non ne viene restituita
    /// alcuna parte.
    /// </summary>
    public byte[] Decrypt(ContentBlobRequest request, ReadOnlySpan<byte> encrypted)
    {
        if (encrypted.Length < HeaderSize + TagSize)
        {
            throw Corrupted(request, "length");
        }

        if (encrypted[0] != FormatVersion)
        {
            throw Corrupted(request, "format");
        }

        byte[] associatedData = BuildAssociatedData(request);
        int plaintextLength = encrypted.Length - HeaderSize - TagSize;
        byte[] plaintext = new byte[plaintextLength];

        ReadOnlySpan<byte> nonce = encrypted.Slice(1, NonceSize);
        ReadOnlySpan<byte> ciphertext = encrypted.Slice(HeaderSize, plaintextLength);
        ReadOnlySpan<byte> tag = encrypted.Slice(HeaderSize + plaintextLength, TagSize);

        try
        {
            using AesGcm aes = new(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw Corrupted(request, "integrity", ex);
        }

        return plaintext;
    }

    /// <summary>
    /// AAD del blob. Nessun contenuto entra qui: solo identificatori gia' presenti nel database.
    /// </summary>
    private static byte[] BuildAssociatedData(ContentBlobRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BlobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);

        string associated = string.Join(
            '|',
            request.SessionId,
            request.BlobId,
            request.Kind.ToWire(),
            request.SchemaVersion.ToString(CultureInfo.InvariantCulture));

        return Encoding.UTF8.GetBytes(associated);
    }

    /// <summary>Azzera la copia locale della chiave.</summary>
    public void Dispose() => CryptographicOperations.ZeroMemory(key);

    private static PaRouterException Corrupted(ContentBlobRequest request, string reason, Exception? inner = null) =>
        new PaRouterException(
            ErrorCode.ContentUnavailable,
            "The stored content cannot be authenticated and was not returned.",
            inner)
            .WithMetadata("blobId", request.BlobId)
            .WithMetadata("reason", reason);
}
