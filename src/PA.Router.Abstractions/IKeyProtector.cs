namespace PA.Router.Abstractions;

/// <summary>
/// Protezione della chiave dati legata a utente e macchina. Su Windows e' DPAPI CurrentUser.
/// Se la piattaforma non offre un protector, il modo persistente e' indisponibile: non esiste
/// un fallback in chiaro (03-specs/STORAGE-AUDIT.md).
/// </summary>
public interface IKeyProtector
{
    /// <summary>Identifica l'implementazione nelle diagnosi, senza esporre materiale chiave.</summary>
    string Name { get; }

    /// <summary>Vero se la piattaforma corrente supporta questo protector.</summary>
    bool IsAvailable { get; }

    /// <summary>Protegge la chiave dati. Lancia <see cref="PaRouterException"/> se indisponibile.</summary>
    byte[] Protect(ReadOnlySpan<byte> plaintextKey);

    /// <summary>Ripristina la chiave dati protetta.</summary>
    byte[] Unprotect(ReadOnlySpan<byte> protectedKey);
}
