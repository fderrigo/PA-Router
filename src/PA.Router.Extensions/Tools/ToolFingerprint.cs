using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PA.Router.Extensions.Tools;

/// <summary>
/// Impronta di uno strumento (ADR-020): SHA-256 della forma canonica di cio' che lo definisce. Se una sorgente
/// cambia descrizione o parametri — un aggiornamento ostile, uno strumento che ne imita un altro — l'impronta
/// cambia e le dichiarazioni fatte per quella di prima non valgono piu'.
/// </summary>
public static class ToolFingerprint
{
    /// <summary>Prefisso dell'algoritmo.</summary>
    public const string Prefix = "sha256:";

    /// <summary>Impronta delle parti indicate, in forma canonica.</summary>
    public static string Compute(params (string Key, JsonNode? Value)[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        JsonObject root = [];

        foreach ((string key, JsonNode? value) in parts)
        {
            root[key] = value?.DeepClone();
        }

        return Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(root))));
    }

    /// <summary>Forma canonica: chiavi in ordine ordinale, nessuno spazio.</summary>
    public static string Canonical(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";

            case JsonObject item:
                {
                    StringBuilder builder = new("{");
                    bool first = true;

                    foreach (KeyValuePair<string, JsonNode?> property in item.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }

                        builder.Append(JsonSerializer.Serialize(property.Key)).Append(':').Append(Canonical(property.Value));
                        first = false;
                    }

                    return builder.Append('}').ToString();
                }

            case JsonArray array:
                return "[" + string.Join(",", array.Select(Canonical)) + "]";

            default:
                return node.ToJsonString();
        }
    }
}

/// <summary>Nomi degli strumenti come li vede il modello.</summary>
public static class ToolNames
{
    /// <summary>Lunghezza massima: il limite piu' stretto fra i fornitori.</summary>
    public const int MaxLength = 64;

    /// <summary>Nome ammesso da tutti i fornitori: lettere, cifre, trattino basso e trattino.</summary>
    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        StringBuilder builder = new(Math.Min(name.Length, MaxLength));

        foreach (char character in name)
        {
            if (builder.Length == MaxLength)
            {
                break;
            }

            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_');
        }

        return builder.Length == 0 ? "tool" : builder.ToString();
    }

    /// <summary>Vero se il nome e' quello che MCP raccomanda: 1-128 caratteri fra lettere, cifre, _ - .</summary>
    public static bool IsWellFormedMcpName(string name) =>
        name.Length is >= 1 and <= 128 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
}
