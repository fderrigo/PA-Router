using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Scrive il sottoinsieme YAML che <see cref="RestrictedYamlReader"/> accetta: mappe, sequenze e
/// scalari semplici. Niente ancore, tag, chiavi complesse o blocchi multilinea, perche' cio' che
/// il prodotto non sa rileggere non ha motivo di essere scritto.
///
/// Le virgolette compaiono solo dove YAML le richiede: un file di configurazione va letto da una
/// persona, e un documento pieno di virgolette inutili si legge peggio.
/// </summary>
public static class RestrictedYamlWriter
{
    /// <summary>Serializza un documento gia' costruito come albero JSON.</summary>
    public static string Write(JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(document);

        StringBuilder builder = new();
        WriteNode(document, 0, builder);

        return builder.ToString();
    }

    private static void WriteNode(JsonNode node, int indent, StringBuilder builder)
    {
        switch (node)
        {
            case JsonObject map:
                WriteMap(map, indent, builder);
                break;

            case JsonArray sequence:
                WriteSequence(sequence, indent, builder);
                break;

            default:
                builder.Append(Scalar(node)).Append('\n');
                break;
        }
    }

    private static void WriteMap(JsonObject map, int indent, StringBuilder builder)
    {
        string pad = new(' ', indent * 2);

        foreach (KeyValuePair<string, JsonNode?> entry in map)
        {
            switch (entry.Value)
            {
                case JsonObject nested when nested.Count > 0:
                    builder.Append(pad).Append(entry.Key).Append(":\n");
                    WriteMap(nested, indent + 1, builder);
                    break;

                case JsonArray items when items.Count > 0:
                    builder.Append(pad).Append(entry.Key).Append(":\n");

                    // La sequenza resta al livello della chiave, come negli esempi consegnati.
                    WriteSequence(items, indent, builder);
                    break;

                default:
                    builder.Append(pad).Append(entry.Key).Append(": ").Append(Scalar(entry.Value)).Append('\n');
                    break;
            }
        }
    }

    private static void WriteSequence(JsonArray items, int indent, StringBuilder builder)
    {
        string pad = new(' ', indent * 2);

        foreach (JsonNode? item in items)
        {
            if (item is JsonObject map && map.Count > 0)
            {
                StringBuilder nested = new();
                WriteMap(map, indent + 1, nested);

                string[] lines = nested.ToString().TrimEnd('\n').Split('\n');

                builder.Append(pad).Append("- ").Append(lines[0].TrimStart()).Append('\n');

                for (int index = 1; index < lines.Length; index++)
                {
                    builder.Append(lines[index]).Append('\n');
                }

                continue;
            }

            builder.Append(pad).Append("- ").Append(Scalar(item)).Append('\n');
        }
    }

    private static string Scalar(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        if (node is JsonArray)
        {
            return "[]";
        }

        if (node is JsonObject)
        {
            return "{}";
        }

        JsonValue value = (JsonValue)node;

        // Un nodo costruito in memoria conserva il tipo CLR con cui e' stato scritto: un int non
        // si legge come long, quindi i tentativi vanno fatti nell'ordine in cui possono comparire.
        if (value.TryGetValue(out bool flag))
        {
            return flag ? "true" : "false";
        }

        if (value.TryGetValue(out int small))
        {
            return small.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue(out long integer))
        {
            return integer.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue(out double number))
        {
            return number.ToString("0.0###########", CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue(out decimal exact))
        {
            return exact.ToString(CultureInfo.InvariantCulture);
        }

        if (!value.TryGetValue(out string? text) || text is null)
        {
            throw new InvalidOperationException("The document holds a scalar this writer cannot serialise.");
        }

        return NeedsQuotes(text) ? System.Text.Json.JsonSerializer.Serialize(text) : text;
    }

    /// <summary>
    /// Una stringa va fra virgolette quando senza di esse verrebbe riletta come un altro tipo, o
    /// non verrebbe riletta affatto.
    /// </summary>
    private static bool NeedsQuotes(string text)
    {
        if (text.Length == 0 || text.Trim().Length != text.Length)
        {
            return true;
        }

        if (text is "true" or "false" or "null" or "yes" or "no" or "on" or "off" or "~")
        {
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return true;
        }

        if (text.Contains(": ", StringComparison.Ordinal) || text.Contains(" #", StringComparison.Ordinal))
        {
            return true;
        }

        if (!char.IsLetterOrDigit(text[0]) && text[0] is not '_')
        {
            return true;
        }

        foreach (char character in text)
        {
            if (!char.IsLetterOrDigit(character) && character is not ('_' or '-' or '.' or '/' or ':' or '@' or '+'))
            {
                return true;
            }
        }

        return false;
    }
}
