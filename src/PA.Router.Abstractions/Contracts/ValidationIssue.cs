namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Problema rilevato durante la validazione di un documento. Riporta dove si trova, non che cosa
/// contiene: il valore non viene mai incluso, perche' un file di configurazione puo' contenere
/// riferimenti sensibili (03-specs/CONFIG.md, «senza stampare valore sensibile»).
/// </summary>
/// <param name="Source">File di origine, in forma relativa alla directory dati.</param>
/// <param name="Path">Percorso della proprieta', in forma JSON Pointer.</param>
/// <param name="Line">Riga del documento, quando l'origine e' un file di testo.</param>
/// <param name="Code">Codice stabile del controllo che ha fallito.</param>
/// <param name="Message">Descrizione safe, senza valori.</param>
public sealed record ValidationIssue(
    string Source,
    string Path,
    long? Line,
    string Code,
    string Message)
{
    /// <summary>Rappresentazione compatta per CLI e log, priva di valori.</summary>
    public override string ToString() =>
        Source + (Line is long line ? ":" + line.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)
        + " " + Path + " [" + Code + "] " + Message;
}

/// <summary>Codici stabili dei controlli di validazione.</summary>
public static class ValidationCodes
{
    /// <summary>Il documento non e' leggibile come YAML o JSON ristretto.</summary>
    public const string Malformed = "MALFORMED_DOCUMENT";

    /// <summary>Una chiave compare due volte nello stesso oggetto.</summary>
    public const string DuplicateKey = "DUPLICATE_KEY";

    /// <summary>Il documento usa una costruzione YAML non ammessa.</summary>
    public const string UnsupportedConstruct = "UNSUPPORTED_CONSTRUCT";

    /// <summary>Il documento viola lo schema JSON.</summary>
    public const string SchemaViolation = "SCHEMA_VIOLATION";

    /// <summary>Versione di schema sconosciuta a questa build.</summary>
    public const string UnknownSchemaVersion = "UNKNOWN_SCHEMA_VERSION";

    /// <summary>Il file non esiste.</summary>
    public const string MissingFile = "MISSING_FILE";

    /// <summary>Percorso non ammesso: assoluto, traversal, UNC o fuori dalla directory dati.</summary>
    public const string InvalidPath = "INVALID_PATH";

    /// <summary>Identificatore duplicato fra gli elementi di una collezione.</summary>
    public const string DuplicateId = "DUPLICATE_ID";

    /// <summary>Riferimento a un identificatore inesistente.</summary>
    public const string UnknownReference = "UNKNOWN_REFERENCE";

    /// <summary>Endpoint non conforme al vincolo locale.</summary>
    public const string InvalidEndpoint = "INVALID_ENDPOINT";

    /// <summary>Combinazione di campi non coerente con la specifica.</summary>
    public const string InconsistentDeclaration = "INCONSISTENT_DECLARATION";

    /// <summary>Il target e' abilitato ma non e' ancora configurabile.</summary>
    public const string NotReadyForUse = "NOT_READY_FOR_USE";

    /// <summary>
    /// Il documento usa una forma sostituita: si converte con <c>pa config migrate</c>, mai in
    /// silenzio alla lettura.
    /// </summary>
    public const string MigrationRequired = "MIGRATION_REQUIRED";
}
