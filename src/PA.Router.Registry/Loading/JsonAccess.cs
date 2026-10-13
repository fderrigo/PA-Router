using System.Globalization;
using System.Text.Json.Nodes;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Accesso tipizzato al documento gia' validato dallo schema. I metodi lanciano se un campo
/// manca o ha il tipo sbagliato: a quel punto sarebbe un difetto della validazione, non un
/// errore dell'operatore, e va scoperto subito invece di diventare un default silenzioso.
/// </summary>
internal static class JsonAccess
{
    internal static JsonObject Object(JsonNode node) =>
        node as JsonObject ?? throw new InvalidOperationException("Expected an object.");

    internal static JsonObject Object(JsonObject parent, string name) =>
        parent[name] as JsonObject ?? throw new InvalidOperationException("Expected object property " + name + ".");

    internal static string String(JsonObject parent, string name) =>
        StringOrNull(parent, name) ?? throw new InvalidOperationException("Expected string property " + name + ".");

    internal static string? StringOrNull(JsonObject parent, string name) =>
        parent[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    internal static bool Bool(JsonObject parent, string name) =>
        parent[name] is JsonValue value && value.TryGetValue(out bool flag)
            ? flag
            : throw new InvalidOperationException("Expected boolean property " + name + ".");

    /// <summary>
    /// Un intero puo' arrivare come <c>long</c> dal lettore YAML o come <c>double</c> da un JSON
    /// con notazione decimale: entrambi vanno accettati, un valore non intero no.
    /// </summary>
    internal static int Int(JsonObject parent, string name)
    {
        double value = Number(parent, name);

        return double.IsInteger(value)
            ? (int)value
            : throw new InvalidOperationException("Expected integer property " + name + ".");
    }

    internal static double Number(JsonObject parent, string name)
    {
        if (parent[name] is not JsonValue value)
        {
            throw new InvalidOperationException("Expected number property " + name + ".");
        }

        if (value.TryGetValue(out long integer))
        {
            return integer;
        }

        if (value.TryGetValue(out double number))
        {
            return number;
        }

        throw new InvalidOperationException("Expected number property " + name + ".");
    }

    internal static Guid Guid(JsonObject parent, string name) =>
        System.Guid.Parse(String(parent, name), CultureInfo.InvariantCulture);

    internal static Uri? UriOrNull(JsonObject parent, string name) =>
        StringOrNull(parent, name) is string value ? new Uri(value, UriKind.Absolute) : null;

    internal static DateTimeOffset? DateTimeOrNull(JsonObject parent, string name) =>
        StringOrNull(parent, name) is string value
            ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

    internal static JsonArray Array(JsonObject parent, string name) =>
        parent[name] as JsonArray ?? throw new InvalidOperationException("Expected array property " + name + ".");

    internal static IReadOnlyList<string> StringList(JsonObject parent, string name) =>
        [.. Array(parent, name).Select(item => item!.GetValue<string>())];

    internal static IReadOnlySet<string> StringSet(JsonObject parent, string name) =>
        new HashSet<string>(StringList(parent, name), StringComparer.Ordinal);

    internal static T Enum<T>(JsonObject parent, string name)
        where T : struct, Enum =>
        WireEnum.Parse<T>(String(parent, name));

    internal static IReadOnlySet<T> EnumSet<T>(JsonObject parent, string name)
        where T : struct, Enum =>
        new HashSet<T>(Array(parent, name).Select(item => WireEnum.Parse<T>(item!.GetValue<string>())));

    // ── Campi facoltativi ─────────────────────────────────────────────────────
    // Lo schema v2 rende obbligatorie solo le proprieta' che identificano il servizio: le altre
    // dipendono dall'adapter. Assente e null si comportano allo stesso modo — non e' un default
    // silenzioso, e' l'assenza di una dichiarazione, che i gate trattano come deny dove conta.

    internal static JsonObject? ObjectOrNull(JsonObject parent, string name) => parent[name] as JsonObject;

    internal static bool BoolOrDefault(JsonObject parent, string name, bool fallback) =>
        parent[name] is JsonValue value && value.TryGetValue(out bool flag) ? flag : fallback;

    internal static int IntOrDefault(JsonObject parent, string name, int fallback) =>
        parent[name] is JsonValue ? Int(parent, name) : fallback;

    internal static int? IntOrNull(JsonObject parent, string name) =>
        parent[name] is JsonValue ? Int(parent, name) : null;

    internal static double? NumberOrNull(JsonObject parent, string name) =>
        parent[name] is JsonValue ? Number(parent, name) : null;

    internal static T EnumOrDefault<T>(JsonObject parent, string name, T fallback)
        where T : struct, Enum =>
        StringOrNull(parent, name) is string wire ? WireEnum.Parse<T>(wire) : fallback;

    internal static IReadOnlyList<string> StringListOrEmpty(JsonObject parent, string name) =>
        parent[name] is JsonArray ? StringList(parent, name) : [];

    internal static IReadOnlySet<string> StringSetOrEmpty(JsonObject parent, string name) =>
        new HashSet<string>(StringListOrEmpty(parent, name), StringComparer.Ordinal);

    internal static IReadOnlySet<T> EnumSetOrEmpty<T>(JsonObject parent, string name)
        where T : struct, Enum =>
        parent[name] is JsonArray ? EnumSet<T>(parent, name) : new HashSet<T>();

    /// <summary>
    /// Insieme di luoghi di elaborazione. Non e' un enum: il vocabolario e' aperto sui paesi e
    /// chiuso sui gruppi, e la verifica del termine sta nei controlli semantici, che possono
    /// indicare quale voce dell'elenco e' sbagliata.
    /// </summary>
    internal static IReadOnlySet<ProcessingRegion> RegionSet(JsonObject parent, string name) =>
        new HashSet<ProcessingRegion>(Array(parent, name).Select(item => Regions.Parse(item!.GetValue<string>())));

    internal static IReadOnlySet<ProcessingRegion> RegionSetOrEmpty(JsonObject parent, string name) =>
        parent[name] is JsonArray ? RegionSet(parent, name) : new HashSet<ProcessingRegion>();
}
