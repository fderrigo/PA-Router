using PA.Router.Abstractions.Contracts;
using PA.Router.Extensions;
using PA.Router.Infrastructure.Storage;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Pacchetto installato, con lo stato e se l'operatore lo ha abilitato in questo progetto.
///
/// Dalla S85 non ci sono piu' permessi da mostrare: un pacchetto porta solo testo di skill, e cio'
/// che conta e' se e' **abilitato qui** — lo stato del pacchetto piu' un grant attivo per questo
/// progetto e questo digest, che e' esattamente cio' che una skill pretende per entrare in un turno.
/// </summary>
/// <param name="Id">Identificatore del pacchetto.</param>
/// <param name="Version">Versione installata.</param>
/// <param name="State">Stato del pacchetto.</param>
/// <param name="InstallDigest">Digest che lega manifesto e contenuti.</param>
/// <param name="EnabledHere">Vero se esiste un grant attivo per questo progetto e questo digest.</param>
public sealed record PluginSummary(
    string Id,
    string Version,
    PluginState State,
    string InstallDigest,
    bool EnabledHere);

/// <summary>Skill offerta da un pacchetto installato.</summary>
/// <param name="SkillId">Identificatore della skill.</param>
/// <param name="PluginId">Pacchetto che la offre.</param>
/// <param name="State">Stato del pacchetto.</param>
/// <param name="Description">Descrizione dichiarata dal manifesto.</param>
public sealed record SkillSummary(string SkillId, string PluginId, PluginState State, string Description);

/// <summary>
/// Pacchetti e skill, in un punto solo.
///
/// La regola con cui una skill entra in un turno — pacchetto abilitato, grant attivo nel progetto,
/// digest verificato prima dell'uso — esisteva in due copie, una delle quali ormai senza chiamanti.
/// Una regola di permesso duplicata e' il posto dove prima o poi una copia smette di controllare
/// qualcosa (INV-22).
/// </summary>
public static class ExtensionQueries
{
    /// <summary>
    /// Vero se la configurazione ammette le estensioni. Con estensioni disattivate nessun front end
    /// elenca pacchetti o sceglie skill: la regola sta qui per non esistere in una copia per
    /// ciascuno.
    /// </summary>
    public static bool Available(ConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.Config.Extensions.Enabled;
    }

    /// <summary>
    /// Pacchetti installati, con lo stato e se sono abilitati in questo progetto.
    ///
    /// Fino alla S84 la resa mostrava tre insiemi di permessi — dichiarati, concessi, effettivi —
    /// che dalla S81 non governavano piu' niente: nessun percorso di esecuzione ne consumava uno.
    /// Resta la sola domanda che ha ancora una risposta utile: **questo pacchetto e' abilitato
    /// qui?**
    /// </summary>
    public static IReadOnlyList<PluginSummary> ListPlugins(SqlitePluginCatalog catalog, ConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(snapshot);

        Guid projectId = snapshot.Config.Api.ProjectId;
        List<PluginSummary> plugins = [];

        foreach (PluginRecord record in catalog.List())
        {
            plugins.Add(new PluginSummary(
                record.Id,
                record.Version,
                record.State,
                record.InstallDigest,
                // Un grant revocato non abilita piu' niente, e un grant di un altro progetto o di un
                // altro digest non vale qui.
                catalog.FindGrant(record.InstallDigest, projectId) is { IsActive: true }));
        }

        return plugins;
    }

    /// <summary>
    /// Skill offerte dai pacchetti installati. Si leggono identificatori e descrizioni: i corpi
    /// restano sul disco finche' qualcuno non chiede quella skill.
    /// </summary>
    public static IReadOnlyList<SkillSummary> ListSkills(SqlitePluginCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        List<SkillSummary> skills = [];

        foreach (PluginRecord record in catalog.List())
        {
            PluginManifest manifest = ManifestReader.Read(record.ManifestJson);

            foreach ((string skillId, string description) in SkillLoader.Catalogue(manifest))
            {
                skills.Add(new SkillSummary(skillId, record.Id, record.State, description));
            }
        }

        return skills;
    }

    /// <summary>
    /// Skill pronta per entrare nel contesto di un turno, oppure null se non puo' entrarci.
    ///
    /// Un pacchetto disabilitato o senza grant attivo per il progetto non produce testo: il
    /// chiamante nega. Il digest viene verificato adesso, non all'installazione: un file cambiato
    /// sul disco dopo l'abilitazione non entra.
    /// </summary>
    public static ContextItem? LoadForTurn(ConfigurationSnapshot snapshot, RouterStack stack, string skillId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        SqlitePluginCatalog catalog = stack.PluginCatalog();
        string pluginRoot = Path.Combine(snapshot.DataDirectory, snapshot.Config.Extensions.PluginRoot);

        foreach (PluginRecord record in catalog.List())
        {
            PluginManifest manifest = ManifestReader.Read(record.ManifestJson);

            if (!manifest.Skills.Any(skill => string.Equals(skill.Id, skillId, StringComparison.Ordinal)))
            {
                continue;
            }

            if (record.State is not PluginState.Enabled
                || catalog.FindGrant(record.InstallDigest, snapshot.Config.Api.ProjectId) is not { IsActive: true })
            {
                return null;
            }

            string root = Path.Combine(pluginRoot, record.Id, record.Version);

            PluginInstaller.Verify(root, record.InstallDigest);

            return SkillLoader.ToContextItem(SkillLoader.Load(
                new InstalledPlugin(manifest, record.InstallDigest, record.State, root, record.InstalledAt),
                skillId));
        }

        return null;
    }
}
