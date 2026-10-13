using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Memory;
using PA.Router.Policy;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Lavoro di memoria che segue un turno, in un punto solo.
///
/// Prima di questa classe l'ambito del ricordo e lo svuotamento della coda erano scritti tre
/// volte — in <c>pa run</c>, nel REPL e nel worker — con la stessa forma e nessuna garanzia che
/// restassero uguali. Tre copie di una regola di ambito sono tre occasioni perche' un ricordo
/// finisca nel posto sbagliato, e la copia che sbaglia non fallisce: scrive.
///
/// Qui l'ambito si calcola una volta e la coda si svuota in un modo solo. Chi ha un ciclo di vita
/// proprio — il worker, che vive fra un turno e l'altro — apre la propria composizione con
/// <see cref="Open"/> e usa lo stesso indicizzatore.
/// </summary>
public static class TurnMemory
{
    /// <summary>Lavori svuotati in una passata dopo un turno.</summary>
    public const int DrainBatch = 64;

    /// <summary>
    /// Ambito del ricordo per una sessione. Lo decide il canale del profilo, mai un parametro del
    /// chiamante: un'estensione o un backend non devono poter allargare cio' che vedono.
    /// </summary>
    public static MemoryScope ScopeFor(ConfigurationSnapshot snapshot, CompiledPolicy policy, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return policy.Channel is Channel.Chatbot
            ? MemoryScope.Chatbot(snapshot.Config.Api.ProjectId, sessionId)
            : MemoryScope.PersonalCli(snapshot.Config.Api.ProjectId, sessionId);
    }

    /// <summary>
    /// Apre la composizione della memoria per una sessione, oppure restituisce null se la
    /// configurazione non la prevede. Senza servizio di embedding dichiarato non si apre nulla: la
    /// coda resta dov'e' e la ricerca resta lessicale dichiarata.
    /// </summary>
    public static MemoryStack? Open(RouterStack stack, PolicyDocument profile, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(profile);

        ConfigurationSnapshot snapshot = stack.Snapshot;

        // La memoria durevole richiede la conservazione dei contenuti: in memory_only non esiste un
        // posto dove scrivere un ricordo, e prometterlo sarebbe falso.
        if (snapshot.Config.Memory is not { Enabled: true, IsUnconfigured: false }
            || snapshot.Config.ContentMode is not ContentMode.Encrypted)
        {
            return null;
        }

        CompiledPolicy policy = PolicyCompiler.Compile(profile, snapshot.Config);
        MemoryStack memory = MemoryStack.Create(stack, snapshot, policy, ScopeFor(snapshot, policy, sessionId));

        if (memory.Configured)
        {
            return memory;
        }

        memory.Dispose();

        return null;
    }

    /// <summary>Indicizzatore di una composizione gia' aperta.</summary>
    public static MemoryIndexer IndexerFor(MemoryStack memory)
    {
        ArgumentNullException.ThrowIfNull(memory);

        return new MemoryIndexer(memory.Store, memory.Search.Embeddings);
    }

    /// <summary>
    /// Svuota la coda lasciata da un turno.
    ///
    /// Senza worker il turno lascerebbe il proprio lavoro in attesa e il turno successivo non
    /// troverebbe nulla: era il difetto D1. Chi possiede lo store lo svuota prima di proseguire.
    /// Un errore qui non cambia l'esito del turno, che e' gia' concluso: il lavoro resta in coda
    /// con i suoi tentativi ed e' visibile a <c>pa memory status</c>.
    /// </summary>
    /// <returns>Lavori eseguiti, oppure null se l'indicizzazione e' stata rinviata da un errore.</returns>
    public static async Task<int?> DrainAsync(
        RouterStack stack,
        PolicyDocument profile,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using MemoryStack? memory = Open(stack, profile, sessionId);

        if (memory is null)
        {
            return 0;
        }

        try
        {
            return await IndexerFor(memory).DrainAsync(DrainBatch, cancellationToken).ConfigureAwait(false);
        }
        catch (PaRouterException)
        {
            return null;
        }
    }
}
