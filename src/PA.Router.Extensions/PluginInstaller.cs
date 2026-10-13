using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Extensions;

/// <summary>
/// Installazione locale di un pacchetto (03-specs/PLUGINS-SKILLS.md, «Lifecycle»).
///
/// Non esiste uno script di installazione: l'installazione e' lettura, verifica e copia. Un
/// pacchetto che volesse eseguire qualcosa per installarsi sarebbe gia' un'esecuzione non
/// autorizzata, prima che qualcuno abbia deciso di fidarsene.
///
/// Ogni file dichiarato viene verificato con il proprio SHA-256 prima della copia, e vengono
/// copiati soltanto i file dichiarati: cio' che il manifesto non enumera non entra nello store,
/// anche se sta nella directory di origine.
///
/// L'<c>installDigest</c> lega manifesto e contenuti. Cambiare un byte cambia il digest, e un
/// digest diverso e' un'installazione diversa: i grant non la seguono.
/// </summary>
public sealed class PluginInstaller
{
    private readonly IClock clock;

    public PluginInstaller(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
    }

    /// <summary>
    /// Valida il pacchetto in <paramref name="sourceDirectory"/> e lo copia sotto
    /// <paramref name="pluginRoot"/>. Non abilita nulla: il pacchetto nasce <c>installed</c>.
    /// </summary>
    public InstalledPlugin Install(string sourceDirectory, string pluginRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRoot);

        string source = Path.GetFullPath(sourceDirectory);
        string manifestPath = Path.Combine(source, ManifestReader.FileName);

        if (!File.Exists(manifestPath))
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "The package has no manifest.")
                .WithMetadata("reason", "manifest_missing");
        }

        string manifestText = File.ReadAllText(manifestPath);
        PluginManifest manifest = ManifestReader.Read(manifestText);

        Dictionary<string, byte[]> contents = new(StringComparer.Ordinal);

        foreach (PluginFile file in manifest.Files)
        {
            string path = ManifestReader.ResolveInside(source, file.Path);

            if (!File.Exists(path))
            {
                throw new PaRouterException(ErrorCode.InputInvalid, "A declared file is missing from the package.")
                    .WithMetadata("reason", "file_missing")
                    .WithMetadata("path", file.Path);
            }

            byte[] bytes = File.ReadAllBytes(path);
            string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(digest),
                    Encoding.ASCII.GetBytes(file.Sha256)))
            {
                // Un file diverso da quello dichiarato invalida tutto il pacchetto: non si installa
                // «il resto».
                throw new PaRouterException(ErrorCode.InputInvalid, "A file does not match its declared digest.")
                    .WithMetadata("reason", "digest_mismatch")
                    .WithMetadata("path", file.Path);
            }

            contents[file.Path] = bytes;
        }

        EnsureDeclared(manifest);

        string installDigest = ComputeInstallDigest(manifestText, manifest);
        string destination = Path.Combine(Path.GetFullPath(pluginRoot), manifest.Id, manifest.Version);

        if (Directory.Exists(destination))
        {
            // Reinstallare sopra un'installazione esistente nasconderebbe quale digest e' attivo.
            throw new PaRouterException(ErrorCode.InputInvalid, "That plugin version is already installed.")
                .WithMetadata("reason", "already_installed")
                .WithMetadata("plugin", manifest.Key);
        }

        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, ManifestReader.FileName), manifestText);

        foreach ((string relative, byte[] bytes) in contents)
        {
            string target = ManifestReader.ResolveInside(destination, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }

        return new InstalledPlugin(manifest, installDigest, PluginState.Installed, destination, clock.UtcNow);
    }

    /// <summary>
    /// Rilegge un'installazione e ne verifica di nuovo i digest. Serve prima di usarla: un file
    /// sostituito dopo l'installazione non deve poter essere caricato.
    /// </summary>
    public static PluginManifest Verify(string root, string expectedInstallDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedInstallDigest);

        string manifestPath = Path.Combine(root, ManifestReader.FileName);

        if (!File.Exists(manifestPath))
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "The installed package has no manifest.")
                .WithMetadata("reason", "manifest_missing");
        }

        string manifestText = File.ReadAllText(manifestPath);
        PluginManifest manifest = ManifestReader.Read(manifestText);

        foreach (PluginFile file in manifest.Files)
        {
            string path = ManifestReader.ResolveInside(root, file.Path);

            if (!File.Exists(path)
                || !string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
                    file.Sha256,
                    StringComparison.Ordinal))
            {
                throw new PaRouterException(ErrorCode.InputInvalid, "The installed package was modified.")
                    .WithMetadata("reason", "digest_mismatch")
                    .WithMetadata("path", file.Path);
            }
        }

        string digest = ComputeInstallDigest(manifestText, manifest);

        if (!string.Equals(digest, expectedInstallDigest, StringComparison.Ordinal))
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "The install digest does not match the catalogue.")
                .WithMetadata("reason", "install_digest_mismatch");
        }

        return manifest;
    }

    /// <summary>
    /// Digest dell'installazione: manifesto piu' l'elenco ordinato di percorso e digest dei file.
    /// L'ordine e' fissato per rendere il valore riproducibile.
    /// </summary>
    public static string ComputeInstallDigest(string manifestText, PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifestText);
        ArgumentNullException.ThrowIfNull(manifest);

        StringBuilder builder = new();

        builder.Append(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifestText))));

        foreach (PluginFile file in manifest.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            builder.Append('|').Append(file.Path).Append('|').Append(file.Sha256);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// Ogni skill e ogni strumento devono puntare a file enumerati: un percorso non dichiarato
    /// non sarebbe verificato da nessun digest.
    /// </summary>
    private static void EnsureDeclared(PluginManifest manifest)
    {
        HashSet<string> declared = new(manifest.Files.Select(file => file.Path), StringComparer.Ordinal);

        foreach (PluginSkill skill in manifest.Skills)
        {
            Require(declared, skill.Entry, "skill_entry_not_declared");

            foreach (string resource in skill.Resources)
            {
                Require(declared, resource, "skill_resource_not_declared");
            }
        }

    }

    private static void Require(IReadOnlySet<string> declared, string path, string reason)
    {
        if (!declared.Contains(path))
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "The manifest references a file it does not declare.")
                .WithMetadata("reason", reason)
                .WithMetadata("path", path);
        }
    }

    /// <summary>Rappresentazione stabile della versione, per gli ordinamenti del catalogo.</summary>
    public static (int Major, int Minor, int Patch) ParseVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        string[] parts = version.Split('.');

        return (
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture));
    }
}
