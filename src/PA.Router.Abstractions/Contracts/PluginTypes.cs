namespace PA.Router.Abstractions.Contracts;

/// <summary>Stato di un pacchetto nel catalogo locale.</summary>
public enum PluginState
{
    [WireValue("installed")]
    Installed,

    [WireValue("enabled")]
    Enabled,

    [WireValue("disabled")]
    Disabled,
}

/// <summary>File dichiarato dal manifesto, con il digest che deve avere.</summary>
/// <param name="Path">Percorso relativo normalizzato.</param>
/// <param name="Sha256">Digest esadecimale minuscolo.</param>
public sealed record PluginFile(string Path, string Sha256);

/// <summary>Skill dichiarata da un pacchetto.</summary>
/// <param name="Id">Identificatore della skill.</param>
/// <param name="Description">Descrizione mostrata nel catalogo.</param>
/// <param name="Entry">File di istruzioni.</param>
/// <param name="Resources">Risorse dichiarate, caricabili solo su richiesta esplicita.</param>
public sealed record PluginSkill(
    string Id,
    string Description,
    string Entry,
    IReadOnlyList<string> Resources);

/// <summary>
/// Binding verso un plugin nativo del programma ufficiale.
///
/// <paramref name="VersionRange"/> e' informativo: la compatibilita' la certifica l'adapter sulla
/// versione esatta osservata, non un intervallo dichiarato dal pacchetto (03-specs/PLUGINS-SKILLS.md).
/// </summary>
/// <param name="Provider">Programma ufficiale interessato.</param>
/// <param name="PluginId">Identificatore del plugin nel suo ecosistema.</param>
/// <param name="VersionRange">Intervallo dichiarato, informativo.</param>
/// <param name="ConfigDigest">Digest della configurazione attesa.</param>
public sealed record NativeBinding(
    string Provider,
    string PluginId,
    string VersionRange,
    string ConfigDigest);

/// <summary>Manifesto di un pacchetto PA Router.</summary>
/// <param name="SchemaVersion">Versione del formato.</param>
/// <param name="Id">Identificatore del pacchetto.</param>
/// <param name="Version">Versione semantica.</param>
/// <param name="Description">Descrizione.</param>
/// <param name="Files">File enumerati e verificati.</param>
/// <param name="Skills">Skill offerte.</param>
/// <param name="NativeBindings">Binding verso plugin nativi.</param>
public sealed record PluginManifest(
    int SchemaVersion,
    string Id,
    string Version,
    string Description,
    IReadOnlyList<PluginFile> Files,
    IReadOnlyList<PluginSkill> Skills,
    IReadOnlyList<NativeBinding> NativeBindings)
{
    /// <summary>Chiave di catalogo: identificatore e versione insieme.</summary>
    public string Key => Id + "@" + Version;
}

/// <summary>
/// Riga del catalogo cosi' come e' conservata: il manifesto resta il testo su cui l'operatore ha
/// deciso, e viene interpretato da chi ha il lettore severo, non dallo storage.
/// </summary>
/// <param name="Id">Identificatore del pacchetto.</param>
/// <param name="Version">Versione.</param>
/// <param name="InstallDigest">Digest dell'installazione.</param>
/// <param name="ManifestJson">Manifesto conservato per intero.</param>
/// <param name="State">Stato nel catalogo.</param>
/// <param name="InstalledAt">Istante di installazione.</param>
public sealed record PluginRecord(
    string Id,
    string Version,
    string InstallDigest,
    string ManifestJson,
    PluginState State,
    DateTimeOffset InstalledAt);

/// <summary>Pacchetto installato nel catalogo locale.</summary>
/// <param name="Manifest">Manifesto verificato.</param>
/// <param name="InstallDigest">Digest di manifesto e file, che identifica questa installazione.</param>
/// <param name="State">Stato corrente.</param>
/// <param name="Root">Directory di installazione.</param>
/// <param name="InstalledAt">Istante di installazione.</param>
public sealed record InstalledPlugin(
    PluginManifest Manifest,
    string InstallDigest,
    PluginState State,
    string Root,
    DateTimeOffset InstalledAt);

/// <summary>
/// Autorizzazione dell'operatore a un pacchetto, per progetto.
///
/// E' legata al digest dell'installazione: un aggiornamento produce un digest nuovo e quindi non
/// eredita il permesso — la nuova versione nasce disabilitata.
/// </summary>
/// <param name="InstallDigest">Installazione autorizzata.</param>
/// <param name="ProjectId">Progetto in cui vale.</param>
/// <param name="GrantedAt">Istante della concessione.</param>
/// <param name="RevokedAt">Istante della revoca, quando avvenuta.</param>
public sealed record PluginGrant(
    string InstallDigest,
    Guid ProjectId,
    DateTimeOffset GrantedAt,
    DateTimeOffset? RevokedAt)
{
    /// <summary>Vero se il grant e' ancora valido.</summary>
    public bool IsActive => RevokedAt is null;
}

/// <summary>Istruzioni di una skill, gia' verificate e filtrate.</summary>
/// <param name="SkillId">Skill caricata.</param>
/// <param name="PluginId">Pacchetto di origine.</param>
/// <param name="InstallDigest">Installazione da cui proviene.</param>
/// <param name="Instructions">Testo di SKILL.md.</param>
/// <param name="Resources">Risorse richieste esplicitamente, nel loro ordine.</param>
/// <param name="Bytes">Byte complessivi caricati.</param>
public sealed record LoadedSkill(
    string SkillId,
    string PluginId,
    string InstallDigest,
    string Instructions,
    IReadOnlyList<string> Resources,
    int Bytes);

/// <summary>Limiti delle estensioni, da 03-specs/PLUGINS-SKILLS.md.</summary>
public static class ExtensionLimits
{
    /// <summary>Byte massimi del file di istruzioni di una skill.</summary>
    public const int MaxSkillBytes = 32 * 1024;

    /// <summary>Byte massimi delle risorse caricate insieme a una skill.</summary>
    public const int MaxResourceBytes = 128 * 1024;

    /// <summary>File massimi in un pacchetto.</summary>
    public const int MaxFiles = 128;
}
