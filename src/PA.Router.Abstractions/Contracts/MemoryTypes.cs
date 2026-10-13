namespace PA.Router.Abstractions.Contracts;

/// <summary>Tipo di osservazione, come da <c>observations.kind</c>.</summary>
public enum ObservationKind
{
    [WireValue("decision")]
    Decision,

    [WireValue("discovery")]
    Discovery,

    [WireValue("bugfix")]
    Bugfix,

    [WireValue("test_result")]
    TestResult,

    [WireValue("handover")]
    Handover,

    [WireValue("note")]
    Note,
}

/// <summary>
/// Stato di verifica di un'osservazione.
///
/// Il default di qualunque output di un modello e' <see cref="Unverified"/>: un test «passato»
/// diventa <see cref="Verified"/> solo se il runtime ha osservato exit code e riferimento
/// all'evidenza. Una frase del modello non basta (03-specs/MEMORY.md).
/// </summary>
public enum Verification
{
    [WireValue("verified")]
    Verified,

    [WireValue("unverified")]
    Unverified,
}

/// <summary>Ampiezza di condivisione di un'osservazione.</summary>
public enum MemoryScopeKind
{
    [WireValue("session")]
    Session,

    [WireValue("project")]
    Project,
}

/// <summary>
/// Dove viene indicizzata un'osservazione, cioe' quale servizio di embedding ne calcola i vettori.
///
/// Non e' una preferenza: e' l'unico punto in cui il testo di un ricordo puo' lasciare la
/// macchina. La destinazione si decide alla nascita dell'osservazione, dalla classe del dato, e
/// non cambia piu' — un ricordo nato locale resta locale, perche' riclassificarlo e spedirlo dopo
/// sarebbe una perdita differita (03-specs/SEMANTIC-SEARCH.md).
/// </summary>
public enum MemoryDestination
{
    /// <summary>Servizio locale: la destinazione predefinita e l'unica per tutto cio' che non e' pubblico.</summary>
    [WireValue("local")]
    Local,

    /// <summary>
    /// Servizio potenziato fuori dalla macchina, solo per la classe <c>public</c>. Tolto da ADR-032: nessun ricordo nuovo
    /// nasce cosi', e il valore resta per le righe scritte prima.
    /// </summary>
    [WireValue("enhanced")]
    Enhanced,
}

/// <summary>Tipo di lavoro asincrono sulla memoria.</summary>
public enum MemoryJobKind
{
    [WireValue("index")]
    Index,

    [WireValue("purge")]
    Purge,
}

/// <summary>Stato di un lavoro di memoria.</summary>
public enum MemoryJobState
{
    [WireValue("pending")]
    Pending,

    [WireValue("running")]
    Running,

    [WireValue("completed")]
    Completed,

    [WireValue("failed")]
    Failed,
}

/// <summary>Stato dell'indice al momento della risposta.</summary>
public enum MemoryIndexStatus
{
    [WireValue("ready")]
    Ready,

    [WireValue("rebuilding")]
    Rebuilding,

    [WireValue("unavailable")]
    Unavailable,
}

/// <summary>Modo con cui una ricerca e' stata eseguita.</summary>
public enum MemorySearchMode
{
    /// <summary>Vettori e lessicale fusi: e' la ricerca semantica.</summary>
    [WireValue("hybrid")]
    Hybrid,

    /// <summary>
    /// Solo lessicale, perche' l'embedding locale non era disponibile e il profilo non lo
    /// richiedeva. Va dichiarato: chiamarla ricerca semantica sarebbe falso.
    /// </summary>
    [WireValue("lexical_only")]
    LexicalOnly,
}

/// <summary>
/// Limiti della memoria locale, fissati da 03-specs/SEMANTIC-SEARCH.md e MEMORY.md.
///
/// Sono limiti dichiarati, non stime: superarli produce MEMORY_LIMIT e manutenzione esplicita,
/// mai la cancellazione automatica di ricordi per fare spazio.
/// </summary>
public static class MemoryLimits
{
    /// <summary>Byte massimi del testo sintetico di un'osservazione.</summary>
    public const int MaxObservationBytes = 8 * 1024;

    /// <summary>Byte massimi di un chunk.</summary>
    public const int MaxChunkBytes = 2048;

    /// <summary>Sovrapposizione massima fra chunk consecutivi.</summary>
    public const int MaxChunkOverlapBytes = 256;

    /// <summary>Chunk massimi per progetto.</summary>
    public const int MaxChunksPerProject = 20_000;

