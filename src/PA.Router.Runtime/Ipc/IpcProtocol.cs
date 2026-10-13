using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Runtime.Ipc;

/// <summary>
/// Costanti e messaggi del canale IPC locale (03-specs/LOCAL-RUNTIME.md, protocollo 2).
///
/// L'autenticazione e' l'identita' del sistema operativo: la pipe e' creata con
/// <c>PipeOptions.CurrentUserOnly</c>, quindi solo lo stesso utente puo' connettersi e il client
/// verifica che il server appartenga a quell'utente. Il nonce di sfida non e' una password:
/// lega le richieste alla connessione corrente, cosi' un frame catturato altrove non e'
/// riutilizzabile. Codice ostile dello stesso utente resta fuori dal confine di isolamento.
/// </summary>
public static class IpcProtocol
{
    /// <summary>
    /// Versione del protocollo supportata da questa build.
    ///
    /// La 2 aggiunge alla richiesta di turno skill, capacita' e workspace. Con la 1 un worker li
    /// avrebbe ignorati senza errore, e un turno chiesto con una skill sarebbe partito senza: alzare
    /// la versione fa si' che client e worker di versioni diverse si rifiutino all'handshake invece
    /// di eseguire una richiesta diversa da quella scritta.
    /// </summary>
    ///
    /// La 3 aggiunge i contenuti allegati dall'operatore (S63, <c>@file</c> e <c>--attach</c>). Vale
    /// la stessa ragione: un worker della 2 li avrebbe ignorati senza errore, e la richiesta sarebbe
    /// partita **senza il file** che l'operatore aveva allegato — cioe' una richiesta diversa da
    /// quella scritta, eseguita in silenzio.
    ///
    /// La 4 aggiunge all'**esito** del turno gli effetti collaterali e i tentativi usati (S77).
    /// Stessa regola: un client della 3 non li avrebbe trovati nella risposta e avrebbe mostrato un
    /// turno con effetti possibili come se non ne avesse, cioe' un esito diverso da quello vero,
    /// in silenzio. La versione si alza quando un campo nuovo in un messaggio esistente verrebbe
    /// perso senza errore — non quando si aggiunge un metodo.
    ///
    /// La 5 aggiunge all'esito il **dettaglio del rifiuto** (S116): la difficolta' assegnata e, per
    /// ogni servizio, i numeri contro cui e' stato misurato. Un client della 4 avrebbe mostrato i
    /// soli codici — «il voto e' troppo basso» senza dire quale voto ne' quale soglia — cioe' la
    /// spiegazione monca che questo campo esiste per chiudere.
    ///
    /// La 6 aggiunge alla richiesta la **conferma dell'uscita dal perimetro** (S124, ADR-029). Un worker della 5
    /// l'avrebbe ignorata e avrebbe negato ancora, chiedendo una conferma gia' data: il terminale sarebbe rimasto a
    /// chiederla per sempre. E aggiunge all'esito la risposta **dichiarata piu' debole** di un servizio dentro il
    /// perimetro: un client della 5 l'avrebbe mostrata come una risposta qualunque.
    ///
    /// La 7 aggiunge all'esito la risposta **sotto il livello richiesto** (ADR-031): nessun servizio ammesso bastava per
    /// il profilo di domanda e ha risposto il piu' capace. Un client della 6 l'avrebbe mostrata senza l'avviso.
    public const int Version = 8;

    /// <summary>Dimensione massima di un frame, in byte.</summary>
    public const int MaxFrameBytes = 768 * 1024;

    /// <summary>Metodo di verifica del canale.</summary>
    public const string PingMethod = "ping";

    /// <summary>Metodo di stato dello store.</summary>
    public const string StoreStatusMethod = "store/status";

    /// <summary>Esecuzione di un turno dentro il worker.</summary>
    public const string ExecutionRunMethod = "execution/run";

    /// <summary>Stato della coda di indicizzazione della memoria.</summary>
    public const string MemoryStatusMethod = "memory/status";

    /// <summary>Ricerca nei ricordi dell'ambito di una sessione.</summary>
    public const string MemorySearchMethod = "memory/search";

