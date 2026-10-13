using PA.Router.Abstractions;

namespace PA.Router.Infrastructure.Crypto;

/// <summary>
/// Sceglie il protector della chiave dati secondo la piattaforma: DPAPI CurrentUser su Windows,
/// passphrase con derivazione PBKDF2 altrove.
///
/// La scelta e' un fatto della piattaforma, non della configurazione: non esiste un campo per
/// imporre un protector piu' debole, e dove nessuno dei due e' ottenibile il modo persistente resta
/// indisponibile invece di degradare in chiaro.
/// </summary>
public static class PlatformKeyProtector
{
    /// <summary>Costruisce il protector della piattaforma corrente.</summary>
    public static IKeyProtector Create() =>
        OperatingSystem.IsWindows() ? new DpapiKeyProtector() : new PassphraseKeyProtector();
}
