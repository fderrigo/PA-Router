using System.Collections.Concurrent;
using System.Reflection;

namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Valore wire di un membro di enum. I contratti usano stringhe: il numero ordinale di un enum
/// non e' mai parte del formato (05-quality/CODING-STANDARDS.md).
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class WireValueAttribute(string value) : Attribute
{
    /// <summary>Stringa usata nei documenti e negli schemi.</summary>
    public string Value { get; } = value;
}

/// <summary>
/// Conversione fra membri di enum e stringhe wire. Ogni membro deve dichiarare il proprio
/// <see cref="WireValueAttribute"/>: un membro aggiunto senza attributo fallisce subito, invece
/// di serializzarsi con un nome inatteso.
/// </summary>
public static class WireEnum
{
    private static readonly ConcurrentDictionary<Type, Maps> Cache = new();

    /// <summary>Stringa wire del valore.</summary>
    public static string ToWire<T>(this T value)
        where T : struct, Enum =>
        Maps.For(typeof(T)).ToWire.TryGetValue(value, out string? wire)
            ? wire
            : throw new ArgumentOutOfRangeException(nameof(value), "Enum member without a wire value.");

    /// <summary>Valore corrispondente alla stringa wire, confronto ordinale.</summary>
    public static bool TryParse<T>(string? wire, out T value)
        where T : struct, Enum
    {
        if (wire is not null && Maps.For(typeof(T)).FromWire.TryGetValue(wire, out object? parsed))
        {
            value = (T)parsed;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Valore corrispondente alla stringa wire; lancia se sconosciuto.</summary>
    public static T Parse<T>(string wire)
        where T : struct, Enum =>
        TryParse(wire, out T value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(wire), "Unknown wire value for " + typeof(T).Name + ".");

    /// <summary>Tutte le stringhe wire dell'enum, in ordine di dichiarazione.</summary>
    public static IReadOnlyList<string> WireValues<T>()
        where T : struct, Enum =>
        [.. Enum.GetValues<T>().Select(value => value.ToWire())];

    private sealed class Maps
    {
        private Maps(Dictionary<object, string> toWire, Dictionary<string, object> fromWire)
        {
            ToWire = toWire;
            FromWire = fromWire;
        }

        public Dictionary<object, string> ToWire { get; }

        public Dictionary<string, object> FromWire { get; }

        public static Maps For(Type type) => Cache.GetOrAdd(type, Build);

        private static Maps Build(Type type)
        {
            Dictionary<object, string> toWire = [];
            Dictionary<string, object> fromWire = new(StringComparer.Ordinal);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                WireValueAttribute attribute = field.GetCustomAttribute<WireValueAttribute>()
                    ?? throw new InvalidOperationException(
                        type.Name + "." + field.Name + " has no wire value.");

                object value = field.GetValue(null)!;
                toWire.Add(value, attribute.Value);
                fromWire.Add(attribute.Value, value);
            }

            return new Maps(toWire, fromWire);
        }
    }
}
