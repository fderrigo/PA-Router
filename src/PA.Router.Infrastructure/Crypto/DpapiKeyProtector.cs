using System.Security.Cryptography;
using System.Text;
using PA.Router.Abstractions;

namespace PA.Router.Infrastructure.Crypto;

/// <summary>
/// Protezione della chiave dati con DPAPI CurrentUser: la chiave protetta resta legata a utente
/// e macchina. Su piattaforme senza DPAPI il protector si dichiara indisponibile e il modo
/// persistente non parte; non esiste una variante in chiaro.
/// </summary>
public sealed class DpapiKeyProtector : IKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("pa-router-master-key-v1");

    /// <inheritdoc />
    public string Name => "dpapi-current-user";

    /// <inheritdoc />
    public bool IsAvailable => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plaintextKey)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Unavailable();
        }

        try
        {
            return ProtectedData.Protect(plaintextKey.ToArray(), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new PaRouterException(
                ErrorCode.CryptoUnavailable,
                "The operating system key protector refused to protect the data key.",
                ex);
        }
    }

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedKey)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Unavailable();
        }

        try
        {
            return ProtectedData.Unprotect(protectedKey.ToArray(), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            // Chiave di un altro utente o di un'altra macchina, oppure file manomesso.
            throw new PaRouterException(
                ErrorCode.CryptoUnavailable,
                "The protected data key cannot be read by the current user on this machine.",
                ex);
        }
    }

    private static PaRouterException Unavailable() =>
        new(ErrorCode.CryptoUnavailable,
            "This platform has no supported key protector, so the encrypted content mode is unavailable.");
}
