namespace PA.Router.Hosting;

/// <summary>
/// Identita del modulo. I test di architettura confrontano queste dichiarazioni con i
/// riferimenti realmente emessi nell'assembly: un modulo non puo dichiarare una dipendenza
/// che il grafo dei progetti non consente.
/// </summary>
public static class ModuleInfo
{
    /// <summary>Nome dell'assembly del modulo.</summary>
    public static string Name => "PA.Router.Hosting";

    /// <summary>Moduli PA Router da cui questo modulo dipende.</summary>
    public static IReadOnlyList<string> Dependencies =>
    [
        PA.Router.Core.ModuleInfo.Name,
        PA.Router.Infrastructure.ModuleInfo.Name,
        PA.Router.Search.Local.ModuleInfo.Name,
        PA.Router.Embeddings.Ollama.ModuleInfo.Name,
        PA.Router.Adapters.Anthropic.ModuleInfo.Name,
        PA.Router.Adapters.Ollama.ModuleInfo.Name,
        PA.Router.Adapters.Codex.ModuleInfo.Name,
        PA.Router.Adapters.OpenCode.ModuleInfo.Name,
        PA.Router.Adapters.ClaudeCode.ModuleInfo.Name,
        PA.Router.Detectors.Onnx.ModuleInfo.Name,
    ];
}
