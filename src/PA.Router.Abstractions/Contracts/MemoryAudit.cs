namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Traccia degli eventi di memoria che non appartengono a un'esecuzione.
///
/// L'embedding di una query o di un'osservazione avviene fuori dal turno, ma resta un'invocazione
/// di un modello su dati dell'utente: viene registrata prima della chiamata, con soli metadati
/// allowlisted — mai il testo, mai il vettore (03-specs/POLICY.md, «gate delle elaborazioni
/// ausiliarie»).
/// </summary>
public interface IMemoryAudit
{
    /// <summary>Registra un evento di memoria con i suoi metadati.</summary>
    /// <param name="code">Codice stabile dell'evento.</param>
    /// <param name="metadata">Coppie gia' sicure da registrare.</param>
    /// <param name="cancellationToken">Annullamento.</param>
    ValueTask RecordAsync(
        string code,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken);
}

/// <summary>Codici degli eventi di memoria.</summary>
public static class MemoryAuditCodes
{
    /// <summary>Intenzione di invocare il modello di embedding, registrata prima della chiamata.</summary>
    public const string EmbedIntent = "memory_embed_intent";

    /// <summary>Rifiuto del gate ausiliario prima di qualunque invocazione.</summary>
    public const string EmbedDenied = "memory_embed_denied";

    /// <summary>Ricordo reso invisibile su richiesta dell'operatore.</summary>
    public const string Forgotten = "memory_forgotten";

    /// <summary>
    /// Destinazione scelta per un ricordo, con la classe e la versione dei rilevatori che l'hanno
    /// decisa. E' l'unico punto in cui si vede **perche'** un testo e' uscito o e' rimasto dentro,
    /// e va registrato anche quando la destinazione e' quella locale: un audit che parla solo delle
    /// uscite non permette di accorgersi che non ne e' avvenuta nessuna.
    /// </summary>
    public const string Routed = "memory_routed";
}
