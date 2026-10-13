using System.Text.Json.Nodes;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>Metadati degli schemi incorporati; la validazione resta in ConfigurationLoader.</summary>
public static class SetupContract
{
    /// <summary>Restituisce una copia dello schema normativo con riferimenti locali risolti.</summary>
    public static JsonObject Schema(string kind)
    {
        string name = kind == "service" ? "registry" : kind;
        using Stream stream = typeof(ConfigurationLoader).Assembly.GetManifestResourceStream(
            "PA.Router.Registry.Loading.Schemas." + name + ".schema.json")
            ?? throw new ArgumentException("Schema non disponibile.", nameof(kind));
        JsonObject root = JsonNode.Parse(stream)!.AsObject();
        JsonObject start = kind == "service" ? root["properties"]!["services"]!["items"]!.AsObject() : root;
        return Expand(start, root).AsObject();
    }

    private static JsonNode Expand(JsonNode node, JsonObject root)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue reference)
            {
                JsonNode target = root;
                foreach (string part in reference.GetValue<string>()[2..].Split('/')) { target = target[part]!; }
                return Expand(target, root);
            }
            JsonObject copy = new();
            foreach ((string key, JsonNode? value) in obj)
            {
                if (key is not ("$defs" or "allOf")) { copy[key] = value is null ? null : Expand(value, root); }
            }
            return copy;
        }
        if (node is JsonArray array) { return new JsonArray([.. array.Select(value => value is null ? null : Expand(value, root))]); }
        return node.DeepClone();
    }
}
