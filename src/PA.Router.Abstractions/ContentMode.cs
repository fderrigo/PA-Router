namespace PA.Router.Abstractions;

/// <summary>
/// Modo di conservazione del contenuto di una sessione. Valori wire dello schema SQL.
/// </summary>
public enum ContentMode
{
    /// <summary>Nessuna riga o blob persistente: tutto vive in memoria e sparisce col worker.</summary>
    MemoryOnly,

    /// <summary>Blob cifrati su disco con chiave protetta dal sistema operativo.</summary>
    Encrypted,
}

/// <summary>Conversioni wire per <see cref="ContentMode"/>.</summary>
public static class ContentModes
{
    /// <summary>Valore usato dallo schema SQL.</summary>
    public static string ToWire(this ContentMode mode) => mode switch
    {
        ContentMode.MemoryOnly => "memory_only",
        ContentMode.Encrypted => "encrypted",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>Modo corrispondente al valore letto dallo schema SQL.</summary>
    public static ContentMode Parse(string wire) => wire switch
    {
        "memory_only" => ContentMode.MemoryOnly,
        "encrypted" => ContentMode.Encrypted,
        _ => throw new ArgumentOutOfRangeException(nameof(wire), "Unknown content mode: " + wire),
    };
}
