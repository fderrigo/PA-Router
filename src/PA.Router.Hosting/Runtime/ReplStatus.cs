using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Infrastructure;
using PA.Router.Policy;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>Dove puo' avvenire l'inferenza di questa sessione, dal piu' chiuso al piu' aperto.</summary>
public enum StatusReach
{
    /// <summary>Solo questa macchina.</summary>
    ThisMachine,

    /// <summary>Anche macchine della rete locale dell'operatore.</summary>
    LocalNetwork,

    /// <summary>Anche servizi di terzi.</summary>
    Cloud,
}

/// <summary>Che cosa fa la memoria in questa sessione.</summary>
public enum StatusMemory
{
    /// <summary>Spenta.</summary>
    Off,

    /// <summary>Attiva e con ricerca per significato.</summary>
    Semantic,

    /// <summary>Attiva, ma la ricerca e' solo per parole.</summary>
    LexicalOnly,

    /// <summary>Attiva e configurata in modo che nessun ricordo verra' mai scritto.</summary>
    NeverRecords,
}

/// <summary>
/// Stato del router visibile a chi sta per scrivere una richiesta.
///
/// E' un'operazione (INV-22): il calcolo sta qui, il disegno in ciascun front end. Non contiene mai
/// percorsi completi, indirizzi, nomi di credenziali, testo di una domanda o identificatori di
/// sessione: una barra di stato vive su uno schermo che qualcuno puo' guardare.
/// </summary>
/// <param name="ProfileId">Identificatore del profilo della sessione.</param>
/// <param name="Answering">Nome del servizio che risponde, o null se nessuno e' ammesso.</param>
/// <param name="Predicted">
/// Vero finche' nessuno ha ancora risposto in questa sessione. La barra non nomina un servizio che
/// non ha risposto: dire chi rispondera' richiederebbe di osservare i servizi — cioe' di aprire
/// connessioni a ogni invito — e sarebbe comunque una previsione, che il primo rifiuto smentirebbe.
/// </param>
/// <param name="Reach">Dove puo' avvenire l'inferenza.</param>
/// <param name="MinimumClass">Classe minima imposta a ogni richiesta dal profilo.</param>
/// <param name="Memory">Stato della memoria.</param>
/// <param name="MemoryStaysInSession">Vero se nessun ricordo sara' visibile da un'altra conversazione.</param>
/// <param name="ContentPersisted">Vero se i contenuti restano su disco cifrati.</param>
/// <param name="WorkspaceName">Nome della cartella del recinto, senza il percorso.</param>
/// <param name="Writable">Vero se il recinto concede la scrittura.</param>
/// <param name="Shell">Vero se il recinto concede i comandi di sistema.</param>
public sealed record StatusView(
    string ProfileId,
    string? Answering,
    bool Predicted,
    StatusReach Reach,
    DataClass MinimumClass,
    StatusMemory Memory,
    bool MemoryStaysInSession,
    bool ContentPersisted,
    string? WorkspaceName,
    bool Writable,
    bool Shell)
{
    /// <summary>Nome dato dall'operatore al profilo (<c>name</c>); null: la resa mostra l'identificatore.</summary>
    public string? ProfileName { get; init; }
}

/// <summary>
/// Costruisce lo <see cref="StatusView"/> di una sessione.
///
/// Niente probe, niente rete, nessun lock dello store: tutto viene dalla configurazione riletta e da
/// cio' che il turno precedente ha gia' detto. Una barra ridisegnata a ogni invito non puo'
/// permettersi di chiedere qualcosa a qualcuno.
/// </summary>
public static class ReplStatus
{
    /// <summary>Stato corrente per il profilo indicato.</summary>
    /// <param name="snapshot">Configurazione in vigore.</param>
    /// <param name="profileId">Profilo della sessione.</param>
    /// <param name="lastServiceId">Servizio che ha risposto all'ultimo turno, se c'e' stato.</param>
    /// <param name="workspace">Recinto della sessione, se l'operatore ne ha fissato uno.</param>
    public static StatusView For(
        ConfigurationSnapshot snapshot,
        string profileId,
        string? lastServiceId = null,
        WorkspaceDescriptor? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        PolicyDocument profile = snapshot.Profiles.TryGetValue(profileId, out PolicyDocument? found)
            ? found
            : snapshot.DefaultProfile;

        CompiledPolicy policy = PolicyCompiler.Compile(profile, snapshot.Config);

        ServiceDescriptor? answering = lastServiceId is string served ? snapshot.FindService(served) : null;

        return new StatusView(
            profile.ProfileId,
            answering is null ? null : Name(answering),
            answering is null,
            Reach(ProfileFacts.Describe(snapshot, profile.ProfileId, SystemClock.Instance)),
            policy.MinimumDataClass,
            Memory(snapshot, profileId),
            MemoryQueries.SessionScopedOnly(snapshot, profileId),
            snapshot.Config.ContentMode is ContentMode.Encrypted,
            workspace?.RootPath is string root ? Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) : null,
            workspace?.Writable == true,
            workspace?.Shell == true)
        {
            ProfileName = profile.Name,
        };
    }

    /// <summary>Nome del modello quando c'e', altrimenti l'identificatore del servizio.</summary>
    private static string Name(ServiceDescriptor service) =>
        service.Model is string model && !model.Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal)
            ? model
            : service.Id;

    /// <summary>
    /// Fin dove arrivano i dati con questa etichetta (ADR-032, fase 4): la zona piu' lontana che le regole ammettono, non
    /// oltre il servizio ammesso piu' lontano (<see cref="ProfileFacts"/>). Prima la leggeva dalla forma precedente alle
    /// zone e dalle collocazioni ammesse, che non esistono piu'.
    /// </summary>
    private static StatusReach Reach(ProfileFactsReport facts)
    {
        TrustZone farthest = new[] { facts.Public, facts.Personal, facts.Sensitive }.Max();

        return farthest >= TrustZone.ThirdPartyDpa ? StatusReach.Cloud
            : farthest >= TrustZone.Lan ? StatusReach.LocalNetwork
            : StatusReach.ThisMachine;
    }

    private static StatusMemory Memory(ConfigurationSnapshot snapshot, string profileId)
    {
        if (!MemoryQueries.Available(snapshot)) { return StatusMemory.Off; }
        if (MemoryQueries.NeverRecords(snapshot, profileId)) { return StatusMemory.NeverRecords; }

        return MemoryQueries.LexicalOnly(snapshot) ? StatusMemory.LexicalOnly : StatusMemory.Semantic;
    }
}
