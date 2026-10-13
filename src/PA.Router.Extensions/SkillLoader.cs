using System.Security.Cryptography;
using System.Text;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Extensions;

/// <summary>
/// Caricamento progressivo delle skill (03-specs/PLUGINS-SKILLS.md, «Skill loading»).
///
/// Il catalogo legge identificatori e descrizioni, non i corpi: elencare una skill non significa
/// caricarne le istruzioni. Il caricamento avviene solo per la skill che l'operatore ha scelto, con
/// il suo file di istruzioni e le sole risorse dichiarate che vengono richieste esplicitamente.
///
/// Nessuna scoperta automatica basata sul testo: una skill non si attiva perche' un modello ha
/// scritto che servirebbe. E il contenuto caricato resta testo selezionato dall'operatore — non
/// diventa policy, e non concede permessi che il grant non contiene.
/// </summary>
public static class SkillLoader
{
    /// <summary>Elenca le skill di un pacchetto senza leggerne i corpi.</summary>
    public static IReadOnlyList<(string SkillId, string Description)> Catalogue(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return [.. manifest.Skills.Select(skill => (skill.Id, skill.Description))];
    }

    /// <summary>
    /// Carica una skill e le risorse richieste.
    /// </summary>
    /// <param name="plugin">Pacchetto installato, gia' verificato.</param>
    /// <param name="skillId">Skill scelta dall'operatore.</param>
    /// <param name="requestedResources">
    /// Risorse da includere; devono essere fra quelle dichiarate dalla skill. Un elenco vuoto
    /// carica le sole istruzioni.
    /// </param>
    public static LoadedSkill Load(
        InstalledPlugin plugin,
        string skillId,
        IReadOnlyList<string>? requestedResources = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        PluginSkill skill = plugin.Manifest.Skills.FirstOrDefault(
            item => string.Equals(item.Id, skillId, StringComparison.Ordinal))
            ?? throw Unsupported("skill_not_found", skillId);

        byte[] instructions = ReadVerified(plugin, skill.Entry);

        if (instructions.Length > ExtensionLimits.MaxSkillBytes)
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "The skill instructions exceed the declared limit.")
                .WithMetadata("reason", "skill_too_large")
                .WithMetadata("skill", skillId);
        }

        List<string> resources = [];
        int resourceBytes = 0;

        foreach (string requested in requestedResources ?? [])
        {
            if (!skill.Resources.Contains(requested, StringComparer.Ordinal))
            {
                // Una risorsa non dichiarata dalla skill non e' caricabile chiedendola per nome.
                throw new PaRouterException(ErrorCode.InputInvalid, "That resource is not declared by the skill.")
                    .WithMetadata("reason", "resource_not_declared")
                    .WithMetadata("resource", requested);
            }

            byte[] bytes = ReadVerified(plugin, requested);
            resourceBytes += bytes.Length;

            if (resourceBytes > ExtensionLimits.MaxResourceBytes)
            {
                throw new PaRouterException(ErrorCode.InputInvalid, "The requested resources exceed the declared limit.")
                    .WithMetadata("reason", "resources_too_large");
            }

            resources.Add(Encoding.UTF8.GetString(bytes));
        }

        return new LoadedSkill(
            skill.Id,
            plugin.Manifest.Id,
            plugin.InstallDigest,
            Encoding.UTF8.GetString(instructions),
            resources,
            instructions.Length + resourceBytes);
    }

    /// <summary>
    /// Testo da aggiungere al payload. E' marcato come scelto dall'operatore, distinto dalla
    /// memoria e dai documenti non fidati: chi legge il payload deve sapere da dove viene ogni
    /// parte, e nessuna delle due supera la policy.
    /// </summary>
    public static ContextItem ToContextItem(LoadedSkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);

        StringBuilder builder = new();

        builder.Append("[skill ").Append(skill.SkillId).Append(" | pacchetto ").Append(skill.PluginId)
            .Append(" | selezionata dall'operatore]\n");

        builder.Append(skill.Instructions);

        foreach (string resource in skill.Resources)
        {
            builder.Append("\n\n").Append(resource);
        }

        return new ContextItem(
            skill.PluginId + ":" + skill.SkillId,
            builder.ToString(),
            Provenance.Operator,
            DeclaredClass: null);
    }

    /// <summary>
    /// Legge un file del pacchetto verificandone il digest dichiarato: fra l'installazione e l'uso
    /// il file potrebbe essere stato sostituito.
    /// </summary>
    private static byte[] ReadVerified(InstalledPlugin plugin, string relativePath)
    {
        PluginFile declared = plugin.Manifest.Files.FirstOrDefault(
            file => string.Equals(file.Path, relativePath, StringComparison.Ordinal))
            ?? throw new PaRouterException(ErrorCode.InputInvalid, "The file is not declared by the manifest.")
                .WithMetadata("reason", "file_not_declared")
                .WithMetadata("path", relativePath);

        string path = ManifestReader.ResolveInside(plugin.Root, relativePath);

        if (!File.Exists(path))
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "A declared file is missing from the installation.")
                .WithMetadata("reason", "file_missing")
                .WithMetadata("path", relativePath);
        }

        byte[] bytes = File.ReadAllBytes(path);

        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), declared.Sha256, StringComparison.Ordinal))
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "A file does not match its declared digest.")
                .WithMetadata("reason", "digest_mismatch")
                .WithMetadata("path", relativePath);
        }

        return bytes;
    }

    private static PaRouterException Unsupported(string reason, string skillId) =>
        new PaRouterException(ErrorCode.ExtensionPermissionDenied, "That skill is not available.")
            .WithMetadata("reason", reason)
            .WithMetadata("skill", skillId);
}
