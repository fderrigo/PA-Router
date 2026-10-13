using PA.Router.Abstractions.Contracts;

namespace PA.Router.Sdk;

/// <summary>
/// Facade del runtime (03-specs/HEADLESS-API-SDK.md, «SDK .NET»).
///
/// Espone il ciclo di vita, non una finta chat completion: creare una sessione, eseguire un turno,
/// leggerne lo stato, osservarne gli eventi, annullarlo, cancellare la sessione. Profilo, canale e
/// capability non sono parametri: li fissa la fabbrica che ha costruito il runtime.
/// </summary>
public interface IPaRouterRuntime
{
    /// <summary>Crea una sessione e restituisce il token una volta sola.</summary>
    ValueTask<CreatedSession> CreateSessionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Verifica il token di sessione con un confronto a tempo costante. Una sessione inesistente e
    /// un token sbagliato danno lo stesso esito: chi chiede non deve poter distinguere i due casi.
    /// </summary>
    bool Authorize(string sessionId, string sessionToken);

    /// <summary>
    /// Avvia un turno e restituisce subito lo stato. La chiave di idempotenza e' obbligatoria ed e'
    /// legata alla sessione: la stessa chiave con la stessa richiesta restituisce la stessa
    /// esecuzione, con una richiesta diversa e' un conflitto.
    /// </summary>
    ValueTask<ExecutionSnapshot> ExecuteAsync(
        string sessionId,
        ApplicationRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Stato corrente di un'esecuzione.</summary>
    ExecutionSnapshot GetExecution(string executionId);

    /// <summary>
    /// Sequenze ancora disponibili per un'esecuzione: la piu' bassa e la piu' alta conservate.
    ///
    /// Serve a distinguere tre casi che altrimenti si confondono in uno stream vuoto: un cursore
    /// che chiede eventi gia' rimossi dalla retention, uno che chiede una sequenza mai esistita, e
    /// uno legittimo che deve semplicemente attendere. Il primo va detto, perche' il client deve
    /// smettere di ritentare e leggere lo stato.
    /// </summary>
    /// <returns>Intervallo conservato, oppure null se nessun evento esiste per quell'esecuzione.</returns>
    (int First, int Last)? EventRange(string executionId);

    /// <summary>Eventi di stato, senza testo, a partire dalla sequenza indicata.</summary>
    IAsyncEnumerable<ExecutionEvent> ObserveAsync(
        string executionId,
        int afterSequence,
        CancellationToken cancellationToken);

    /// <summary>Chiede l'annullamento. Idempotente anche su un'esecuzione gia' terminale.</summary>
    void Cancel(string executionId);

    /// <summary>
    /// Cancella la sessione e i suoi contenuti. Restituisce falso se un'esecuzione e' ancora
    /// attiva: cancellare sotto un turno in corso renderebbe incerto cio' che sta facendo.
    /// </summary>
    ValueTask<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);
}
