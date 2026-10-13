namespace PA.Router.Sdk;

/// <summary>
/// Identita del modulo. I test di architettura confrontano queste dichiarazioni con i
/// riferimenti realmente emessi nell'assembly: un modulo non puo dichiarare una dipendenza
/// che il grafo dei progetti non consente.
/// </summary>
public static class ModuleInfo
{
    /// <summary>Nome dell'assembly del modulo.</summary>
    public static string Name => "PA.Router.Sdk";

    /// <summary>Moduli PA Router da cui questo modulo dipende.</summary>
    public static IReadOnlyList<string> Dependencies => [PA.Router.Hosting.ModuleInfo.Name, PA.Router.Abstractions.ModuleInfo.Name];
}
