using PA.Router.Abstractions;

namespace PA.Router.Infrastructure;

/// <summary>Orologio di sistema. I test usano un clock fisso al suo posto.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>Istanza condivisa: la classe non ha stato.</summary>
    public static readonly SystemClock Instance = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
