using System.Security.Cryptography;
using System.Text;

namespace PA.Router.Runtime.Ipc;

/// <summary>
/// Derivazione del nome della pipe dalla directory dati, come richiesto da
/// 03-specs/LOCAL-RUNTIME.md.
///
/// La chiave dell'HMAC e' una label pubblica, non un segreto: qualunque client dello stesso
/// utente deve poter calcolare lo stesso nome senza aprire lo store. Il nome non trasporta il
/// path in chiaro e non contiene token di provider; la protezione del canale resta l'ACL utente.
/// </summary>
public static class IpcPipeName
{
    private const string Prefix = "pa-router-";

    private static readonly byte[] Label = Encoding.UTF8.GetBytes("pa-router-ipc-v1");

    /// <summary>Nome della pipe per una directory dati.</summary>
    public static string ForDataDirectory(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        // Path canonico: due client che indicano la stessa directory in forme diverse devono
        // raggiungere lo stesso worker. Il case viene normalizzato solo dove il filesystem lo
        // ignora davvero: su Linux due nomi che differiscono per maiuscole sono due directory.
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));

        if (OperatingSystem.IsWindows())
        {
            canonical = canonical.ToUpperInvariant();
        }

        byte[] digest = HMACSHA256.HashData(Label, Encoding.UTF8.GetBytes(canonical));

        return Prefix + Convert.ToHexStringLower(digest)[..32];
    }
}
