namespace PA.Router.Abstractions.Contracts;

/// <summary>Disponibilita' del contenuto di una esecuzione.</summary>
public enum ContentAvailability
{
    [WireValue("available")]
    Available,

    [WireValue("unavailable")]
    Unavailable,

    [WireValue("expired")]
    Expired,
}

/// <summary>Sessione minima necessaria a reggere le chiavi esterne dello schema.</summary>
/// <param name="SessionId">Identificatore della sessione.</param>
/// <param name="ProfileId">Profilo applicato.</param>
/// <param name="Channel">Canale della sessione.</param>
/// <param name="ContentMode">Modo di conservazione.</param>
/// <param name="ProjectId">Progetto di appartenenza.</param>
/// <param name="TokenHmac">HMAC del token, obbligatorio per il canale chatbot.</param>
/// <param name="ExpiresAtMs">Scadenza della sessione.</param>
public sealed record SessionRecord(
    string SessionId,
    string ProfileId,
    Channel Channel,
    ContentMode ContentMode,
    Guid ProjectId,
    string? TokenHmac,
    long ExpiresAtMs);

/// <summary>
/// Transazione T1: accettazione, idempotency e audit di ingresso.
/// </summary>
/// <param name="ExecutionId">Nuova esecuzione.</param>
/// <param name="SessionId">Sessione proprietaria.</param>
/// <param name="PolicyDigest">Digest della policy applicata.</param>
/// <param name="RegistryDigest">Digest del registro applicato.</param>
/// <param name="InputHmac">HMAC dell'input, mai il testo.</param>
/// <param name="DeadlineAtMs">Deadline globale, unica per tutti i tentativi.</param>
/// <param name="IdempotencyKeyHmac">HMAC della chiave di idempotenza, quando presente.</param>
/// <param name="AuditEventId">Identificatore dell'evento di audit di accettazione.</param>
public sealed record AcceptedExecution(
    string ExecutionId,
    string SessionId,
    string PolicyDigest,
    string RegistryDigest,
    string InputHmac,
    long DeadlineAtMs,
    string? IdempotencyKeyHmac,
    string AuditEventId)
{
    /// <summary>
    /// Digest della richiesta **intera** a cui la chiave di idempotenza e' legata (S74).
    ///
    /// Non coincide con <see cref="InputHmac"/>, che copre il solo testo: due richieste con lo
    /// stesso input e istruzioni diverse sono richieste diverse, e la riga di idempotenza deve
    /// saperlo distinguere. Finche' `request_hmac` conservava l'HMAC del solo input, il confronto
    /// dopo un riavvio era **piu' debole** di quello fatto in memoria prima del riavvio: lo stesso
    /// controllo dava due risposte diverse a seconda di chi lo faceva.
    ///
    /// Null per i chiamanti che non hanno idempotenza — il terminale e il worker — dove vale
    /// l'HMAC dell'input e nulla cambia.
    /// </summary>
    public string? RequestDigest { get; init; }
}

/// <summary>Decisione di policy da persistere, gia' priva di dettagli sensibili.</summary>
/// <param name="TargetId">Candidato valutato.</param>
/// <param name="Allowed">Esito.</param>
/// <param name="ReasonsJson">Codici di negazione in JSON.</param>
/// <param name="ScoreJson">Componenti del punteggio in JSON, solo per gli ammessi.</param>
public sealed record StoredDecision(string TargetId, bool Allowed, string ReasonsJson, string? ScoreJson);

/// <summary>
/// Transazione T2: decisioni, intento di dispatch e tentativo in stato dispatching.
///
/// Deve essere committata <em>prima</em> che l'adapter parta: e' l'unica traccia che esiste se il
/// processo muore subito dopo l'avvio del target (INV-06).
/// </summary>
/// <param name="ExecutionId">Esecuzione corrente.</param>
/// <param name="AttemptId">Tentativo che sta per partire.</param>
/// <param name="Ordinal">Numero del tentativo, 1 o 2.</param>
/// <param name="TargetId">Target scelto.</param>
/// <param name="GrantNonceHmac">HMAC del nonce del grant, unico per costruzione dello schema.</param>
/// <param name="PayloadHmac">HMAC del payload autorizzato.</param>
/// <param name="SideEffects">Effetti attribuiti al tentativo al momento del dispatch.</param>
/// <param name="Decisions">Decisioni di tutti i candidati valutati.</param>
/// <param name="AuditEventId">Identificatore dell'evento di audit pre-dispatch.</param>
public sealed record DispatchIntent(
    string ExecutionId,
    string AttemptId,
    int Ordinal,
    string TargetId,
    string GrantNonceHmac,
    string PayloadHmac,
    SideEffectState SideEffects,
    IReadOnlyList<StoredDecision> Decisions,
    string AuditEventId);

/// <summary>Riferimento a un contenuto conservato, gia' scritto dallo storage.</summary>
/// <param name="BlobId">Identificatore del blob.</param>
/// <param name="Kind">Tipo di contenuto.</param>
/// <param name="RelativePath">Percorso relativo alla directory dati.</param>
/// <param name="CipherSha256">Digest del file cifrato.</param>
/// <param name="ByteLength">Lunghezza del file cifrato.</param>
/// <param name="ExpiresAtMs">Scadenza del contenuto.</param>
public sealed record StoredBlob(
    string BlobId,
    BlobKind Kind,
    string RelativePath,
    string CipherSha256,
    long ByteLength,
    long ExpiresAtMs);

