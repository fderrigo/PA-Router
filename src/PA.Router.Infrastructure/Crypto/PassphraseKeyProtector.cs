using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PA.Router.Abstractions;

namespace PA.Router.Infrastructure.Crypto;

/// <summary>
/// Protezione della chiave dati con una passphrase dell'operatore, per le piattaforme senza DPAPI.
///
/// Perche' una passphrase e non un file con permessi 0600: un file protetto dai soli permessi non
/// e' cifrato: chi legge il disco, ne fa un backup o diventa root lo apre. DPAPI su Windows lega la
/// chiave alle credenziali di sessione dell'utente, e l'unico equivalente su Linux che non dipenda
/// da un demone di sessione — assente su una macchina headless, che e' il caso d'uso principale —
/// e' un segreto che l'operatore fornisce.
///
/// La passphrase arriva da <c>PA_ROUTER_PASSPHRASE</c> oppure, se il terminale e' interattivo, da
/// una richiesta a video. Se non e' ottenibile il protector si dichiara indisponibile e il modo
/// persistente non parte: non esiste una variante in chiaro.
///
/// La chiave di cifratura si deriva con PBKDF2-HMAC-SHA512 a 600.000 iterazioni, sopra la soglia
/// raccomandata da OWASP per SHA-512. La derivazione e' nello standard e non introduce alcuna
/// dipendenza: per un prodotto che promette riservatezza, una libreria di terze parti dentro il
/// percorso della chiave e' superficie che conviene non avere.
///
/// L'identificativo della macchina entra come dato associato di AES-GCM: come DPAPI, un blob
/// copiato altrove non si apre nemmeno con la passphrase giusta.
/// </summary>
public sealed class PassphraseKeyProtector : IKeyProtector
{
    /// <summary>Variabile d'ambiente da cui il worker, che non ha terminale, prende il segreto.</summary>
    public const string PassphraseVariable = "PA_ROUTER_PASSPHRASE";

    private const int Iterations = 600_000;

    private const int SaltLength = 32;

    private const int NonceLength = 12;

    private const int TagLength = 16;

    private const int KeyLength = 32;

    private static readonly byte[] Magic = "PAK1"u8.ToArray();

    private readonly Func<string?> passphraseSource;

    // La derivazione costa quasi un secondo: senza questa cache ogni lettura del vault la
    // ripagherebbe. Vive in memoria e muore con il processo.
    private readonly ConcurrentDictionary<string, byte[]> derived = new(StringComparer.Ordinal);

    private string? passphrase;

    /// <summary>Costruisce il protector con la sorgente predefinita: ambiente, poi terminale.</summary>
    public PassphraseKeyProtector()
        : this(ReadFromEnvironmentOrConsole)
    {
    }

    /// <summary>Costruisce il protector con una sorgente esplicita della passphrase.</summary>
    /// <param name="passphraseSource">Restituisce la passphrase, o <c>null</c> se non ottenibile.</param>
    public PassphraseKeyProtector(Func<string?> passphraseSource)
    {
        ArgumentNullException.ThrowIfNull(passphraseSource);
        this.passphraseSource = passphraseSource;
    }

    /// <inheritdoc />
    public string Name => "passphrase-pbkdf2-sha512";

