using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Policy;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>Un tipo di servizio che puo' rispondere, come lo capisce chi configura.</summary>
public enum AnswererKind
{
    /// <summary>Modelli su questa macchina o nella rete locale.</summary>
    LocalModels,

    /// <summary>Modelli su una propria macchina remota.</summary>
    OwnServer,

    /// <summary>Servizi di terzi su internet.</summary>
    Internet,

    /// <summary>Agenti che leggono e modificano file.</summary>
    Agents,
}

/// <summary>
/// Che cosa comporta un'etichetta (un profilo di regole), ricavato dalle regole stesse e non scritto a mano.
/// </summary>
/// <param name="ProfileId">Identificatore dell'etichetta.</param>
/// <param name="Answerers">Per ogni tipo di servizio presente nel registro, se le regole ne ammettono almeno uno.</param>
/// <param name="Public">Zona piu' lontana per i dati pubblici.</param>
/// <param name="Personal">Zona piu' lontana per i dati personali.</param>
/// <param name="Sensitive">Zona piu' lontana per i dati sensibili.</param>
public sealed record ProfileFactsReport(
    string ProfileId,
    IReadOnlyDictionary<AnswererKind, bool> Answerers,
    TrustZone Public,
    TrustZone Personal,
    TrustZone Sensitive);

/// <summary>
/// La descrizione di un'etichetta, calcolata (deciso con l'operatore il 23 settembre 2026).
///
/// Un'etichetta e' il nome di un pacchetto di regole: il nome da solo non dice niente, e una descrizione scritta a mano
/// diventava falsa appena si cambiava una regola — «Tutto su questo computer» dopo aver ammesso un servizio su internet.
/// Qui la descrizione si ricava dalle regole, in modo deterministico: per ogni tipo di servizio del registro la stessa
/// valutazione che il setup usa per dire se un servizio e' compatibile (<see cref="ServiceSetupCatalog.Compatibility"/>),
/// e le zone del profilo compilato. Un'etichetta nuova ha la sua descrizione appena ha un nome.
/// </summary>
public static class ProfileFacts
{
    /// <summary>I fatti dell'etichetta indicata, sui servizi del registro.</summary>
    public static ProfileFactsReport Describe(ConfigurationSnapshot snapshot, string profileId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(clock);

        // Una vista solo per la valutazione: mai inviata, salvata o usata per un grant.
        ConfigurationSnapshot view = snapshot with { Config = snapshot.Config with { DefaultProfileId = profileId } };
        Dictionary<AnswererKind, bool> answerers = [];
        TrustZone? farthest = null;

        foreach (ServiceDescriptor service in snapshot.Registry.Services.Where(service => ServiceSetupCatalog.AnswersRequests(service.Capability)))
        {
            AnswererKind kind = KindOf(service);
            bool admitted = ServiceSetupCatalog.Compatibility(view, service, clock).Count == 0;
            answerers[kind] = answerers.GetValueOrDefault(kind) || admitted;

            if (admitted)
            {
                TrustZone zone = TrustZones.Of(service.InferencePlacement, service.Controls, service.ProcessingRegions);
                farthest = farthest is TrustZone current && current >= zone ? current : zone;
            }
        }

        PolicyZones zones = PolicyCompiler.Compile(snapshot.Profiles[profileId], snapshot.Config).Zones;

        // Dove i dati possono arrivare davvero: la zona delle regole, ma non oltre il servizio ammesso piu' lontano. «Tutto su
        // questo computer» ammette per le regole i dati pubblici fino a una tua macchina in UE, e senza nessun servizio li'
        // la frase avrebbe detto una cosa che non puo' succedere.
        TrustZone Reach(DataClass dataClass) => farthest is TrustZone limit && limit < zones.For(dataClass) ? limit : zones.For(dataClass);

        return new ProfileFactsReport(
            profileId,
            answerers,
            Reach(DataClass.Public),
            Reach(DataClass.Personal),
            Reach(DataClass.Sensitive));
    }

    /// <summary>Il tipo di un servizio che risponde.</summary>
    public static AnswererKind KindOf(ServiceDescriptor service)
    {
        ArgumentNullException.ThrowIfNull(service);

        return service.Kind is TargetKind.Agent ? AnswererKind.Agents
            : service.InferencePlacement switch
            {
                Placement.Loopback or Placement.Lan => AnswererKind.LocalModels,
                Placement.PrivateRemote => AnswererKind.OwnServer,
                _ => AnswererKind.Internet,
            };
    }
}