/// <summary>
/// Transazione T3: esito, riferimenti ai contenuti, stato del tentativo e audit terminale.
///
/// Solo dopo il commit si puo' pubblicare <c>completed</c>: pubblicare prima significherebbe
/// annunciare un risultato che un guasto potrebbe costringere a smentire.
/// </summary>
/// <param name="ExecutionId">Esecuzione conclusa.</param>
/// <param name="AttemptId">
/// Tentativo concluso, oppure null quando l'esecuzione si e' chiusa prima di crearne uno: un
/// deny o un errore di preparazione non hanno un tentativo a cui riferirsi.
/// </param>
/// <param name="State">Stato terminale della esecuzione.</param>
/// <param name="AttemptState">Stato terminale del tentativo.</param>
/// <param name="SideEffects">Effetti collaterali attribuiti.</param>
/// <param name="ErrorCode">Codice errore, quando presente.</param>
/// <param name="UsageJson">Consumo osservato in JSON, quando presente.</param>
/// <param name="ContentAvailability">Disponibilita' del contenuto.</param>
/// <param name="Blobs">Contenuti da referenziare.</param>
/// <param name="AuditEventId">Identificatore dell'evento di audit terminale.</param>
/// <param name="Decisions">
/// Decisioni da persistire con questo esito, per i percorsi che non hanno mai creato un tentativo
/// (S83). Un turno **negato** si ferma prima del dispatch, quindi le sue decisioni non possono
/// passare da T2: entrano qui con ordinale 0, «deciso prima di ogni tentativo». Vuoto per tutti gli
/// altri percorsi, dove le decisioni sono gia' state scritte insieme all'intento di dispatch.
/// </param>
public sealed record ExecutionOutcome(
    string ExecutionId,
    string? AttemptId,
    ExecutionState State,
    AttemptState AttemptState,
    SideEffectState SideEffects,
    ErrorCode? ErrorCode,
    string? UsageJson,
    ContentAvailability ContentAvailability,
    IReadOnlyList<StoredBlob> Blobs,
    string AuditEventId,
    IReadOnlyList<StoredDecision>? Decisions = null);

/// <summary>
/// Persistenza transazionale di una esecuzione.
///
/// Le tre transazioni sono brevi e separate di proposito: fra T2 e T3 c'e' la chiamata al
/// provider, che puo' durare minuti e non deve tenere aperta alcuna transazione ne' bloccare il
/// database per le altre esecuzioni (ADR-006, AC-19).
/// </summary>
public interface IExecutionStore
{
    /// <summary>
    /// Assicura che il progetto configurato esista. La migrazione 002 crea un progetto legacy,
    /// ma <c>pa init</c> ne genera uno nuovo: senza questa riga la sessione violerebbe la chiave
    /// esterna.
    /// </summary>
    ValueTask EnsureProjectAsync(Guid projectId, string name, CancellationToken cancellationToken);

    /// <summary>Crea la sessione minima necessaria alle chiavi esterne.</summary>
    ValueTask CreateSessionAsync(SessionRecord session, CancellationToken cancellationToken);

    /// <summary>
    /// Registra un evento di audit fuori dalle tre transazioni principali. E' una transazione
    /// breve e propria: l'analisi, la valutazione e la selezione vanno registrate quando
    /// avvengono, non raccolte e scritte alla fine, dove un guasto le perderebbe tutte insieme.
    /// </summary>
    ValueTask AppendAuditAsync(AuditRecord record, CancellationToken cancellationToken);

    /// <summary>T1: accettazione, idempotency e audit di ingresso.</summary>
    ValueTask AcceptAsync(AcceptedExecution accepted, CancellationToken cancellationToken);

    /// <summary>T2: decisioni e intento di dispatch, committato prima dell'effetto esterno.</summary>
    ValueTask CommitDispatchIntentAsync(DispatchIntent intent, CancellationToken cancellationToken);

    /// <summary>T3: esito, contenuti, stato della sessione e audit terminale.</summary>
    ValueTask CompleteAsync(ExecutionOutcome outcome, CancellationToken cancellationToken);

    /// <summary>
    /// Registra un evento di stato assegnandogli la prossima sequenza dentro la stessa transazione.
    /// Serve ai percorsi terminali, che non conoscono l'ordinale del tentativo: calcolare la
    /// sequenza fuori dalla transazione lascerebbe due scritture concorrenti sullo stesso numero.
    /// </summary>
    ValueTask AppendNextStateEventAsync(
        string executionId,
        ExecutionState state,
        long expiresAtMs,
        CancellationToken cancellationToken);

    /// <summary>Registra un cambio di stato osservabile dal client, per il replay degli eventi.</summary>
    ValueTask AppendStateEventAsync(
        string executionId,
        int sequence,
        ExecutionState state,
        long expiresAtMs,
        CancellationToken cancellationToken);
}