    /// <summary>Stato della memoria visibile da una sessione, con la coda.</summary>
    public const string MemoryOverviewMethod = "memory/overview";

    /// <summary>Pacchetti installati.</summary>
    public const string PluginListMethod = "extensions/plugins";

    /// <summary>Skill offerte dai pacchetti installati.</summary>
    public const string SkillListMethod = "extensions/skills";

    /// <summary>Sessioni locali, per sceglierne una (S62).</summary>
    public const string SessionListMethod = "sessions/list";

    /// <summary>Nome leggibile di una sessione.</summary>
    public const string SessionNameMethod = "sessions/name";

    /// <summary>Archiviazione di una sessione.</summary>
    public const string SessionArchiveMethod = "sessions/archive";

    /// <summary>Turni conservati di una sessione.</summary>
    public const string SessionTranscriptMethod = "sessions/transcript";

    /// <summary>Biforca di una sessione.</summary>
    public const string SessionForkMethod = "sessions/fork";

    /// <summary>
    /// Eventi dei turni di una sessione, trasmessi finche' il client resta in ascolto (S65).
    ///
    /// La sottoscrizione e' **per sessione** perche' il client la sessione la conosce prima del
    /// turno, mentre l'identificatore dell'esecuzione lo assegna il runtime. Cosi' lo stesso metodo
    /// serve a seguire il proprio turno e ad **attaccarsi** da un altro terminale.
    /// </summary>
    public const string SessionEventsMethod = "sessions/events";

    /// <summary>
    /// Vero per i metodi che rispondono con piu' frame.
    ///
    /// Un metodo **nuovo** non alza la versione del protocollo: un worker che non lo conosce lo
    /// rifiuta esplicitamente (S37). Cambiare invece la forma della risposta di un metodo esistente
    /// l'avrebbe richiesta, ed e' la ragione per cui `execution/run` non e' stato toccato.
    /// </summary>
    public static bool IsStreaming(string method) =>
        string.Equals(method, SessionEventsMethod, StringComparison.Ordinal);

    /// <summary>Opzioni JSON condivise: nomi camelCase e nessun campo numerico implicito.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Versione dell'assembly runtime, confrontata all'handshake.</summary>
    public static string RuntimeVersion { get; } =
        typeof(IpcProtocol).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(IpcProtocol).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}

/// <summary>Saluto inviato dal worker appena la connessione si apre.</summary>
/// <param name="ProtocolVersion">Versione del protocollo del worker.</param>
/// <param name="WorkerVersion">Versione dell'eseguibile del worker.</param>
/// <param name="Nonce">Sfida legata a questa connessione, in base64.</param>
public sealed record ServerHello(int ProtocolVersion, string WorkerVersion, string Nonce);

/// <summary>Risposta del client al saluto.</summary>
/// <param name="ProtocolVersion">Versione del protocollo del client.</param>
/// <param name="ClientVersion">Versione dell'eseguibile del client.</param>
/// <param name="Nonce">Nonce ricevuto, restituito invariato.</param>
public sealed record ClientHello(int ProtocolVersion, string ClientVersion, string Nonce);

/// <summary>Esito dell'handshake.</summary>
/// <param name="Accepted">Vero se la connessione puo' proseguire.</param>
/// <param name="ErrorCode">Codice wire dell'errore quando l'handshake e' rifiutato.</param>
/// <param name="Message">Messaggio safe associato all'errore.</param>
public sealed record HandshakeResult(bool Accepted, string? ErrorCode, string? Message);

/// <summary>Richiesta applicativa.</summary>
/// <param name="Id">Identificatore di correlazione scelto dal client.</param>
/// <param name="Method">Metodo invocato.</param>
/// <param name="Payload">Contenuto della richiesta, assente per i metodi che non ne hanno.</param>
public sealed record IpcRequest(string Id, string Method, JsonElement? Payload = null);

