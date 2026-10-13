using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Regole sui percorsi dichiarati nella configurazione. Un percorso relativo deve restare dentro
/// la directory dati: traversal, radici assolute, UNC e link simbolici sono rifiutati invece di
/// essere normalizzati, perche' la normalizzazione nasconderebbe l'intento.
/// </summary>
public static class PathRules
{
    /// <summary>
    /// Verifica un percorso relativo alla directory dati e restituisce il percorso assoluto.
    /// </summary>
    public static bool TryResolveRelative(
        string dataDirectory,
        string source,
        string pointer,
        long? line,
        string relativePath,
        List<ValidationIssue> issues,
        out string resolved)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(issues);

        resolved = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            issues.Add(new ValidationIssue(source, pointer, line, ValidationCodes.InvalidPath, "The path is empty."));
            return false;
        }

        if (Path.IsPathRooted(relativePath))
        {
            issues.Add(new ValidationIssue(
                source, pointer, line, ValidationCodes.InvalidPath,
                "The path must be relative to the data directory."));
            return false;
        }

        if (relativePath.Split('/', '\\').Any(segment => segment is ".."))
        {
            issues.Add(new ValidationIssue(
                source, pointer, line, ValidationCodes.InvalidPath,
                "The path must not walk outside the data directory."));
            return false;
        }

        string root = Path.GetFullPath(dataDirectory);
        string candidate = Path.GetFullPath(Path.Combine(root, relativePath));

        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue(
                source, pointer, line, ValidationCodes.InvalidPath,
                "The path resolves outside the data directory."));
            return false;
        }

        if (IsLink(candidate))
        {
            issues.Add(new ValidationIssue(
                source, pointer, line, ValidationCodes.InvalidPath,
                "The path is a link or reparse point, which is not allowed for store files."));
            return false;
        }

        resolved = candidate;
        return true;
    }

    /// <summary>
    /// Verifica un percorso assoluto di eseguibile dichiarato nel registro. L'esistenza non e'
    /// garantita qui: e' il probe a osservarla, questa e' solo la forma ammessa.
    /// </summary>
    public static bool IsAcceptableExecutablePath(string executable) =>
        !string.IsNullOrWhiteSpace(executable)
        && Path.IsPathFullyQualified(executable)
        && !executable.StartsWith(@"\\", StringComparison.Ordinal)
        && !executable.Split('/', '\\').Any(segment => segment is "..");

    /// <summary>Vero se l'endpoint punta all'host locale su http o https.</summary>
    public static bool IsLoopbackEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsAbsoluteUri)
        {
            return false;
        }

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return false;
        }

        return endpoint.IsLoopback;
    }

    private static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo? target = File.Exists(path)
                ? File.ResolveLinkTarget(path, returnFinalTarget: false)
                : Directory.Exists(path)
                    ? Directory.ResolveLinkTarget(path, returnFinalTarget: false)
                    : null;

            return target is not null;
        }
        catch (IOException)
        {
            // Un percorso non ispezionabile viene trattato come non affidabile.
            return true;
        }
    }
}
