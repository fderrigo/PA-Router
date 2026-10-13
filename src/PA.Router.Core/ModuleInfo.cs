namespace PA.Router.Core;

/// <summary>
/// Identita del modulo. I test di architettura confrontano queste dichiarazioni con i
/// riferimenti realmente emessi nell'assembly: un modulo non puo dichiarare una dipendenza
/// che il grafo dei progetti non consente.
/// </summary>
public static class ModuleInfo
{
    /// <summary>Nome dell'assembly del modulo.</summary>
    public static string Name => "PA.Router.Core";

    /// <summary>Moduli PA Router da cui questo modulo dipende.</summary>
    public static IReadOnlyList<string> Dependencies =>
    [
        PA.Router.Abstractions.ModuleInfo.Name,
        PA.Router.Privacy.ModuleInfo.Name,
        PA.Router.Policy.ModuleInfo.Name,
        PA.Router.Registry.ModuleInfo.Name,
        PA.Router.Routing.ModuleInfo.Name,
        PA.Router.Session.ModuleInfo.Name,
        PA.Router.Execution.ModuleInfo.Name,
        PA.Router.Memory.ModuleInfo.Name,
        PA.Router.Extensions.ModuleInfo.Name,
    ];
}
