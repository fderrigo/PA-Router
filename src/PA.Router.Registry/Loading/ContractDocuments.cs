using System.Text.Json.Nodes;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Conversione da documento gia' validato a DTO, per chi legge un singolo file invece
/// dell'intera directory dati: <c>pa policy validate</c>, i test di contratto, gli strumenti di
/// diagnosi.
///
/// I metodi presuppongono che lo schema sia gia' stato applicato: chiamarli su un documento non
/// validato e' un errore di programmazione e si manifesta come eccezione, non come DTO parziale.
/// </summary>
public static class ContractDocuments
{
    /// <summary>Configurazione operativa.</summary>
    public static ConfigDocument ReadConfig(JsonNode root) => DocumentMapper.ToConfig(root);

    /// <summary>Registro dei target.</summary>
    public static RegistryDocument ReadRegistry(JsonNode root) => DocumentMapper.ToRegistry(root);

    /// <summary>Profilo di policy.</summary>
    public static PolicyDocument ReadPolicy(JsonNode root) => DocumentMapper.ToPolicy(root);
}