    /// <summary>
    /// Dimensione massima di un vettore ammessa da un'installazione che non dichiara altro.
    ///
    /// E' un valore predefinito, non una proprieta' del prodotto: quanto costa un vettore lungo
    /// dipende dalla macchina su cui gira, e la macchina di chi sviluppa non e' un vincolo di
    /// progetto. Un'installazione con piu' memoria e piu' disco puo' alzarlo in
    /// <c>memory.maxEmbeddingDimension</c> e usare modelli piu' capaci.
    /// </summary>
    public const int DefaultMaxDimension = 2048;

    /// <summary>
    /// Tetto che nessuna configurazione puo' superare.
    ///
    /// Esiste perche' un limite configurabile senza un massimo non e' un limite: i vettori vivono
    /// in memoria durante la ricerca, e un valore assurdo non produrrebbe un errore di
    /// configurazione ma un esaurimento di memoria a meta' di un turno. Quattromila componenti
    /// coprono ogni modello di embedding pubblicato finora con margine.
    /// </summary>
    public const int AbsoluteMaxDimension = 4096;

    /// <summary>Budget globale della cache degli indici.</summary>
    public const long CacheBudgetBytes = 256L * 1024 * 1024;

    /// <summary>Risultati richiedibili da una ricerca.</summary>
    public const int MaxSearchLimit = 20;

    /// <summary>Risultati restituiti quando il chiamante non chiede altro.</summary>
    public const int DefaultSearchLimit = 5;

    /// <summary>Candidati conservati da ciascuna delle due liste prima della fusione.</summary>
    public const int CandidatesPerList = 50;

    /// <summary>
    /// Somiglianza minima perche' un ricordo sia un **risultato** e non soltanto il piu' vicino.
    ///
    /// La ricerca vettoriale ordina per coseno e restituisce i primi: senza un pavimento restituisce
    /// sempre qualcosa, anche quando non c'e' niente di pertinente. Con un ricordo solo in archivio,
    /// qualunque domanda trovava quel ricordo.
    ///
    /// Non e' una misura: e' un **valore scelto**, e dipende dal modello di embedding. Sul modello osservato su questa
    /// macchina (`embeddinggemma`, vettori normalizzati) 0,5 separava le domande pertinenti — un
    /// saluto contro «saluto» 0,504, «come stai» 0,573; «allenatore del milan» contro il ricordo
    /// giusto 0,700 — da quelle che non lo erano — «ricetta della carbonara» contro un saluto 0,423,
    /// «fisica quantistica» 0,357. Dal 24 settembre 2026 (ADR-032, fase 2) e' una costante interna: il campo
    /// `memory.minimumSimilarity` non esiste piu', e `pa config migrate` lo toglie.
    /// </summary>
    public const double DefaultMinimumSimilarity = 0.5;

    /// <summary>Costante della Reciprocal Rank Fusion.</summary>
    public const int FusionConstant = 60;

    /// <summary>
    /// Peso della lista lessicale quando esistono anche i vettori.
    ///
    /// La RRF a pari dignita' presuppone due recuperatori di qualita' confrontabile. Sul corpus
    /// versionato — parafrasi senza parole in comune — non e' cosi': il lessicale porta 0,233 di
    /// recall@5 contro 0,700 dei vettori, e fondendo alla pari il risultato **scende** a 0,333,
    /// perche' con k = 60 un documento mediocre presente in entrambe le liste
    /// (2 / 91 = 0,022) supera un documento ottimo presente in una sola (1 / 61 = 0,016).
    ///
    /// Con peso 0,05 il lessicale torna a fare cio' per cui serve — spareggiare fra candidati che
    /// i vettori giudicano simili, e recuperare le corrispondenze esatte di termine — senza
    /// governare l'ordine: 0,767 sullo stesso corpus, sopra i soli vettori.
    ///
    /// Misure in evidence/live-2026-09-11/fusion-strategies.txt, sei modelli locali.
    /// </summary>
    public const double HybridLexicalWeight = 0.05;

    /// <summary>Byte massimi dell'elenco restituito da una ricerca.</summary>
    public const int MaxSearchResultBytes = 4 * 1024;

    /// <summary>Byte massimi restituiti da un fetch completo.</summary>
    public const int MaxFetchBytes = 32 * 1024;

    /// <summary>Osservazioni richiedibili in un solo fetch.</summary>
    public const int MaxFetchIds = 10;

    /// <summary>Vicini restituiti da <c>timeline</c>, per lato.</summary>
    public const int MaxTimelineNeighbours = 5;

    /// <summary>Tentativi massimi di un lavoro di indicizzazione.</summary>
    public const int MaxJobAttempts = 3;
}

