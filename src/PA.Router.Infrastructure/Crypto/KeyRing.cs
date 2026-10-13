using System.Security.Cryptography;
using System.Text;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Infrastructure.Storage;

namespace PA.Router.Infrastructure.Crypto;

/// <summary>
/// Chiavi locali dello store. La chiave dati e' casuale di 32 byte, conservata solo in forma
/// protetta dal sistema operativo; la chiave HMAC per i metadata e' derivata con HKDF e non
/// coincide mai con la chiave dati.
///
/// Token di sessione e di servizio non passano di qui: vengono confrontati come HMAC, non
/// cifrati come password recuperabili (03-specs/STORAGE-AUDIT.md).
/// </summary>
public sealed class KeyRing : IMetadataDigest, IDisposable
{
    /// <summary>Label HKDF della chiave HMAC dei metadata, fissata dalla specifica.</summary>
    public const string MetadataHmacLabel = "pa-router-metadata-v1";

    /// <summary>Versione del formato del file di chiave protetta.</summary>
    public const byte FileFormatVersion = 1;

    private const int DataKeySize = 32;

    private readonly byte[] dataKey;
    private readonly byte[] metadataHmacKey;
    private bool disposed;

    private KeyRing(byte[] dataKey, string protectorName)
    {
        this.dataKey = dataKey;
        metadataHmacKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: dataKey,
            outputLength: 32,
            salt: null,
            info: Encoding.UTF8.GetBytes(MetadataHmacLabel));

        ProtectorName = protectorName;
        Cipher = new BlobCipher(dataKey);
    }

    /// <summary>Protector usato, per le diagnosi.</summary>
    public string ProtectorName { get; }

    /// <summary>Cifratura dei blob con la chiave dati.</summary>
    public BlobCipher Cipher { get; }

    /// <summary>
    /// Apre le chiavi esistenti o ne crea di nuove. Se il protector non e' disponibile l'errore
    /// e' CRYPTO_UNAVAILABLE: il chiamante deve ricadere su memory_only, mai su testo in chiaro.
    /// </summary>
    public static KeyRing OpenOrCreate(StorePaths paths, IKeyProtector protector)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(protector);

        if (!protector.IsAvailable)
        {
            throw new PaRouterException(
                ErrorCode.CryptoUnavailable,
                "No key protector is available on this platform, so persistent encryption is disabled.")
                .WithMetadata("protector", protector.Name);
        }

        Directory.CreateDirectory(paths.KeysDirectory);

        return File.Exists(paths.MasterKeyFile)
            ? Open(paths, protector)
            : Create(paths, protector);
    }

    private static KeyRing Open(StorePaths paths, IKeyProtector protector)
    {
        byte[] stored = File.ReadAllBytes(paths.MasterKeyFile);

        if (stored.Length < 2 || stored[0] != FileFormatVersion)
        {
            throw new PaRouterException(
                ErrorCode.CryptoUnavailable,
                "The protected key file has an unsupported format.");
        }

        byte[] dataKey = protector.Unprotect(stored.AsSpan(1));

        if (dataKey.Length != DataKeySize)
        {
            CryptographicOperations.ZeroMemory(dataKey);
            throw new PaRouterException(
                ErrorCode.CryptoUnavailable,
                "The protected key file does not contain a usable data key.");
        }

        return new KeyRing(dataKey, protector.Name);
    }

    private static KeyRing Create(StorePaths paths, IKeyProtector protector)
    {
        byte[] dataKey = RandomNumberGenerator.GetBytes(DataKeySize);
        byte[] protectedKey = protector.Protect(dataKey);

        byte[] file = new byte[1 + protectedKey.Length];
        file[0] = FileFormatVersion;
        protectedKey.CopyTo(file, 1);

        // Scrittura atomica: un crash a meta' non deve lasciare una chiave troncata al posto
        // di quella valida.
        string temporary = paths.MasterKeyFile + ".tmp";

        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(file, 0, file.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, paths.MasterKeyFile, overwrite: false);

        return new KeyRing(dataKey, protector.Name);
    }

    /// <summary>
    /// HMAC-SHA256 esadecimale dei metadata, per i digest salvati nel database. Il valore non
    /// e' invertibile e non contiene il testo di origine.
    /// </summary>
    public string ComputeMetadataHmac(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Convert.ToHexStringLower(HMACSHA256.HashData(metadataHmacKey, data));
    }

    /// <inheritdoc />
    public string Compute(ReadOnlySpan<byte> data) => ComputeMetadataHmac(data);

    /// <summary>Variante testuale di <see cref="ComputeMetadataHmac(ReadOnlySpan{byte})"/>.</summary>
    public string ComputeMetadataHmac(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ComputeMetadataHmac(Encoding.UTF8.GetBytes(value));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Cipher.Dispose();
        CryptographicOperations.ZeroMemory(dataKey);
        CryptographicOperations.ZeroMemory(metadataHmacKey);
    }
}
