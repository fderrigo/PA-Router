using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Adapters.Anthropic;
using PA.Router.Adapters.Ollama;
using PA.Router.Detectors.Onnx;

namespace PA.Router.Hosting.Runtime;

/// <summary>Discovery esplicita; mai eseguita al disegno della schermata.</summary>
public static class SetupDiscovery
{
    public static async Task<IReadOnlyList<string>?> ModelsAsync(ServiceDescriptor service, IClock clock, CancellationToken cancellationToken)
    {
        var models = await ModelCatalogueAsync(service, clock, cancellationToken).ConfigureAwait(false);
        return models is null ? null : [.. models.Keys];
    }

    /// <summary>Observed catalogue metadata only; no inference, implicit dimension or context-window defaults.</summary>
    public static async Task<IReadOnlyDictionary<string, OllamaProbe.InstalledModel>?> ModelCatalogueAsync(
        ServiceDescriptor service, IClock clock, CancellationToken cancellationToken)
    {
        if (service.Adapter is not (AdapterKind.Ollama or AdapterKind.OllamaEmbed) || service.Endpoint is null) { return null; }
        using OllamaProbe probe = new(clock);
        return await probe.DiscoverCatalogueAsync(service.InferencePlacement, service.Endpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Se un modello di chat di Ollama ragiona prima di rispondere e se si puo' spegnere. Solo su richiesta del setup: carica
    /// il modello e gli fa una domanda di una parola.
    /// </summary>
    public static Task<ThinkingSupport> ThinkingAsync(ServiceDescriptor service, CancellationToken cancellationToken) =>
        service.Adapter is AdapterKind.Ollama && service.Capability is ServiceCapability.Chat && service.Endpoint is Uri endpoint && service.Model is { Length: > 0 } model
            ? OllamaThinkingProbe.ObserveAsync(endpoint, model, cancellationToken)
            : Task.FromResult(ThinkingSupport.None);

    /// <summary>Only explicitly requested network discovery; credentials remain in the resolver.</summary>
    public static async Task<IReadOnlyList<string>?> RemoteModelsAsync(ServiceDescriptor service, IClock clock,
        Func<string, string?> secrets, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        (IReadOnlyList<string>? Models, string? FailureCode) result;
        if (service.Adapter == AdapterKind.Anthropic)
        {
            using AnthropicProbe probe = new(clock, secrets);
            result = await probe.DiscoverAsync(service, deadline.Token).ConfigureAwait(false);
        }
        else
        {
            using OpenAiCompatibleProbe probe = new(clock, secrets);
            result = await probe.DiscoverAsync(service, deadline.Token).ConfigureAwait(false);
        }
        if (result.FailureCode is { } failure)
        {
            throw new PaRouterException(ErrorCode.TargetUnavailable, "Model catalogue unavailable.").WithMetadata("reason", failure);
        }
        return result.Models;
    }

    /// <summary>Lists names of installed ONNX bundles without loading weights or executing code.</summary>
    public static IReadOnlyList<string> LocalModels(string modelsRoot)
    {
        if (!Directory.Exists(modelsRoot)) { return []; }
        return [.. Directory.EnumerateDirectories(modelsRoot).Select(Path.GetFileName)
            .Where(name => LocalModelStore.Resolve(modelsRoot, name) is { } path && OnnxPiiModel.IsInstalled(path))
            .Select(name => name!).Order(StringComparer.Ordinal)];
    }

    public static IReadOnlyList<string> Executables(ServiceDescriptor service) => Executables(service.Adapter,
        Environment.GetEnvironmentVariable("PATH"), StandardDirectories());

    internal static IReadOnlyList<string> Executables(AdapterKind adapter, string? searchPath, IEnumerable<string> standardDirectories)
    {
        string? file = adapter switch
        {
            AdapterKind.ClaudeCodeCli => "claude",
            AdapterKind.CodexCli => "codex",
            AdapterKind.OpenCodeCli => "opencode",
            _ => null,
        };
        if (file is null) { return []; }
        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd"] : [string.Empty];
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return [.. (searchPath ?? string.Empty).Split(Path.PathSeparator).Concat(standardDirectories)
            .Select(path => path.Trim().Trim('"')).Where(Path.IsPathFullyQualified)
            .SelectMany(path => extensions.Select(extension => Path.Combine(path, file + extension)))
            .Where(File.Exists).Select(Path.GetFullPath).Distinct(comparer)];
    }

    private static IEnumerable<string> StandardDirectories()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Path.IsPathFullyQualified(home))
        {
            yield return Path.Combine(home, ".local", "bin");
            yield return Path.Combine(home, ".opencode", "bin");
        }
        string? custom = Environment.GetEnvironmentVariable("OPENCODE_INSTALL_DIR");
        if (!string.IsNullOrEmpty(custom)) { yield return custom; }
        if (OperatingSystem.IsWindows())
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (Path.IsPathFullyQualified(appData)) { yield return Path.Combine(appData, "npm"); }
        }
        else
        {
            yield return "/usr/local/bin";
            yield return "/usr/bin";
            yield return "/opt/homebrew/bin";
        }
    }
}