/// <summary>Risposta applicativa.</summary>
/// <param name="Id">Identificatore della richiesta corrispondente.</param>
/// <param name="Ok">Vero se il metodo ha avuto successo.</param>
/// <param name="ErrorCode">Codice wire dell'errore, quando presente.</param>
/// <param name="Message">Messaggio safe dell'errore, quando presente.</param>
/// <param name="Payload">Contenuto della risposta.</param>
public sealed record IpcResponse(
    string Id,
    bool Ok,
    string? ErrorCode,
    string? Message,
    JsonElement? Payload,
    string? Reason = null)
{
    /// <summary>
    /// Vero sull'ultimo frame di una risposta a piu' frame (S65).
    ///
    /// Per i metodi normali resta falso e non significa niente: la risposta e' una sola e finisce
    /// dove finisce. Serve a chi legge uno stream per sapere quando smettere di aspettare, invece di
    /// dedurlo dalla chiusura della connessione — che e' un guasto, non una fine.
    /// </summary>
    public bool Final { get; init; }
}

/// <summary>Richiesta di ascolto degli eventi di una sessione (S65).</summary>
/// <param name="SessionId">Sessione da seguire.</param>
public sealed record SessionEventsRequest(string SessionId);

/// <summary>
/// Un evento di turno nella forma in cui attraversa il canale (S65).
///
/// E' la stessa forma delle righe JSONL di S64, e non e' un caso: sono lo stesso insieme chiuso di
/// eventi, reso una volta. L'unico evento che porta testo e' il blocco di risposta, che e' **gia'
/// passato dallo screening**; argomenti di strumenti, risultati e credenziali non attraversano
/// questo flusso.
/// </summary>
/// <param name="Type">Tipo dell'evento, da un insieme chiuso.</param>
/// <param name="Phase">Fase, quando l'evento ne riguarda una.</param>
/// <param name="Outcome">Esito sintetico della fase, senza contenuti.</param>
/// <param name="ElapsedMs">Durata della fase.</param>
/// <param name="Tool">Strumento eseguito.</param>
/// <param name="Source">Sorgente dello strumento.</param>
/// <param name="Round">Giro del dialogo modello-strumento.</param>
/// <param name="InputTokens">Token di ingresso dichiarati; assente significa non misurato.</param>
/// <param name="OutputTokens">Token di uscita dichiarati.</param>
/// <param name="BilledCost">Costo dichiarato dal servizio.</param>
/// <param name="Path">Percorso che il programma **dichiara** di aver toccato.</param>
/// <param name="Text">Blocco di risposta gia' esaminato.</param>
/// <param name="LoadMs">Tempo di caricamento del modello dichiarato dal servizio; assente se non dichiarato.</param>
public sealed record TurnEventPayload(
    string Type,
    string? Phase = null,
    string? Outcome = null,
    long? ElapsedMs = null,
    string? Tool = null,
    string? Source = null,
    int? Round = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    decimal? BilledCost = null,
    string? Path = null,
    string? Text = null,
    long? LoadMs = null,
    decimal? PricedCost = null,
    string? Currency = null);

/// <summary>Contenuto di <see cref="IpcProtocol.PingMethod"/>.</summary>
/// <param name="WorkerVersion">Versione del worker che ha risposto.</param>
/// <param name="ProcessId">PID del worker, per le diagnosi locali.</param>
public sealed record PingPayload(string WorkerVersion, int ProcessId);

/// <summary>Contenuto di <see cref="IpcProtocol.StoreStatusMethod"/>.</summary>
/// <param name="SchemaVersion">Versione di schema applicata.</param>
/// <param name="ContentMode">Modo di conservazione attivo.</param>
/// <param name="KeyProtector">Protector in uso, oppure null in memory_only.</param>
/// <param name="ConnectedClients">Client attualmente collegati, incluso il chiamante.</param>
public sealed record StoreStatusPayload(
    int SchemaVersion,
    string ContentMode,
    string? KeyProtector,
    int ConnectedClients);

