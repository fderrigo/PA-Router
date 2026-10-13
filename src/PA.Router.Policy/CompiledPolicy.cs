using PA.Router.Abstractions.Contracts;

namespace PA.Router.Policy;

/// <summary>
/// Vincoli effettivi dopo la composizione. E' il solo oggetto che la valutazione consulta: dopo
/// la compilazione non si torna a leggere i documenti di partenza, cosi' una modifica a meta'
/// turno non cambia le regole sotto una richiesta gia' avviata.
/// </summary>
/// <param name="ProfileId">Profilo di origine.</param>
/// <param name="Zones">
/// Zone di fiducia del profilo (ADR-029): dal 25 settembre 2026 (ADR-032, fase 4) l'unica regola su dove vanno i dati.
/// </param>
/// <param name="DenyAll">
/// Vero quando la composizione nega ogni candidato. Dalla fase 4 nessun profilo lo produce da solo: un elenco dei servizi
/// vuoto vuol dire tutti, e le collocazioni ammesse non esistono piu'.
/// </param>
/// <param name="DenyAllReason">Motivo del deny totale, quando presente.</param>
public sealed record CompiledPolicy(
    string ProfileId,
    Channel Channel,
    DataClass MinimumDataClass,
    IReadOnlySet<string> AllowedServiceIds,
    PolicyZones Zones,
    IReadOnlySet<Capability> AllowedCapabilities,
    bool RedactContacts,
    int MaxInputBytes,
    int MaxContextBytes,
    int MaxOutputBytes,
    int TimeoutSeconds,
    int MaxAttempts,
    bool AllowLocalMemoryProcessing,
    int MaxMemoryRetentionDays,
    bool DenyAll,
    PolicyReason? DenyAllReason)
{
    /// <summary>Restrizione dei limiti degli strumenti imposta dal profilo; null non restringe.</summary>
    public ToolLimitsRestriction? ToolLimits { get; init; }

    /// <summary>Regola del profilo per graduare il dato personale (ADR-029).</summary>
    public PersonalDataGrading PersonalDataGrading { get; init; } = PersonalDataGrading.Graded;

    /// <summary>
    /// Vero se il profilo ammette il servizio: ogni servizio con un elenco vuoto, i soli nominati altrimenti (ADR-032,
    /// fase 4).
    /// </summary>
    public bool AllowsService(string serviceId) => AllowedServices.Allows(AllowedServiceIds, serviceId);

    /// <summary>
    /// Spesa massima per richiesta, nella valuta del registro (ADR-032, fase 5); null senza tetto. Viene dalla
    /// configurazione, non dal profilo: vale per ogni etichetta.
    /// </summary>
    public double? MaxCostPerRequest { get; init; }

    /// <summary>Obblighi che il profilo impone alla minimizzazione del payload.</summary>
    public IReadOnlySet<Obligation> RequiredObligations =>
        RedactContacts ? new HashSet<Obligation> { Obligation.RedactContacts } : new HashSet<Obligation>();

    /// <summary>Resa compatta: identificatore e stato, nessun elenco di autorizzazioni nel log.</summary>
    public override string ToString() =>
        "CompiledPolicy { Profile = " + ProfileId + ", Channel = " + Channel.ToWire()
        + ", DenyAll = " + DenyAll.ToString(System.Globalization.CultureInfo.InvariantCulture) + " }";
}
