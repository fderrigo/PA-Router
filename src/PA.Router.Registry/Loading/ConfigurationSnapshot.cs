using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Configurazione validata di un istante, immutabile. Il reload produce un nuovo snapshot per le
/// richieste nuove; quelle gia' avviate mantengono il proprio, cosi' una modifica a meta' turno
/// non cambia le regole sotto un'esecuzione (03-specs/CONFIG.md).
///
/// I digest servono al preflight: se cambiano, la richiesta va rivalutata invece di proseguire
/// con decisioni prese su una versione diversa.
/// </summary>
/// <param name="DataDirectory">Directory dati da cui e' stato caricato.</param>
/// <param name="Config">Configurazione operativa.</param>
/// <param name="Registry">Registro dei servizi.</param>
/// <param name="Profiles">Profili di policy, indicizzati per identificatore.</param>
/// <param name="ConfigDigest">Digest canonico della configurazione.</param>
/// <param name="RegistryDigest">Digest canonico del registro.</param>
/// <param name="PolicyDigest">Digest canonico dell'insieme dei profili.</param>
/// <param name="LoadedAt">Istante del caricamento.</param>
public sealed record ConfigurationSnapshot(
    string DataDirectory,
    ConfigDocument Config,
    RegistryDocument Registry,
    IReadOnlyDictionary<string, PolicyDocument> Profiles,
    string ConfigDigest,
    string RegistryDigest,
    string PolicyDigest,
    DateTimeOffset LoadedAt)
{
    /// <summary>Digest complessivo dello snapshot.</summary>
    public string Digest { get; } = CanonicalJson.Digest([ConfigDigest, RegistryDigest, PolicyDigest]);

    /// <summary>Profilo predefinito delle sessioni personali.</summary>
    public PolicyDocument DefaultProfile => Profiles[Config.DefaultProfileId];

    /// <summary>Profilo imposto alle sessioni chatbot dal backend.</summary>
    public PolicyDocument ServiceProfile => Profiles[Config.Api.ServiceProfileId];

    /// <summary>Servizio del registro per identificatore.</summary>
    public ServiceDescriptor? FindService(string serviceId) =>
        Registry.Services.FirstOrDefault(service => string.Equals(service.Id, serviceId, StringComparison.Ordinal));

    /// <summary>Servizi che offrono una capacita', in ordine di identificatore.</summary>
    public IReadOnlyList<ServiceDescriptor> ServicesFor(ServiceCapability capability) =>
        [.. Registry.Services
            .Where(service => service.Capability == capability)
            .OrderBy(service => service.Id, StringComparer.Ordinal)];
}

/// <summary>
/// Esito di un caricamento. Un documento non valido non produce uno snapshot parziale: o la
/// configurazione e' utilizzabile per intero, o restano soltanto i problemi da correggere.
/// </summary>
/// <param name="Snapshot">Snapshot caricato, null se la configurazione non e' valida.</param>
/// <param name="Issues">Problemi rilevati, in ordine di scoperta.</param>
public sealed record ConfigurationLoadResult(
    ConfigurationSnapshot? Snapshot,
    IReadOnlyList<ValidationIssue> Issues)
{
    /// <summary>Vero se la configurazione e' utilizzabile.</summary>
    public bool IsValid => Snapshot is not null;

    /// <summary>Snapshot caricato; lancia se la configurazione non era valida.</summary>
    public ConfigurationSnapshot Require() =>
        Snapshot ?? throw new InvalidOperationException(
            "The configuration is not valid: " + Issues.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " issue(s).");
}
