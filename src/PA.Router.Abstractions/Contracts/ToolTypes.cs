using System.Text.Json;

namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Strumento come lo vede il modello (ADR-020): nome, descrizione e schema dei parametri. Arriva da una
/// sorgente esterna e parte con il payload, quindi passa dalla privacy come il resto; non e' un permesso.
/// </summary>
/// <param name="Name">Nome offerto al modello, unico nella richiesta.</param>
/// <param name="Description">Descrizione della sorgente.</param>
/// <param name="InputSchemaJson">Schema JSON dei parametri, un oggetto.</param>
public sealed record ToolDefinition(string Name, string Description, string InputSchemaJson)
{
    /// <summary>Solo il nome nella resa testuale.</summary>
    public override string ToString() => "ToolDefinition { Name = " + Name + " }";
}

/// <summary>
/// Chiamata proposta dal modello. E' una **proposta**: il router decide se gli argomenti possono andare a
/// quella destinazione prima di eseguirla.
/// </summary>
/// <param name="CallId">Identificatore della chiamata nel giro, assegnato dal fornitore o dall'adapter.</param>
/// <param name="Name">Strumento chiesto.</param>
/// <param name="ArgumentsJson">Argomenti scritti dal modello, come testo JSON.</param>
public sealed record ToolCall(string CallId, string Name, string ArgumentsJson)
{
    /// <summary>
    /// Argomenti come oggetto JSON, oppure un oggetto vuoto se il modello ne ha scritti di malformati. Serve
    /// ai fornitori che vogliono un oggetto nella cronologia: la chiamata malformata non e' stata eseguita.
    /// </summary>
    public string ArgumentsObjectJson => IsJsonObject(ArgumentsJson) ? ArgumentsJson : "{}";

    /// <summary>Vero se il testo e' un oggetto JSON.</summary>
    public static bool IsJsonObject(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind is JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Nessun argomento nella resa testuale.</summary>
    public override string ToString() => "ToolCall { Id = " + CallId + ", Name = " + Name + " }";
}

/// <summary>Risultato di una chiamata, gia' esaminato, come torna al modello.</summary>
/// <param name="CallId">Chiamata a cui risponde.</param>
/// <param name="Name">Strumento.</param>
/// <param name="Content">Contenuto esaminato e, se il profilo lo chiede, redatto.</param>
/// <param name="IsError">Vero se lo strumento o il router rispondono con un errore.</param>
public sealed record ToolCallResult(string CallId, string Name, string Content, bool IsError)
{
    /// <summary>Nessun contenuto nella resa testuale.</summary>
    public override string ToString() =>
        "ToolCallResult { Id = " + CallId + ", Name = " + Name + ", Error = "
        + IsError.ToString(System.Globalization.CultureInfo.InvariantCulture) + " }";
}

/// <summary>Un giro: cio' che il modello ha scritto, le chiamate che ha chiesto e i risultati.</summary>
/// <param name="AssistantText">Testo del modello nello stesso messaggio delle chiamate; non va al chiamante.</param>
/// <param name="Calls">Chiamate proposte.</param>
/// <param name="Results">Risultati, nello stesso ordine.</param>
public sealed record ToolRound(string? AssistantText, IReadOnlyList<ToolCall> Calls, IReadOnlyList<ToolCallResult> Results);

/// <summary>Il modello chiede di chiamare strumenti invece di rispondere.</summary>
/// <param name="Calls">Chiamate proposte nello stesso messaggio.</param>
public sealed record AdapterToolCalls(IReadOnlyList<ToolCall> Calls) : AdapterEvent;

/// <summary>
/// Limiti del giro modello-strumento di un turno. Stanno nella configurazione e il profilo puo' solo
/// restringerli.
/// </summary>
/// <param name="MaxRounds">Giri modello-strumento per turno.</param>
/// <param name="MaxCallsPerRound">Chiamate in un giro.</param>
/// <param name="MaxCalls">Chiamate nel turno.</param>
/// <param name="CallTimeoutSeconds">Tempo di una chiamata.</param>
/// <param name="MaxResultBytes">Byte di un risultato.</param>
public sealed record ToolLimits(int MaxRounds, int MaxCallsPerRound, int MaxCalls, int CallTimeoutSeconds, int MaxResultBytes)
{
    /// <summary>Valori consegnati.</summary>
    public static ToolLimits Default { get; } = new(8, 8, 32, 30, 64 * 1024);