/// <summary>
/// Ambito autorizzato di una interrogazione della memoria.
///
/// Lo scope arriva dalla sessione o dall'host, mai dai parametri di un agente: un modello che
/// chiedesse «cerca in tutti i progetti» non otterrebbe un ambito piu' ampio, perche' l'ambito
/// non e' un argomento della sua richiesta (03-specs/MEMORY.md).
/// </summary>
/// <param name="ProjectId">Progetto autorizzato.</param>
/// <param name="SessionId">Sessione corrente.</param>
/// <param name="Channel">Canale che sta chiedendo.</param>
/// <param name="IncludeProjectScope">Vero se il canale puo' vedere la memoria di progetto.</param>
/// <param name="MaxClassification">Classe massima leggibile in questo ambito.</param>
/// <param name="RequirePublishedForChatbot">Vero se solo le note pubblicate sono visibili.</param>
public sealed record MemoryScope(
    Guid ProjectId,
    string SessionId,
    Channel Channel,
    bool IncludeProjectScope,
    DataClass MaxClassification,
    bool RequirePublishedForChatbot)
{
    /// <summary>
    /// Ambito della CLI personale: sessione corrente piu' memoria di progetto public/internal.
    /// </summary>
    public static MemoryScope PersonalCli(Guid projectId, string sessionId) =>
        new(projectId, sessionId, Channel.PersonalCli, IncludeProjectScope: true, DataClass.Sensitive, false);

    /// <summary>
    /// Ambito del chatbot: sessione corrente piu' le sole note che l'operatore ha pubblicato per
    /// quell'uso. <c>public</c> non significa «pubblicabile al chatbot».
    /// </summary>
    public static MemoryScope Chatbot(Guid projectId, string sessionId) =>
        new(projectId, sessionId, Channel.Chatbot, IncludeProjectScope: true, DataClass.Internal, true);
}

/// <summary>
/// Osservazione conservata. Il contenuto e' il testo sintetico deterministico, non il trascritto
/// grezzo di un provider.
/// </summary>
/// <param name="Id">Identificatore.</param>
/// <param name="ProjectId">Progetto.</param>
/// <param name="SessionId">Sessione che l'ha prodotta.</param>
/// <param name="ExecutionId">Esecuzione di origine, quando esiste.</param>
/// <param name="EventKey">Chiave di deduplica.</param>
/// <param name="Kind">Tipo.</param>
/// <param name="Verification">Stato di verifica.</param>
/// <param name="Classification">Classe del dato osservata dalla privacy.</param>
/// <param name="Scope">Ampiezza di condivisione.</param>
/// <param name="PublishedForChatbot">Vero se l'operatore l'ha pubblicata per il chatbot.</param>
/// <param name="Version">Versione, crescente da 1.</param>
/// <param name="Content">Testo sintetico.</param>
/// <param name="EvidenceRefs">Riferimenti alle prove osservate.</param>
/// <param name="Supersedes">Osservazione che questa sostituisce, quando dichiarato.</param>
/// <param name="CreatedAt">Istante di creazione.</param>
/// <param name="ExpiresAt">Scadenza.</param>
public sealed record ObservationRecord(
    string Id,
    Guid ProjectId,
    string SessionId,
    string? ExecutionId,
    string EventKey,
    ObservationKind Kind,
    Verification Verification,
    DataClass Classification,
    MemoryScopeKind Scope,
    bool PublishedForChatbot,
    int Version,
    string Content,
    IReadOnlyList<string> EvidenceRefs,
    string? Supersedes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    MemoryDestination Destination = MemoryDestination.Local)
{
    /// <summary>Il testo non entra nella resa: un'osservazione finisce nei log come identita'.</summary>
    public override string ToString() =>
        "Observation { " + Id + ", " + Kind.ToWire() + ", v"
        + Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + " }";
}

/// <summary>Frammento indicizzabile di un'osservazione.</summary>
/// <param name="Id">Identificatore del chunk.</param>
/// <param name="ObservationId">Osservazione di origine.</param>
/// <param name="Ordinal">Posizione, da 0.</param>
/// <param name="ContentHmac">HMAC del contenuto del chunk.</param>
/// <param name="StartByte">Offset in byte nel testo dell'osservazione.</param>
/// <param name="ByteLength">Lunghezza in byte.</param>
/// <param name="Text">Testo del chunk; non viene mai scritto in chiaro su disco.</param>
public sealed record MemoryChunk(
    string Id,
    string ObservationId,
    int Ordinal,
    string ContentHmac,
    int StartByte,
    int ByteLength,
    string Text)
{
    /// <inheritdoc />
    public override string ToString() =>
        "Chunk { " + Id + ", ordinal "
        + Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + " }";
}

