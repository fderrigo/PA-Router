using System.Text;
using PA.Router.Abstractions;
using PA.Router.Infrastructure.Crypto;
using PA.Router.Infrastructure.Storage;

namespace PA.Router.Hosting.Security;

/// <summary>
/// Vault locale dei segreti riferiti per nome dalla configurazione (03-specs/CONFIG.md).
///
/// Un file di configurazione contiene il **nome** di un segreto, mai il suo valore: i file di
/// configurazione finiscono nei backup, nelle copie di lavoro e negli incolla di una diagnosi. Il
/// valore vive cifrato con la chiave dati, e senza protector non viene scritto affatto: un segreto
/// in chiaro sarebbe peggio dell'assenza del segreto.
/// </summary>
public static class LocalSecretVault
{
    private const string VaultSessionId = "local-secret-vault";

    /// <summary>Nome del file che contiene un segreto cifrato.</summary>
    public static string FileName(string secretRef)
    {
        if (string.IsNullOrEmpty(secretRef) || secretRef.Length > 64 || secretRef[0] is < 'a' or > 'z')
        {
            throw InvalidSecretName();
        }

        foreach (char character in secretRef)
        {
            if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            {
                throw InvalidSecretName();
            }
        }

        return secretRef + ".secret.bin";
    }

    private static PaRouterException InvalidSecretName() => new(
        ErrorCode.InputInvalid,
        "Secret names must start with a lowercase letter and contain only lowercase letters, digits or hyphens (up to 64 characters).");

    /// <summary>Nomi dei segreti presenti, senza i valori.</summary>
    public static IReadOnlyList<string> List(StorePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (!Directory.Exists(paths.KeysDirectory))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(paths.KeysDirectory, "*.secret.bin")
                .Select(file => Path.GetFileName(file)[..^".secret.bin".Length])
                .Order(StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// Scrive un segreto, sostituendo quello esistente. Restituisce falso quando la piattaforma
    /// non offre un protector.
    /// </summary>
    public static bool TryWrite(StorePaths paths, IKeyProtector protector, string secretRef, string value)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(protector);
        string path = Path.Combine(paths.KeysDirectory, FileName(secretRef));
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!protector.IsAvailable)
        {
            return false;
        }

        using KeyRing keys = KeyRing.OpenOrCreate(paths, protector);

        byte[] encrypted = keys.Cipher.Encrypt(
            new ContentBlobRequest(secretRef, VaultSessionId, BlobKind.Capsule, SchemaVersion: 1),
            Encoding.UTF8.GetBytes(value));

        Directory.CreateDirectory(paths.KeysDirectory);

        // Scrittura atomica, come per la chiave dati: un'interruzione a meta' lascerebbe un segreto troncato, che per
        // il router vale come segreto assente e fa negare il turno.
        string temporary = path + ".tmp";

        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(encrypted, 0, encrypted.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);

        return true;
    }

    /// <summary>
    /// Legge un segreto. Restituisce null quando non esiste o quando non e' decifrabile: un
    /// segreto illeggibile e' un segreto assente, e il chiamante deve negare, non improvvisare.
    /// </summary>
    public static string? TryRead(StorePaths paths, IKeyProtector protector, string secretRef)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(protector);
        string path = Path.Combine(paths.KeysDirectory, FileName(secretRef));

        if (!File.Exists(path) || !protector.IsAvailable)
        {
            return null;
        }

        try
        {
            using KeyRing keys = KeyRing.OpenOrCreate(paths, protector);

            byte[] plain = keys.Cipher.Decrypt(
                new ContentBlobRequest(secretRef, VaultSessionId, BlobKind.Capsule, SchemaVersion: 1),
                File.ReadAllBytes(path));

            return Encoding.UTF8.GetString(plain);
        }
        catch (PaRouterException)
        {
            // Chiave cambiata o file manomesso: il segreto non esiste piu'.
            return null;
        }
    }
}