    /// <summary>Composizione restrittiva: per ogni limite vince il piu' stretto.</summary>
    public ToolLimits Restrict(ToolLimitsRestriction? restriction) =>
        restriction is null
            ? this
            : new ToolLimits(
                Math.Min(MaxRounds, restriction.MaxRounds ?? MaxRounds),
                Math.Min(MaxCallsPerRound, restriction.MaxCallsPerRound ?? MaxCallsPerRound),
                Math.Min(MaxCalls, restriction.MaxCalls ?? MaxCalls),
                Math.Min(CallTimeoutSeconds, restriction.CallTimeoutSeconds ?? CallTimeoutSeconds),
                Math.Min(MaxResultBytes, restriction.MaxResultBytes ?? MaxResultBytes));
}

/// <summary>Restrizione dei limiti scritta in un profilo; un campo assente non restringe.</summary>
public sealed record ToolLimitsRestriction(
    int? MaxRounds = null,
    int? MaxCallsPerRound = null,
    int? MaxCalls = null,
    int? CallTimeoutSeconds = null,
    int? MaxResultBytes = null);

/// <summary>
/// Dove va una chiamata e che cosa ne e' stato dichiarato. Collocazione e sorgente si osservano; classi
/// accettate e restituite e luoghi di elaborazione sono attestati, con la loro scadenza.
/// </summary>
/// <param name="SourceId">Sorgente dello strumento.</param>
/// <param name="ToolName">Nome dello strumento nella sorgente.</param>
/// <param name="Placement">Collocazione della sorgente.</param>
/// <param name="MaxArgumentClass">Classe massima che lo strumento accetta negli argomenti.</param>
/// <param name="ResultClass">Classe di cio' che lo strumento restituisce, come minimo.</param>
/// <param name="Regions">Luoghi di elaborazione attestati.</param>
/// <param name="EvidenceExpiresAt">Scadenza dell'attestazione dei luoghi.</param>
public sealed record ToolDestination(
    string SourceId,
    string ToolName,
    Placement Placement,
    DataClass MaxArgumentClass,
    DataClass ResultClass,
    IReadOnlySet<ProcessingRegion> Regions,
    DateTimeOffset? EvidenceExpiresAt);

/// <summary>Guasto di una chiamata che ferma il giro invece di tornare al modello.</summary>
public enum ToolInvocationFailure
{
    /// <summary>Nessun guasto: il contenuto torna al modello, anche se e' un errore dello strumento.</summary>
    None,

    /// <summary>Sorgente irraggiungibile, redirect o risposta illeggibile.</summary>
    SourceUnavailable,

    /// <summary>La sorgente ha rifiutato le credenziali consegnate dall'applicazione.</summary>
    CredentialsRejected,

    /// <summary>La chiamata non ha risposto entro il limite.</summary>
    Timeout,
}

/// <summary>Esito di una chiamata eseguita dal gateway, prima dell'esame.</summary>
/// <param name="Content">Contenuto restituito.</param>
/// <param name="IsError">Vero se lo strumento risponde con un errore che il modello puo' leggere.</param>
/// <param name="Failure">Guasto che ferma il giro.</param>
public sealed record ToolInvocation(string Content, bool IsError, ToolInvocationFailure Failure = ToolInvocationFailure.None)
{
    /// <summary>Nessun contenuto nella resa testuale.</summary>
    public override string ToString() => "ToolInvocation { Failure = " + Failure + " }";
}

/// <summary>
/// Strumenti gia' preparati per un turno: scoperti, attestati e con le credenziali del turno. Il coordinatore
/// non sa da dove vengono; sa solo chiedere dove va una chiamata e farla eseguire.
/// </summary>
public interface IToolGateway
{
    /// <summary>Strumenti offerti al modello.</summary>
    IReadOnlyList<ToolDefinition> Definitions { get; }

    /// <summary>Destinazione di uno strumento offerto; null se il nome non corrisponde a nulla.</summary>
    ToolDestination? DestinationOf(string toolName);

    /// <summary>
    /// Esegue una chiamata gia' autorizzata. Non lancia per un guasto della sorgente: lo dichiara in
    /// <see cref="ToolInvocation.Failure"/>. Rispetta l'annullamento.
    /// </summary>
    Task<ToolInvocation> InvokeAsync(ToolCall call, CancellationToken cancellationToken);
}

/// <summary>Strumenti di un turno con i limiti effettivi.</summary>
/// <param name="Gateway">Strumenti preparati.</param>
/// <param name="Limits">Limiti gia' composti con il profilo.</param>
public sealed record ToolSession(IToolGateway Gateway, ToolLimits Limits);

/// <summary>Tipo di sorgente degli strumenti (ADR-020).</summary>
public enum ToolSourceKind
{
    /// <summary>Server MCP raggiunto in HTTP (Streamable HTTP).</summary>
    [WireValue("mcp")]
    Mcp,

