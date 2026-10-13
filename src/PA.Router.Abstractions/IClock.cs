namespace PA.Router.Abstractions;

/// <summary>
/// Orologio iniettato: i test usano un clock fisso, il codice non chiama mai DateTime.UtcNow
/// direttamente (05-quality/CODING-STANDARDS.md).
/// </summary>
public interface IClock
{
    /// <summary>Istante corrente in UTC.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Istante corrente in millisecondi Unix, l'unita' usata dallo schema SQL.</summary>
    long UnixTimeMilliseconds => UtcNow.ToUnixTimeMilliseconds();
}
