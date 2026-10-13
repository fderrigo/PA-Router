namespace PA.Router.Abstractions;

/// <summary>
/// Errore tipizzato al confine dell'infrastruttura. Il messaggio e i metadata sono safe per
/// definizione: nessun contenuto raw, path completo, credenziale o eccezione provider integrale.
/// L'eccezione originale resta come <see cref="Exception.InnerException"/> per il log locale,
/// non per la propagazione verso host o API.
/// </summary>
public sealed class PaRouterException : Exception
{
    private readonly Dictionary<string, string> metadata;

    public PaRouterException(ErrorCode code, string safeMessage, Exception? innerException = null)
        : base(safeMessage, innerException)
    {
        Code = code;
        metadata = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Codice stabile associato all'errore.</summary>
    public ErrorCode Code { get; }

    /// <summary>Metadata allowlisted, sicuri da registrare o serializzare.</summary>
    public IReadOnlyDictionary<string, string> Metadata => metadata;

    /// <summary>Aggiunge un metadata safe e restituisce la stessa eccezione.</summary>
    public PaRouterException WithMetadata(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        metadata[key] = value;
        return this;
    }
}