    /// <summary>API descritta da un documento OpenAPI 3: un'operazione, uno strumento.</summary>
    [WireValue("openapi")]
    OpenApi,

    /// <summary>Server MCP avviato come programma su questa macchina: solo dalla configurazione del router.</summary>
    [WireValue("mcp-stdio")]
    McpStdio,
}

/// <summary>Sorgente indicata da un'applicazione nella richiesta.</summary>
/// <param name="Id">Identificatore scelto dall'applicazione, unico nella richiesta.</param>
/// <param name="Kind">Tipo; un'applicazione non indica sorgenti stdio.</param>
/// <param name="Url">Indirizzo: https verificato o loopback numerico.</param>
public sealed record ToolSourceRequest(string Id, ToolSourceKind Kind, Uri Url);

/// <summary>
/// Credenziale di una sorgente per un solo turno (ADR-020). Il router non la conserva: vive in memoria per la
/// durata del turno, va solo alla sorgente indicata e non compare in log, audit, eventi o errori.
/// </summary>
/// <param name="SourceId">Sorgente a cui e' destinata.</param>
/// <param name="Header">Intestazione HTTP che la porta.</param>
/// <param name="Value">Valore; non compare nella resa testuale.</param>
public sealed record ToolCredential(string SourceId, string Header, string Value)
{
    /// <summary>Mai il valore nella resa testuale.</summary>
    public override string ToString() => "ToolCredential { Source = " + SourceId + ", Header = " + Header + " }";
}

/// <summary>
/// Dichiarazione dell'applicazione su uno strumento: che dati accetta e restituisce, dove li tratta, fino a
/// quando. Il router la registra come attestazione dell'applicazione, legata all'impronta dello strumento.
/// </summary>
public sealed record ToolDeclaration(
    string SourceId,
    string Tool,
    string? Fingerprint,
    DataClass? Accepts,
    DataClass? Returns,
    IReadOnlySet<ProcessingRegion> Regions,
    DateTimeOffset? ExpiresAt);

/// <summary>Strumenti chiesti da un'applicazione per un turno.</summary>
public sealed record ToolsRequest(
    IReadOnlyList<ToolSourceRequest> Sources,
    IReadOnlyList<ToolDeclaration> Declarations,
    IReadOnlyList<ToolCredential> Credentials)
{
    /// <summary>Solo conteggi nella resa testuale.</summary>
    public override string ToString() =>
        "ToolsRequest { Sources = " + Sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ", Declarations = " + Declarations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ", Credentials = " + Credentials.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " }";
}

/// <summary>
/// Riscontro su una sorgente o uno strumento non usabili: codice stabile, che cosa, quale campo, perche'. Mai
/// contenuti ne' credenziali.
/// </summary>
public sealed record ToolProblem(
    ErrorCode Code,
    string? SourceId = null,
    string? Tool = null,
    string? Field = null,
    string? Reason = null,
    string? Fingerprint = null);

/// <summary>Strumenti offerti al modello e strumenti esclusi, con il motivo.</summary>
public sealed record ToolOffer(IReadOnlyList<string> Offered, IReadOnlyList<ToolProblem> Excluded);

/// <summary>Gli strumenti della richiesta non si possono usare: il turno non parte e l'applicazione sa perche'.</summary>
public sealed class ToolPreparationException : Exception
{
    public ToolPreparationException(IReadOnlyList<ToolProblem> problems)
        : base("The tools of the request cannot be used.")
    {
        ArgumentNullException.ThrowIfNull(problems);

        if (problems.Count == 0)
        {
            throw new ArgumentException("At least one problem is required.", nameof(problems));
        }

        Problems = problems;
    }

    /// <summary>Codice del primo problema.</summary>
    public ErrorCode Code => Problems[0].Code;

    /// <summary>Tutti i problemi trovati.</summary>
    public IReadOnlyList<ToolProblem> Problems { get; }
}

/// <summary>Attestazione di un'applicazione registrata dal router, con la sua scadenza e l'impronta.</summary>
public sealed record StoredToolAttestation(
    string SourceKey,
    string Tool,
    string Fingerprint,
    DataClass Accepts,
    DataClass Returns,
    IReadOnlySet<ProcessingRegion> Regions,
    DateTimeOffset RecordedAt,
    DateTimeOffset ExpiresAt);
