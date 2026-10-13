using System.Security.Cryptography;
using System.Text;
using PA.Router.Abstractions;
using PA.Router.Infrastructure.Crypto;
using PA.Router.Infrastructure.Storage;

namespace PA.Router.Hosting.Security;

/// <summary>
/// Vault locale del token di servizio dell'API. Il token e' casuale, viene cifrato con la chiave
/// dati e non viene mai stampato dall'inizializzazione: l'esportazione verso il backend e'
/// un'operazione separata e esplicita dell'operatore, prevista in S17 (03-specs/CONFIG.md).
///
/// Il token e' confrontato per HMAC nel database: qui viene conservato perche' l'operatore deve
/// poterlo consegnare al proprio backend una volta sola, non perche' il runtime lo rilegga.
/// </summary>
public static class ServiceTokenVault
{
    private const int TokenBytes = 32;
    private const string VaultSessionId = "local-service-vault";

    /// <summary>Nome del file che contiene il token cifrato.</summary>
    public static string FileName(string tokenRef) => tokenRef + ".token.bin";

    /// <summary>
    /// Crea il token se non esiste. Restituisce false quando la piattaforma non offre un
    /// protector: in quel caso non viene scritto nulla, perche' un token in chiaro sarebbe
    /// peggio dell'assenza del token.
    /// </summary>
    public static bool TryCreate(StorePaths paths, IKeyProtector protector, string tokenRef, out string path)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenRef);

        path = Path.Combine(paths.KeysDirectory, FileName(tokenRef));

        if (File.Exists(path))
        {
            return true;
        }

        if (!protector.IsAvailable)
        {
            path = string.Empty;
            return false;
        }

        using KeyRing keys = KeyRing.OpenOrCreate(paths, protector);

        byte[] token = RandomNumberGenerator.GetBytes(TokenBytes);

        try
        {
            byte[] encrypted = keys.Cipher.Encrypt(
                new ContentBlobRequest(tokenRef, VaultSessionId, BlobKind.Capsule, SchemaVersion: 1),
                Encoding.UTF8.GetBytes(Convert.ToBase64String(token)));

            Directory.CreateDirectory(paths.KeysDirectory);
            File.WriteAllBytes(path, encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }

        return true;
    }
}