    /// <inheritdoc />
    public bool IsAvailable => TryGetPassphrase() is not null;

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plaintextKey)
    {
        string secret = RequirePassphrase();

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] key = DeriveKey(secret, salt);

        byte[] ciphertext = new byte[plaintextKey.Length];
        byte[] tag = new byte[TagLength];

        try
        {
            using AesGcm aes = new(key, TagLength);
            aes.Encrypt(nonce, plaintextKey, ciphertext, tag, AssociatedData());
        }
        catch (CryptographicException ex)
        {
            throw new PaRouterException(
                ErrorCode.CryptoUnavailable,
                "The passphrase key protector refused to protect the data key.",
                ex);
        }

        byte[] blob = new byte[Magic.Length + 4 + SaltLength + NonceLength + TagLength + ciphertext.Length];
        int offset = 0;

        Magic.CopyTo(blob.AsSpan(offset));
        offset += Magic.Length;

        // Il numero di iterazioni viaggia con il blob: alzarlo in futuro non deve rendere
        // illeggibile cio' che e' gia' stato scritto.
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(offset), Iterations);
        offset += 4;

        salt.CopyTo(blob.AsSpan(offset));
        offset += SaltLength;

        nonce.CopyTo(blob.AsSpan(offset));
        offset += NonceLength;

        tag.CopyTo(blob.AsSpan(offset));
        offset += TagLength;

        ciphertext.CopyTo(blob.AsSpan(offset));

        return blob;
    }

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedKey)
    {
        string secret = RequirePassphrase();

        int header = Magic.Length + 4 + SaltLength + NonceLength + TagLength;

        if (protectedKey.Length < header || !protectedKey[..Magic.Length].SequenceEqual(Magic))
        {
            throw new PaRouterException(
                ErrorCode.CryptoUnavailable,
                "The protected data key is not in the format written by this key protector.");
        }

        int offset = Magic.Length;

        int iterations = BinaryPrimitives.ReadInt32LittleEndian(protectedKey[offset..]);
        offset += 4;

        if (iterations is < 1 or > 10_000_000)
        {
            throw new PaRouterException(
                ErrorCode.CryptoUnavailable,
                "The protected data key declares an iteration count outside the accepted range.");
        }

        byte[] salt = protectedKey.Slice(offset, SaltLength).ToArray();
        offset += SaltLength;

        byte[] nonce = protectedKey.Slice(offset, NonceLength).ToArray();
        offset += NonceLength;

        byte[] tag = protectedKey.Slice(offset, TagLength).ToArray();
        offset += TagLength;

        byte[] ciphertext = protectedKey[offset..].ToArray();
        byte[] key = DeriveKey(secret, salt, iterations);
        byte[] plaintext = new byte[ciphertext.Length];

        try
        {
            using AesGcm aes = new(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData());
        }
        catch (CryptographicException ex)
        {
            // Passphrase diversa, macchina diversa o file manomesso: da fuori sono lo stesso
            // errore, ed e' voluto — distinguerli direbbe a chi prova quale delle tre ha sbagliato.
            throw new PaRouterException(
                ErrorCode.CryptoUnavailable,
                "The protected data key cannot be read with the passphrase available on this machine.",
                ex);
        }

        return plaintext;
    }

    private byte[] DeriveKey(string secret, byte[] salt, int iterations = Iterations)
    {
        string cacheKey = string.Concat(
            iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ":",
            Convert.ToHexString(salt));

        return derived.GetOrAdd(
            cacheKey,
            _ => Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(secret),
                salt,
                iterations,
                HashAlgorithmName.SHA512,
                KeyLength));
    }

    private string RequirePassphrase() =>
        TryGetPassphrase()
        ?? throw new PaRouterException(
            ErrorCode.CryptoUnavailable,
            "No passphrase is available, so the encrypted content mode is unavailable. "
            + $"Set {PassphraseVariable} or run the command from an interactive terminal.");

    private string? TryGetPassphrase()
    {
        if (passphrase is not null)
        {
            return passphrase;
        }

        string? value = passphraseSource();

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        passphrase = value;
        return passphrase;
    }

    private static string? ReadFromEnvironmentOrConsole()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(PassphraseVariable);

        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        // Un terminale non interattivo non deve bloccarsi ad aspettare: il worker e i test ne
        // hanno uno rediretto, e la' la risposta corretta e' «non disponibile».
        if (Console.IsInputRedirected)
        {
            return null;
        }

        Console.Error.Write("PA Router passphrase: ");

        StringBuilder builder = new();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key is ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                break;
            }

            if (key.Key is ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }

        return builder.Length is 0 ? null : builder.ToString();
    }

    /// <summary>
    /// Dato associato di AES-GCM: etichetta di formato piu' identificativo della macchina. Senza
    /// identificativo resta la sola etichetta, e il blob perde il legame con la macchina: viene
    /// detto nella documentazione invece di essere simulato con un valore inventato.
    /// </summary>
    private static byte[] AssociatedData()
    {
        string machine = MachineIdentifier() ?? string.Empty;
        return Encoding.UTF8.GetBytes("pa-router-master-key-v1|" + machine);
    }

    private static string? MachineIdentifier()
    {
        foreach (string candidate in (string[])["/etc/machine-id", "/var/lib/dbus/machine-id"])
        {
            try
            {
                if (File.Exists(candidate))
                {
                    string value = File.ReadAllText(candidate).Trim();

                    if (value.Length > 0)
                    {
                        return value;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Illeggibile: si prova il candidato successivo, poi si rinuncia al legame.
            }
        }

        return null;
    }
}
