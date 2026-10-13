using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Override consentiti dall'ambiente. L'allowlist e' chiusa: nessuna interpolazione generica,
/// nessun include remoto, nessuna variabile che possa cambiare permessi, bind dell'API, target o
/// fatturazione (03-specs/CONFIG.md).
/// </summary>
/// <param name="DataDirectory">Directory dati richiesta, gia' verificata.</param>
/// <param name="LogLevel">Livello di log richiesto.</param>
public sealed record EnvironmentOverrides(string? DataDirectory, string? LogLevel)
{
    /// <summary>Variabile che indica la directory dati.</summary>
    public const string DataDirectoryVariable = "PA_ROUTER_DATA_DIR";

    /// <summary>Variabile che indica il livello di log.</summary>
    public const string LogLevelVariable = "PA_ROUTER_LOG_LEVEL";

    /// <summary>Livelli di log ammessi.</summary>
    public static IReadOnlyList<string> LogLevels { get; } =
        ["trace", "debug", "information", "warning", "error", "critical", "none"];

    /// <summary>
    /// Legge le sole variabili dell'allowlist. Una variabile presente ma non valida e' un errore:
    /// ignorarla in silenzio farebbe credere all'operatore di aver cambiato qualcosa.
    /// </summary>
    public static EnvironmentOverrides Read(
        IReadOnlyDictionary<string, string?> environment,
        List<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(issues);

        return new EnvironmentOverrides(
            ReadDataDirectory(environment, issues),
            ReadLogLevel(environment, issues));
    }

    private static string? ReadDataDirectory(
        IReadOnlyDictionary<string, string?> environment,
        List<ValidationIssue> issues)
    {
        if (!environment.TryGetValue(DataDirectoryVariable, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(value))
        {
            issues.Add(Issue(DataDirectoryVariable, "The data directory must be an absolute path."));
            return null;
        }

        string full = Path.GetFullPath(value);

        if (value.Split('/', '\\').Any(segment => segment is ".."))
        {
            issues.Add(Issue(DataDirectoryVariable, "The data directory must not contain traversal segments."));
            return null;
        }

        if (IsInsideRepository(full))
        {
            // Lo store non deve finire in un repository di lavoro: verrebbe committato o
            // cancellato da un'operazione Git.
            issues.Add(Issue(DataDirectoryVariable, "The data directory must stay outside a Git working tree."));
            return null;
        }

        return full;
    }

    private static string? ReadLogLevel(
        IReadOnlyDictionary<string, string?> environment,
        List<ValidationIssue> issues)
    {
        if (!environment.TryGetValue(LogLevelVariable, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.ToLowerInvariant();

        if (!LogLevels.Contains(normalized, StringComparer.Ordinal))
        {
            issues.Add(Issue(LogLevelVariable, "The log level is outside the allowed set."));
            return null;
        }

        return normalized;
    }

    private static bool IsInsideRepository(string path)
    {
        DirectoryInfo? directory = new(path);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return true;
            }

            directory = directory.Parent;
        }

        return false;
    }

    private static ValidationIssue Issue(string variable, string message) =>
        new("environment", "/" + variable, null, ValidationCodes.InvalidPath, message);
}
