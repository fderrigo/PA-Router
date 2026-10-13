namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Generatore di identificatori, iniettato come il clock: i test devono poter fissare gli ID per
/// confrontare audit e decisioni, e il prodotto non deve dipendere da un generatore globale
/// (05-quality/CODING-STANDARDS.md).
/// </summary>
public interface IIdGenerator
{
    /// <summary>Nuovo identificatore, UUID in forma stringa minuscola.</summary>
    string NewId();
}

/// <summary>Generatore di produzione.</summary>
public sealed class GuidIdGenerator : IIdGenerator
{
    /// <summary>Istanza condivisa: la classe non ha stato.</summary>
    public static GuidIdGenerator Instance { get; } = new();

    /// <inheritdoc />
    public string NewId() => Guid.NewGuid().ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}