/// <summary>
/// Richiesta di esecuzione di un turno. Il worker non si fida di un campo di canale che arriva da
/// un client: il canale viene dal profilo, e il profilo dalla configurazione posseduta dal worker
/// (03-specs/LOCAL-RUNTIME.md).
/// </summary>
/// <param name="Input">Testo dell'operatore.</param>
/// <param name="ProfileId">Profilo richiesto; null usa quello predefinito della configurazione.</param>
/// <param name="ServiceId">Servizio richiesto esplicitamente; e' un vincolo in piu', mai un permesso.</param>
/// <param name="SessionId">Sessione a cui appartiene il turno, quando esiste gia'.</param>
/// <param name="ProjectId">Progetto della sessione, quando dichiarato.</param>
/// <param name="SkillId">Skill scelta dall'operatore; il worker la carica con la stessa regola del client.</param>
/// <param name="Capabilities">Capacita' agentiche richieste, in forma wire; il worker le rivaluta.</param>
/// <param name="WorkspaceRoot">Workspace dichiarato per il turno, quando c'e'.</param>
/// <param name="WorkspaceWritable">Vero solo se l'operatore ha concesso la scrittura nel workspace.</param>
public sealed record RunRequestPayload(
    string Input,
    string? ProfileId,
    string? ServiceId,
    string? SessionId,
    string? ProjectId,
    string? SkillId = null,
    IReadOnlyList<string>? Capabilities = null,
    string? WorkspaceRoot = null,
    bool WorkspaceWritable = false,
    IReadOnlyList<ContextItemPayload>? ContextItems = null,
    bool ExternalConfirmed = false);

/// <summary>
/// Un contenuto allegato dall'operatore, nella forma in cui attraversa il canale (S63).
///
/// **Non porta una classe dichiarata**: la decide la scansione di chi esegue. Lasciar dichiarare al
/// client la classe di un contenuto significherebbe lasciargliela abbassare, e la classe di un
/// allegato e' esattamente cio' che decide dove quel testo puo' andare.
/// </summary>
/// <param name="Id">Identificatore stabile dentro il payload.</param>
/// <param name="Text">Testo allegato. **Dato non fidato**, come ogni contenuto.</param>
/// <param name="Provenance">
/// Origine, in forma wire. Il worker accetta solo <c>workspace</c> e <c>operator</c>: un client non
/// puo' dichiarare che un testo viene dalla memoria o da un agente, perche' quelle provenienze
/// portano regole diverse.
/// </param>
public sealed record ContextItemPayload(string Id, string Text, string Provenance);

/// <summary>
/// Esito di un turno eseguito dal worker. Non trasporta il payload inviato al modello ne' le
/// osservazioni: solo cio' che la CLI stampa e l'audit ha gia' registrato.
/// </summary>
/// <param name="ExecutionId">Esecuzione creata.</param>
/// <param name="Status">Stato finale, in forma wire.</param>
/// <param name="ServiceId">Servizio scelto, null quando la policy ha negato.</param>
/// <param name="Output">Testo dopo lo screening, null quando non c'e' risposta.</param>
/// <param name="ErrorCode">Codice dell'errore, quando il turno non e' completato.</param>
/// <param name="Reasons">Motivi del diniego, in ordine stabile.</param>
/// <param name="MemoryPending">Job di indicizzazione ancora in coda dopo il turno.</param>
/// <param name="SideEffects">
/// Effetti attribuiti al turno, in forma wire (S77). Mancava: con un worker vivo — cioe' nella
/// configurazione piu' usata — un agente che aveva toccato dei file chiudeva con effetti
/// <c>possible</c> e la risposta non lo diceva, mentre lo stesso turno eseguito dal client lo
/// dichiarava.
/// </param>
/// <param name="AttemptsUsed">Tentativi usati, per la stessa ragione.</param>
/// <param name="Denial">
/// Perche' nessun servizio ha preso la richiesta, con i numeri che lo hanno deciso (S116). I
/// <c>Reasons</c> restano per chi legge i soli codici; questo campo porta la difficolta' e, per
/// ogni servizio, il valore osservato accanto a quello che sarebbe servito.
/// </param>
public sealed record RunResultPayload(
    string ExecutionId,
    string Status,
    string? ServiceId,
    string? Output,
    string? ErrorCode,
    IReadOnlyList<string> Reasons,
    int MemoryPending,
    string SideEffects = "none",
    int AttemptsUsed = 0,
    DenialDetail? Denial = null,
    bool WeakPerimeter = false,
    bool BelowDemand = false);

