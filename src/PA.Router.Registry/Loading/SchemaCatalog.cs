using System.Reflection;
using System.Text;
using Json.Schema;

namespace PA.Router.Registry.Loading;

/// <summary>Schemi disponibili in questa build.</summary>
public enum ContractSchema
{
    Config,
    Registry,
    Policy,
    Plugin,
}

/// <summary>
/// Schemi JSON dei contratti, inclusi come risorsa da <c>docs/06-contracts</c>: come per il DDL,
/// non esiste una seconda copia che possa divergere dal contratto.
/// </summary>
public static class SchemaCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<ContractSchema, JsonSchema>> SchemasLazy = new(Load);

    /// <summary>Nome del file di contratto corrispondente allo schema.</summary>
    public static string FileNameOf(ContractSchema schema) => schema switch
    {
        ContractSchema.Config => "config.schema.json",
        ContractSchema.Registry => "registry.schema.json",
        ContractSchema.Policy => "policy.schema.json",
        ContractSchema.Plugin => "plugin.schema.json",
        _ => throw new ArgumentOutOfRangeException(nameof(schema)),
    };

    /// <summary>Schema compilato.</summary>
    public static JsonSchema Get(ContractSchema schema) => SchemasLazy.Value[schema];

    private static IReadOnlyDictionary<ContractSchema, JsonSchema> Load()
    {
        Dictionary<ContractSchema, JsonSchema> schemas = [];

        foreach (ContractSchema schema in Enum.GetValues<ContractSchema>())
        {
            string resourceName = typeof(SchemaCatalog).Namespace + ".Schemas." + FileNameOf(schema);
            Assembly assembly = typeof(SchemaCatalog).Assembly;

            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("Missing embedded schema: " + resourceName);
            using StreamReader reader = new(stream, Encoding.UTF8);

            schemas[schema] = JsonSchema.FromText(reader.ReadToEnd());
        }

        return schemas;
    }
}