/// <summary>Lavoro asincrono sulla memoria.</summary>
/// <param name="Id">Identificatore.</param>
/// <param name="ObservationId">Osservazione interessata.</param>
/// <param name="ObservationVersion">Versione dell'osservazione.</param>
/// <param name="ModelFingerprint">Fingerprint del modello di embedding.</param>
/// <param name="Kind">Indicizzazione o rimozione.</param>
/// <param name="State">Stato corrente.</param>
/// <param name="Attempts">Tentativi gia' consumati.</param>
/// <param name="AvailableAtMs">Istante dal quale il lavoro puo' essere preso.</param>
/// <param name="LeaseExpiresAtMs">Scadenza del lease, quando in esecuzione.</param>
/// <param name="ErrorCode">Ultimo errore, quando fallito.</param>
public sealed record MemoryJob(
    string Id,
    string ObservationId,
    int ObservationVersion,
    string ModelFingerprint,
    MemoryJobKind Kind,
    MemoryJobState State,
    int Attempts,
    long AvailableAtMs,
    long? LeaseExpiresAtMs,
    string? ErrorCode,
    MemoryDestination Destination = MemoryDestination.Local);

/// <summary>Un risultato di ricerca: identita', titolo consentito e punteggio.</summary>
/// <param name="ObservationId">Osservazione trovata.</param>
/// <param name="Kind">Tipo.</param>
/// <param name="Verification">Stato di verifica, che non e' il punteggio.</param>
/// <param name="CreatedAt">Data.</param>
/// <param name="Title">Titolo breve gia' filtrato.</param>
/// <param name="Score">Punteggio di fusione.</param>
public sealed record MemoryHit(
    string ObservationId,
    ObservationKind Kind,
    Verification Verification,
    DateTimeOffset CreatedAt,
    string Title,
    double Score);

/// <summary>Esito di una ricerca.</summary>
/// <param name="Hits">Risultati, gia' rivalidati.</param>
/// <param name="Mode">Ibrida o solo lessicale.</param>
/// <param name="IndexStatus">Stato dell'indice al momento della risposta.</param>
/// <param name="ModelFingerprint">Fingerprint usato, quando la ricerca era semantica.</param>
public sealed record MemorySearchResult(
    IReadOnlyList<MemoryHit> Hits,
    MemorySearchMode Mode,
    MemoryIndexStatus IndexStatus,
    string? ModelFingerprint);

/// <summary>
/// Archivio delle osservazioni.
///
/// Le letture sono sempre autorizzate a monte: non esiste un metodo che restituisca tutto e lasci
/// il filtro al chiamante.
/// </summary>
public interface IObservationStore
{
    /// <summary>
    /// Registra un'osservazione. Un <c>eventKey</c> gia' presente non produce un secondo ricordo:
    /// restituisce quello esistente, perche' un replay non e' un fatto nuovo.
    /// </summary>
    ValueTask<ObservationRecord> AppendAsync(
        ObservationRecord observation,
        IReadOnlyList<MemoryChunk> chunks,
        CancellationToken cancellationToken);

    /// <summary>Legge le osservazioni visibili nell'ambito indicato.</summary>
    ValueTask<IReadOnlyList<ObservationRecord>> ReadAuthorizedAsync(
        MemoryScope scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// Legge le sole osservazioni richieste, con lo stesso filtro. Serve alla rivalidazione: fra
    /// il punteggio e la risposta un ricordo puo' essere stato revocato o essere scaduto.
    /// </summary>
    ValueTask<IReadOnlyList<ObservationRecord>> ReadAuthorizedAsync(
        MemoryScope scope,
        IReadOnlyList<string> observationIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marca un'osservazione come cancellata e accoda la rimozione dei vettori. I dati rimossi non
    /// si ricostruiscono dagli embedding: il purge tocca anche quelli e la cache.
    /// </summary>
    ValueTask TombstoneAsync(string observationId, CancellationToken cancellationToken);
}

/// <summary>
/// Ricerca nella memoria locale. Lo <see cref="MemoryScope"/> arriva gia' validato dal runtime.
/// </summary>
public interface IMemorySearch
{
    /// <summary>Elenco compatto di risultati per significato.</summary>
    ValueTask<MemorySearchResult> SearchAsync(
        MemoryScope scope,
        string query,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Contesto temporale attorno a un'osservazione autorizzata.</summary>
    ValueTask<IReadOnlyList<MemoryHit>> TimelineAsync(
        MemoryScope scope,
        string observationId,
        int before,
        int after,
        CancellationToken cancellationToken);

    /// <summary>Corpi delle sole osservazioni richieste, con nuova verifica dell'ambito.</summary>
    ValueTask<IReadOnlyList<ObservationRecord>> GetAsync(
        MemoryScope scope,
        IReadOnlyList<string> observationIds,
        CancellationToken cancellationToken);
}