/// <summary>Stato della coda di indicizzazione.</summary>
/// <param name="Pending">Job in attesa.</param>
/// <param name="Failed">Job falliti dopo i tentativi ammessi.</param>
/// <param name="DrainedSinceStart">Job completati da quando il worker e' vivo.</param>
public sealed record MemoryStatusPayload(int Pending, int Failed, long DrainedSinceStart);

/// <summary>
/// Lettura della memoria per una sessione. L'ambito non viaggia: lo decide il profilo, e il profilo
/// la configurazione posseduta da chi risponde.
/// </summary>
/// <param name="ProfileId">Profilo richiesto; null usa quello predefinito.</param>
/// <param name="SessionId">Sessione il cui ambito si legge.</param>
/// <param name="Query">Testo cercato, solo per la ricerca.</param>
/// <param name="Limit">Risultati richiesti; zero usa il valore predefinito.</param>
public sealed record MemoryQueryPayload(string? ProfileId, string SessionId, string? Query = null, int Limit = 0);

/// <summary>Ricordo trovato, nella forma che il client mostra.</summary>
/// <param name="Id">Identificatore dell'osservazione.</param>
/// <param name="Kind">Tipo, in forma wire.</param>
/// <param name="Verification">Stato di verifica, in forma wire.</param>
/// <param name="CreatedAt">Momento dell'osservazione.</param>
/// <param name="Title">Titolo: testo nato da un turno, quindi non fidato.</param>
public sealed record MemoryHitPayload(string Id, string Kind, string Verification, DateTimeOffset CreatedAt, string Title);

/// <summary>Esito di una ricerca nella memoria.</summary>
/// <param name="Mode">Modo della ricerca, in forma wire.</param>
/// <param name="IndexStatus">Stato dell'indice, in forma wire.</param>
/// <param name="Hits">Ricordi trovati.</param>
/// <param name="IndexError">Codice dell'ultimo guasto dell'indicizzazione, se ce n'e' uno non risolto.</param>
public sealed record MemorySearchPayload(
    string Mode,
    string IndexStatus,
    IReadOnlyList<MemoryHitPayload> Hits,
    string? IndexError = null,
    DateTimeOffset? IndexErrorAt = null);

/// <summary>Stato della memoria visibile da una sessione.</summary>
/// <param name="Configured">Vero se esiste un servizio di embedding utilizzabile.</param>
/// <param name="ModelFingerprint">Fingerprint del modello, se configurato.</param>
/// <param name="Observations">Ricordi visibili.</param>
/// <param name="JobsPending">Lavori in attesa.</param>
/// <param name="JobsRunning">Lavori in corso.</param>
/// <param name="JobsFailed">Lavori falliti.</param>
/// <param name="Generation">Generazione del progetto.</param>
public sealed record MemoryOverviewPayload(
    bool Configured,
    string? ModelFingerprint,
    int Observations,
    int JobsPending,
    int JobsRunning,
    int JobsFailed,
    long Generation,
    string? LastJobError = null,
    DateTimeOffset? LastJobErrorAt = null);

/// <summary>Pacchetto installato.</summary>
/// <param name="Id">Identificatore.</param>
/// <param name="Version">Versione.</param>
/// <param name="State">Stato, in forma wire.</param>
/// <param name="Granted">Permessi concessi nel progetto, in forma wire e in ordine.</param>
public sealed record PluginPayload(string Id, string Version, string State, bool EnabledHere);

/// <summary>Pacchetti installati.</summary>
/// <param name="Plugins">Elenco dei pacchetti.</param>
public sealed record PluginListPayload(IReadOnlyList<PluginPayload> Plugins);

/// <summary>Quali sessioni elencare (S62).</summary>
/// <param name="IncludeArchived">Vero per vedere anche quelle archiviate.</param>
/// <param name="Limit">Quante riportarne al massimo; zero usa il valore predefinito.</param>
public sealed record SessionListRequest(bool IncludeArchived = false, int Limit = 0);

