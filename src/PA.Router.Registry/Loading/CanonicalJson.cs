using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Forma canonica di un documento e digest corrispondente. Le chiavi vengono ordinate e la
/// formattazione eliminata, cosi' lo stesso contenuto scritto in YAML o in JSON produce lo stesso
/// digest: e' quello che rende confrontabile lo snapshot fra riavvii e fra formati.
/// </summary>
public static class CanonicalJson
{
    /// <summary>Testo canonico del documento.</summary>
    public static string Canonicalize(JsonNode? node)
    {
        JsonNode? canonical = Sort(node);
        return canonical is null ? "null" : canonical.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>SHA-256 esadecimale minuscolo della forma canonica.</summary>
    public static string Digest(JsonNode? node) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(node))));

    /// <summary>Digest combinato di piu' documenti, nell'ordine indicato.</summary>
    public static string Digest(IEnumerable<string> digests)
    {
        ArgumentNullException.ThrowIfNull(digests);

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', digests))));
    }

    private static JsonNode? Sort(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject source:
                JsonObject sorted = [];

                foreach (KeyValuePair<string, JsonNode?> property in source.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    sorted[property.Key] = Sort(property.Value);
                }

                return sorted;

            case JsonArray array:
                JsonArray items = [];

                foreach (JsonNode? item in array)
                {
                    items.Add(Sort(item));
                }

                return items;

            default:
                // I valori scalari vengono clonati: l'originale resta di proprieta' del chiamante.
                return node?.DeepClone();
        }
    }
}