/// <summary>Sessione indicata per nome o archiviazione.</summary>
/// <param name="SessionId">Sessione su cui agire.</param>
/// <param name="Name">Nome scelto dall'operatore; null lo toglie.</param>
public sealed record SessionNameRequest(string SessionId, string? Name);

/// <summary>Sessione da archiviare o da riportare in vista.</summary>
/// <param name="SessionId">Sessione su cui agire.</param>
/// <param name="Archived">Vero per archiviare, falso per disarchiviare.</param>
public sealed record SessionArchiveRequest(string SessionId, bool Archived);

/// <summary>
/// Sessione di cui leggere il trascritto.
/// </summary>
/// <param name="SessionId">Sessione da leggere.</param>
/// <param name="Limit">
/// Ultimi turni da riportare; zero usa il valore predefinito. Il tetto esiste perche' un frame del
/// canale e' limitato: una conversazione lunga non deve diventare un errore di protocollo.
/// </param>
public sealed record SessionTranscriptRequest(string SessionId, int Limit = 0);

/// <summary>Sessione da biforcare.</summary>
/// <param name="SessionId">Sessione di origine; non viene modificata.</param>
public sealed record SessionForkRequest(string SessionId);

/// <summary>Una sessione locale, nella forma che il client mostra.</summary>
/// <param name="Id">Identificatore.</param>
/// <param name="ProfileId">Profilo applicato.</param>
/// <param name="Channel">Canale, in forma wire.</param>
/// <param name="ContentMode">Modo di conservazione, in forma wire.</param>
/// <param name="CreatedAt">Creazione.</param>
/// <param name="Turns">Turni conservati.</param>
/// <param name="DisplayName">Nome scelto dall'operatore, quando c'e'. **Testo dell'operatore.**</param>
/// <param name="Archived">Vero se archiviata.</param>
public sealed record SessionPayload(
    string Id,
    string ProfileId,
    string Channel,
    string ContentMode,
    DateTimeOffset CreatedAt,
    int Turns,
    string? DisplayName,
    bool Archived);

/// <summary>Sessioni locali.</summary>
/// <param name="Sessions">Elenco, dalla piu' recente.</param>
/// <param name="PersistsHistory">Vero se questa configurazione conserva i turni.</param>
public sealed record SessionListPayload(IReadOnlyList<SessionPayload> Sessions, bool PersistsHistory);

/// <summary>Un turno del trascritto. **Contenuto della conversazione**, quindi non fidato.</summary>
/// <param name="Ordinal">Posizione, da 1.</param>
/// <param name="Input">Messaggio dell'operatore.</param>
/// <param name="Output">Risposta gia' passata dallo screening, quando c'e'.</param>
/// <param name="CreatedAt">Istante.</param>
public sealed record SessionTurnPayload(int Ordinal, string Input, string? Output, DateTimeOffset CreatedAt);

/// <summary>Trascritto di una sessione.</summary>
/// <param name="Turns">Turni riportati, in ordine.</param>
/// <param name="Total">Turni conservati in tutto: dice se quelli riportati sono solo gli ultimi.</param>
public sealed record SessionTranscriptPayload(IReadOnlyList<SessionTurnPayload> Turns, int Total);

/// <summary>Esito di una scrittura su una sessione.</summary>
/// <param name="SessionId">Sessione toccata; per la biforca e' quella nuova.</param>
public sealed record SessionAckPayload(string SessionId);

/// <summary>Skill offerta da un pacchetto.</summary>
/// <param name="SkillId">Identificatore della skill.</param>
/// <param name="PluginId">Pacchetto che la offre.</param>
/// <param name="State">Stato del pacchetto, in forma wire.</param>
public sealed record SkillPayload(string SkillId, string PluginId, string State);

/// <summary>Skill offerte dai pacchetti installati.</summary>
/// <param name="Skills">Elenco delle skill.</param>
public sealed record SkillListPayload(IReadOnlyList<SkillPayload> Skills);
