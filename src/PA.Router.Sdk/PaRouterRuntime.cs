using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Core;
using PA.Router.Execution;
using PA.Router.Hosting.Adapters;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Content;
using PA.Router.Infrastructure.Crypto;
using PA.Router.Infrastructure.Storage;
using PA.Router.Policy;
using PA.Router.Policy.Grants;
using PA.Router.Privacy;
using PA.Router.Registry.Loading;
using PA.Router.Registry.Probing;

namespace PA.Router.Sdk;

/// <summary>Sessione creata, con il token restituito una volta sola.</summary>
/// <param name="SessionId">Identificatore.</param>
/// <param name="SessionToken">Capability segreta; il router ne conserva solo l'HMAC.</param>
/// <param name="ProfileId">Profilo fissato dalla configurazione.</param>
/// <param name="ContentMode">Modo di conservazione.</param>
public sealed record CreatedSession(string SessionId, string SessionToken, string ProfileId, ContentMode ContentMode);

/// <summary>Stato di un'esecuzione osservabile dal chiamante.</summary>
/// <param name="ExecutionId">Esecuzione.</param>
/// <param name="SessionId">Sessione proprietaria.</param>
/// <param name="State">Stato corrente.</param>
/// <param name="Output">Testo restituibile, solo dopo <c>completed</c>.</param>
/// <param name="ErrorCode">Codice errore stabile, quando presente.</param>
/// <param name="ContentAvailability">Disponibilita' del contenuto.</param>
/// <param name="Usage">Consumo osservato.</param>
/// <param name="MemoryStatus">Stato della memoria per questo turno.</param>
/// <param name="Tools">Strumenti offerti ed esclusi, quando la richiesta ne usa.</param>
/// <param name="ErrorDetail">Riscontro senza contenuti su un rifiuto, quando il codice non basta.</param>
/// <param name="ToolsUnused">Vero se la risposta completata offriva strumenti e non ne ha usato nessuno.</param>
/// <param name="WeakPerimeter">
/// Vero se ha risposto un servizio dentro il perimetro sotto la soglia di qualita', per regola del profilo (ADR-029).
/// </param>
/// <param name="BelowDemand">
/// Vero se nessun servizio ammesso bastava per il profilo di domanda e ha risposto il piu' capace (ADR-031).
/// </param>
public sealed record ExecutionSnapshot(
    string ExecutionId,
    string SessionId,
    ExecutionState State,
    string? Output,
    ErrorCode? ErrorCode,
    ContentAvailability ContentAvailability,
    UsageReport? Usage,
    MemoryStatus MemoryStatus,
    ToolOffer? Tools = null,
    IReadOnlyDictionary<string, string>? ErrorDetail = null,
    bool ToolsUnused = false,
    bool WeakPerimeter = false,
    bool BelowDemand = false);

/// <summary>Stato della memoria dichiarato nelle risposte.</summary>
public enum MemoryStatus
{
    [WireValue("ready")]
    Ready,

    [WireValue("pending")]
    Pending,

    [WireValue("unavailable")]
    Unavailable,

    [WireValue("disabled")]
    Disabled,
}

/// <summary>Evento di stato, senza testo.</summary>
/// <param name="ExecutionId">Esecuzione.</param>
/// <param name="State">Stato raggiunto.</param>
/// <param name="Sequence">Numero d'ordine, da 1.</param>
/// <param name="Timestamp">Istante.</param>
public sealed record ExecutionEvent(string ExecutionId, ExecutionState State, int Sequence, DateTimeOffset Timestamp);

/// <summary>
/// Facade del runtime per le applicazioni (03-specs/HEADLESS-API-SDK.md).
///
/// «API» qui significa l'interfaccia che PA Router espone a un'applicazione locale, non una
/// chiamata a un provider: il backend chiama questo runtime, e se un profilo seleziona una CLI
/// ufficiale e' il router ad avviarne il programma.
///
/// Il canale e' fissato alla costruzione: una fabbrica chatbot non puo' produrre un runtime
/// personale, e nessun parametro di chiamata cambia profilo, target o capability. Il DLL non e' un
/// confine contro codice ostile nello stesso processo, e questa classe non finge di esserlo: e' il
/// confine fra un backend e la pipeline, non fra due livelli di fiducia dentro un processo.
/// </summary>
public sealed class PaRouterRuntime : IPaRouterRuntime, IAsyncDisposable
{
    private readonly RouterStack stack;
    private readonly bool ownsStack;
    private readonly SqliteConnectionFactory factory;
    private readonly IMetadataDigest digest;
    private readonly SqliteExecutionStore store;
    private readonly SqliteSessionService sessions;
    private readonly Channel channel;
    private readonly string profileId;
    private readonly ConcurrentDictionary<string, RunningExecution> executions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> sessionTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> idempotency = new(StringComparer.Ordinal);

    /// <summary>Esecuzioni terminate tenute in memoria: la stessa finestra degli eventi.</summary>
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// Separatore delle parti di una chiave composta. E' un carattere che non compare in un
    /// identificatore ne' in un digest: concatenare senza separatore renderebbe due chiavi diverse
    /// indistinguibili.
    /// </summary>
    private const string Separator = "|";

    /// <summary>Stesso separatore, per le operazioni su carattere singolo.</summary>
    private const char SeparatorChar = '|';

    private PaRouterRuntime(
        RouterStack stack,
        bool ownsStack,
        IMetadataDigest digest,
        SqliteExecutionStore store,
        SqliteSessionService sessions,
        Channel channel,
        string profileId)
    {
        this.stack = stack;
        this.ownsStack = ownsStack;
        factory = stack.ConnectionFactory;
        this.digest = digest;
        this.store = store;
        this.sessions = sessions;
        this.channel = channel;
        this.profileId = profileId;
    }

    /// <summary>
    /// Configurazione in uso. Non si tiene una copia: il turno rilegge i file, e una copia presa alla
    /// costruzione resterebbe quella dell'avvio.
    /// </summary>
    private ConfigurationSnapshot Current => stack.Snapshot;

    /// <summary>
    /// Runtime per un backend chatbot: profilo di servizio e canale fissati dalla configurazione.
    /// Il backend non puo' chiedere un profilo diverso, ne' capability diverse da <c>chat</c>.
    /// </summary>
    public static PaRouterRuntime CreateChatbotRuntime(ConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return Open(snapshot, Channel.Chatbot, snapshot.Config.Api.ServiceProfileId);
    }

    /// <summary>
    /// Runtime per il terminale fidato. Non e' raggiungibile da un backend: la fabbrica e' distinta
    /// proprio per non lasciare che un chiamante chatbot costruisca un canale personale.
    /// </summary>
    public static PaRouterRuntime CreatePersonalRuntime(ConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return Open(snapshot, Channel.PersonalCli, snapshot.Config.DefaultProfileId);
    }

    /// <inheritdoc />
    public async ValueTask<CreatedSession> CreateSessionAsync(CancellationToken cancellationToken)
    {
        string sessionId = Guid.NewGuid().ToString("D", System.Globalization.CultureInfo.InvariantCulture);

        // Il token e' una capability segreta di 256 bit, restituita una volta sola: il router ne
        // conserva solo l'HMAC, e un confronto fallito e' indistinguibile da una sessione assente.
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        string tokenHmac = digest.Compute(Encoding.UTF8.GetBytes(token));

        await store.EnsureProjectAsync(Current.Config.Api.ProjectId, "local", cancellationToken).ConfigureAwait(false);

        await store.CreateSessionAsync(
            new SessionRecord(
                sessionId,
                profileId,
                channel,
                Current.Config.ContentMode,
                Current.Config.Api.ProjectId,
                tokenHmac,
                SystemClock.Instance.UtcNow.AddHours(12).ToUnixTimeMilliseconds()),
            cancellationToken).ConfigureAwait(false);

        sessionTokens[sessionId] = tokenHmac;

        return new CreatedSession(sessionId, token, profileId, Current.Config.ContentMode);
    }

    /// <inheritdoc />
    public bool Authorize(string sessionId, string sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(sessionToken))
        {
            return false;
        }

        if (!sessionTokens.TryGetValue(sessionId, out string? expected))
        {
            // **La riga nello store c'e' gia': mancava la lettura** (difetto trovato dall'audit, S74).
            //
            // L'HMAC viene scritto in `sessions.token_hmac` alla creazione **e** tenuto qui in un
            // dizionario del processo, ma l'autorizzazione consultava soltanto il dizionario, che
            // nessuno reidrata: dopo un riavvio ogni token emesso riceveva 404, cioe' lo stesso
            // esito di un token sbagliato. Un backend non poteva distinguere la sessione scomparsa
            // dal proprio token non valido.
            //
            // Il confronto resta possibile perche' l'HMAC si calcola con la chiave del `KeyRing`,
            // che e' persistita: la stessa chiave dopo il riavvio produce lo stesso digest.
            if (ReadSessionSecret(sessionId) is not (string stored, long expiresAtMs))
            {
                return false;
            }

            // La scadenza veniva scritta a dodici ore e non era mai confrontata con nulla: una
            // capability con una durata dichiarata e non applicata dura quanto il processo.
            if (expiresAtMs <= SystemClock.Instance.UtcNow.ToUnixTimeMilliseconds())
            {
                return false;
            }

            expected = stored;

            // Riletta una volta, poi vale la memoria: le richieste successive non tornano sul disco.
            sessionTokens[sessionId] = stored;
        }

        // Confronto a tempo costante: un controllo di autorizzazione non deve introdurre un canale
        // laterale, per quanto stretto.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(digest.Compute(Encoding.UTF8.GetBytes(sessionToken))));
    }

    /// <summary>
    /// HMAC del token e scadenza di una sessione, letti dallo store.
    ///
    /// Lettura diretta come <see cref="EventRange"/>: l'host API interroga gia' lo store per gli
    /// eventi, e il contratto <c>IExecutionStore</c> non espone letture. Una sessione chiusa non
    /// autorizza piu' nulla, quindi la riga viene cercata fra quelle attive.
    /// </summary>
    private (string TokenHmac, long ExpiresAtMs)? ReadSessionSecret(string sessionId)
    {
        using Microsoft.Data.Sqlite.SqliteConnection connection = factory.Open();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT token_hmac, expires_at_ms FROM sessions WHERE id = $id AND state = 'active';";

        command.Parameters.AddWithValue("$id", sessionId);

        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetInt64(1));
    }

    /// <inheritdoc />
    public async ValueTask<ExecutionSnapshot> ExecuteAsync(
        string sessionId,
        ApplicationRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Input);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(request.ContextItems);

        string input = request.Input;
        IReadOnlyList<ContextItem> contextItems = request.ContextItems;

        string requestDigest = digest.Compute(RequestFingerprint(sessionId, request));

        string scopedKey = sessionId + Separator + idempotencyKey;

        // La chiave si verifica prima di tutto il resto, busy compreso: un backend che ritenta non
        // deve consumare uno slot ne' produrre un secondo dispatch.
        if (idempotency.TryGetValue(scopedKey, out string? existing))
        {
            string[] parts = existing.Split(SeparatorChar);

            if (!string.Equals(parts[1], requestDigest, StringComparison.Ordinal))
            {
                throw new PaRouterException(ErrorCode.InputInvalid, "The idempotency key was used with a different request.")
                    .WithMetadata("reason", "idempotency_conflict");
            }

            return Snapshot(parts[0]);
        }

        // **La chiave e' nello store: mancava la lettura** (difetto trovato dall'audit, S74).
        //
        // La riga in `idempotency_keys` si scrive nella stessa transazione dell'accettazione, e il
        // commento di quella scrittura dice proprio «senza, una ripetizione potrebbe creare una
        // seconda esecuzione». Ma nessuno la rileggeva: in tutto il prodotto quella tabella aveva
        // una sola INSERT e una sola DELETE, e la deduplica viveva solo nel dizionario di processo.
        // Dopo un riavvio la stessa chiave creava un **secondo dispatch** verso il fornitore, che e'
        // esattamente il caso per cui l'idempotenza esiste.
        if (ReadIdempotentExecution(sessionId, idempotencyKey) is (string knownId, string knownDigest))
        {
            if (!string.Equals(knownDigest, requestDigest, StringComparison.Ordinal))
            {
                throw new PaRouterException(ErrorCode.InputInvalid, "The idempotency key was used with a different request.")
                    .WithMetadata("reason", "idempotency_conflict");
            }

            idempotency[scopedKey] = knownId + Separator + knownDigest;

            return Restored(knownId);
        }

        Evict();

        // L'ammissione si prende adesso, non dentro il turno: un backend che chiede due turni della stessa sessione, o
        // che riempie gli slot del processo, riceve 429 subito invece di un 202 che fallira' da solo.
        if (!stack.Scheduler.TryAcquire(new ScheduleRequest(sessionId, null), out SchedulerLease? admission, out ErrorCode busy))
        {
            throw new PaRouterException(busy, "The runtime cannot accept another turn now.")
                .WithMetadata("reason", busy.ToWire());
        }

        PreparedTools? tools;

        try
        {
            // Gli strumenti si preparano prima di accettare la richiesta (ADR-020): una sorgente irraggiungibile, uno
            // strumento non dichiarato o una credenziale mancante tornano all'applicazione nella risposta, e la chiave
            // di idempotenza non resta legata a una richiesta mai accettata.
            tools = await ToolPreparation
                .PrepareAsync(stack, profileId, request.Tools, cancellationToken, channel)
                .ConfigureAwait(false);
        }
        catch
        {
            admission!.Dispose();
            throw;
        }

        string executionId = Guid.NewGuid().ToString("D", System.Globalization.CultureInfo.InvariantCulture);

        if (!idempotency.TryAdd(scopedKey, executionId + Separator + requestDigest))
        {
            admission!.Dispose();

            if (tools is not null)
            {
                await tools.DisposeAsync().ConfigureAwait(false);
            }

            // Due richieste con la stessa chiave nello stesso istante: vince chi ha scritto, e l'altra vale come una
            // ripetizione solo se e' davvero la stessa richiesta.
            string[] winner = idempotency[scopedKey].Split(SeparatorChar);

            if (!string.Equals(winner[1], requestDigest, StringComparison.Ordinal))
            {
                throw new PaRouterException(ErrorCode.InputInvalid, "The idempotency key was used with a different request.")
                    .WithMetadata("reason", "idempotency_conflict");
            }

            return Snapshot(winner[0]);
        }

        RunningExecution running = new(executionId, sessionId) { Offer = tools?.Offer };

        executions[executionId] = running;

        // Il turno rilegge la configurazione e il profilo per identificatore: qui basta sapere che
        // il profilo esiste nella configurazione in uso.
        PolicyDocument profile = TurnInputs.ResolveProfile(Current, profileId);

        // Il turno gira in background: la risposta 202 arriva subito con l'identificatore, e lo
        // stato si legge dagli eventi.
        running.Task = Task.Run(
            async () =>
            {
                try
                {
                    // Lo stesso turno della CLI e del worker: memoria, cronologia, analisi della
                    // richiesta, privacy, policy, ranking, dispatch e screening. L'host API non ne
                    // tiene una copia propria, altrimenti una richiesta che arriva da un backend
                    // verrebbe trattata diversamente dalla stessa richiesta scritta al terminale.
                    TurnOutcome outcome = await TurnRunner.RunAsync(
                        stack,
                        new TurnRequest(
                            profile,
                            input,
                            sessionId,
                            ContextItems: contextItems,
                            IdempotencyKey: idempotencyKey,
                            // Il digest viaggia con la chiave fino alla riga di idempotenza: senza,
                            // lo store conserverebbe l'HMAC del solo input e la ripetizione dopo un
                            // riavvio verrebbe giudicata con una regola piu' debole di questa (S74).
                            RequestDigest: requestDigest,
                            ExecutionId: executionId,
                            Instructions: request.Instructions,
                            Tools: tools?.Session)
                        {
                            // Il canale e' quello del runtime (ADR-032, fase 2): l'host API e' sempre chatbot, e il turno
                            // nega capacita', recinto e conferme su quel canale (INV-23).
                            Channel = channel,
                        },
                        running.Cancellation.Token,
                        admission).ConfigureAwait(false);

                    running.Complete(outcome.Result!);
                }
                catch (OperationCanceledException)
                {
                    running.Complete(new ExecutionResult(
                        executionId, ExecutionState.Cancelled, null, ErrorCode.Cancelled,
                        SideEffectState.None, [], null, 0));
                }
                catch (PaRouterException ex)
                {
                    running.Complete(new ExecutionResult(
                        executionId, ExecutionState.Failed, null, ex.Code,
                        SideEffectState.None, [], null, 0));
                }
                catch (Exception)
                {
                    // Un difetto senza codice: non si sa se il servizio ha lavorato. L'esecuzione lo dichiara e finisce,
                    // invece di restare accettata per sempre, tenere la sessione e non chiudere mai lo stream.
                    running.Complete(new ExecutionResult(
                        executionId, ExecutionState.Uncertain, null, ErrorCode.SideEffectUncertain,
                        SideEffectState.Possible, [], null, 0));
                }
                finally
                {
                    admission!.Dispose();

                    if (tools is not null)
                    {
                        try
                        {
                            // Le sorgenti si chiudono con il turno: credenziali e processi non sopravvivono.
                            await tools.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // La chiusura di una sorgente non cambia l'esito del turno.
                        }
                    }
                }
            },
            CancellationToken.None);

        return Snapshot(executionId);
    }

    /// <summary>
    /// Forma canonica di una richiesta per l'idempotenza: un array JSON, quindi senza ambiguita' fra i
    /// campi. Stessa chiave con istruzioni diverse e' una richiesta diversa, e diventa un conflitto.
    /// </summary>
    private static byte[] RequestFingerprint(string sessionId, ApplicationRequest request) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            sessionId,
            request.Input,
            request.Instructions,
            request.ContextItems.Select(item => new object?[]
            {
                item.Id, item.Text, item.Provenance.ToWire(), item.DeclaredClass?.ToWire(),
            }).ToArray(),
            ToolsFingerprint(request.Tools),
        });

    /// <summary>
    /// Strumenti nella forma canonica della richiesta. Le credenziali contano per sorgente e intestazione, mai per
    /// valore: una credenziale rinnovata non rende diversa la stessa richiesta, e il valore non entra in un digest.
    /// </summary>
    private static object? ToolsFingerprint(ToolsRequest? tools) =>
        tools is null
            ? null
            : new object?[]
            {
                tools.Sources.Select(source => new object?[] { source.Id, source.Kind.ToWire(), source.Url.AbsoluteUri }).ToArray(),
                tools.Declarations.Select(declaration => new object?[]
                {
                    declaration.SourceId,
                    declaration.Tool,
                    declaration.Fingerprint,
                    declaration.Accepts?.ToWire(),
                    declaration.Returns?.ToWire(),
                    declaration.Regions.Select(region => region.Code).Order(StringComparer.Ordinal).ToArray(),
                    declaration.ExpiresAt?.ToUnixTimeMilliseconds(),
                }).ToArray(),
                tools.Credentials.Select(credential => new object?[] { credential.SourceId, credential.Header }).ToArray(),
            };

    /// <inheritdoc />
    public ExecutionSnapshot GetExecution(string executionId) => Snapshot(executionId);

    /// <inheritdoc />
    public async IAsyncEnumerable<ExecutionEvent> ObserveAsync(
        string executionId,
        int afterSequence,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        int last = afterSequence;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Il risultato del turno arriva dopo il tentativo di scrivere l'evento terminale:
            // leggerlo PRIMA degli eventi chiude la race con l'ultima lettura. Lo stato SQL da
            // solo non basta per un turno vivo (T3 precede l'append dell'evento).
            // Dopo il riavvio nessun produttore aggiunge eventi a un'esecuzione gia' terminale.
            bool terminal = executions.TryGetValue(executionId, out RunningExecution? running)
                ? running.IsTerminal
                : IsTerminal(Restored(executionId).State);
            foreach (ExecutionEvent stored in ReadEvents(executionId, last))
            {
                last = stored.Sequence;

                yield return stored;
                if (IsTerminal(stored.State)) { yield break; }
            }

            if (terminal)
            {
                // Lo stream si chiude sul terminale: una riconnessione rilegge gli eventi, non
                // riesegue nulla.
                yield break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsTerminal(ExecutionState state) => state is
        ExecutionState.Completed or ExecutionState.Denied or ExecutionState.Failed
        or ExecutionState.Cancelled or ExecutionState.Uncertain;

    /// <inheritdoc />
    public void Cancel(string executionId)
    {
        if (executions.TryGetValue(executionId, out RunningExecution? running))
        {
            // Idempotente anche su un'esecuzione terminale: la richiesta viene accettata e non
            // cambia nulla.
            running.Cancellation.Cancel();
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (executions.Values.Any(execution =>
                string.Equals(execution.SessionId, sessionId, StringComparison.Ordinal) && !execution.IsTerminal))
        {
            return false;
        }

        _ = await SessionDeletion.DeleteAsync(stack, sessions, sessionId, cancellationToken).ConfigureAwait(false);

        sessionTokens.TryRemove(sessionId, out _);

        // Con la sessione se ne vanno le sue esecuzioni e le sue chiavi: quello che l'operatore ha cancellato non resta
        // nella memoria del processo.
        foreach (RunningExecution running in executions.Values.ToArray())
        {
            if (string.Equals(running.SessionId, sessionId, StringComparison.Ordinal))
            {
                Forget(running.ExecutionId);
            }
        }

        return true;
    }

    /// <summary>Stato della memoria per la sessione corrente.</summary>
    public MemoryStatus Memory =>
        !Current.Config.Memory.Enabled ? MemoryStatus.Disabled
        : Current.Config.Memory.IsUnconfigured ? MemoryStatus.Unavailable
        : MemoryStatus.Ready;

    /// <summary>
    /// Toglie dalla memoria del processo le esecuzioni terminate da piu' di un giorno, con le loro chiavi: gli eventi
    /// hanno la stessa finestra, e un host che vive a lungo non deve crescere per sempre.
    /// </summary>
    private void Evict()
    {
        DateTimeOffset limit = SystemClock.Instance.UtcNow - CompletedRetention;

        foreach (RunningExecution running in executions.Values.ToArray())
        {
            if (running.CompletedAt is DateTimeOffset completed && completed < limit)
            {
                Forget(running.ExecutionId);
            }
        }
    }

    private void Forget(string executionId)
    {
        executions.TryRemove(executionId, out _);

        foreach ((string key, string value) in idempotency)
        {
            if (string.Equals(value.Split(SeparatorChar)[0], executionId, StringComparison.Ordinal))
            {
                idempotency.TryRemove(key, out _);
            }
        }
    }

    private ExecutionSnapshot Snapshot(string executionId)
    {
        if (!executions.TryGetValue(executionId, out RunningExecution? running))
        {
            return Restored(executionId);
        }

        return new ExecutionSnapshot(
            executionId,
            running.SessionId,
            running.State,
            running.Result?.Output,
            running.Result?.ErrorCode,
            // In memory_only il contenuto vive nel processo: dopo un riavvio non esiste, e va
            // dichiarato invece di restituire una risposta vuota che sembra completa.
            running.Result?.Output is not null ? ContentAvailability.Available : ContentAvailability.Unavailable,
            running.Result?.Usage,
            Memory,
            running.Offer,
            running.Result?.Detail,
            running.Result?.ToolsUnused ?? false,
            running.Result?.WeakPerimeter ?? false,
            running.Result?.BelowDemand ?? false);
    }

    /// <summary>
    /// Esecuzione gia' registrata per una chiave di idempotenza, letta dallo store.
    ///
    /// La chiave viaggia come HMAC, come al momento della scrittura: la tabella non conserva il
    /// valore che il backend ha scelto.
    /// </summary>
    private (string ExecutionId, string RequestHmac)? ReadIdempotentExecution(string sessionId, string idempotencyKey)
    {
        using Microsoft.Data.Sqlite.SqliteConnection connection = factory.Open();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT execution_id, request_hmac FROM idempotency_keys "
            + "WHERE session_id = $session AND key_hmac = $key;";

        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$key", digest.Compute(Encoding.UTF8.GetBytes(idempotencyKey)));

        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();

        return reader.Read()
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    /// <summary>
    /// Esito di un'esecuzione che questo processo non ha eseguito, ricostruito dallo store.
    ///
    /// Dopo un riavvio l'esecuzione non e' piu' nel registro in memoria, quindi
    /// <see cref="Snapshot"/> direbbe che non esiste. Qui si restituisce cio' che lo store sa —
    /// stato e codice — e si **dichiara** che il contenuto non e' disponibile: il testo vive nei
    /// blob della sessione, e prometterlo qui significherebbe restituire una risposta vuota che
    /// sembra completa. E' la stessa distinzione che il prodotto fa gia' in `memory_only`.
    /// </summary>
    private ExecutionSnapshot Restored(string executionId)
    {
        using Microsoft.Data.Sqlite.SqliteConnection connection = factory.Open();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT state, error_code, session_id FROM executions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", executionId);

        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read())
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "That execution does not exist.")
                .WithMetadata("reason", "execution_not_found");
        }

        ErrorCode? code = reader.IsDBNull(1) || !ErrorCodes.TryParseWire(reader.GetString(1), out ErrorCode parsed)
            ? null
            : parsed;

        return new ExecutionSnapshot(
            executionId,
            reader.GetString(2),
            WireEnum.Parse<ExecutionState>(reader.GetString(0)),
            null,
            code,
            ContentAvailability.Unavailable,
            null,
            Memory);
    }

    /// <inheritdoc />
    public (int First, int Last)? EventRange(string executionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        using Microsoft.Data.Sqlite.SqliteConnection connection = factory.Open();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT MIN(sequence), MAX(sequence) FROM execution_events WHERE execution_id = $id;";

        command.Parameters.AddWithValue("$id", executionId);

        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }

        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private IReadOnlyList<ExecutionEvent> ReadEvents(string executionId, int afterSequence)
    {
        using Microsoft.Data.Sqlite.SqliteConnection connection = factory.Open();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT sequence, state, created_at_ms FROM execution_events "
            + "WHERE execution_id = $id AND sequence > $after ORDER BY sequence;";

        command.Parameters.AddWithValue("$id", executionId);
        command.Parameters.AddWithValue("$after", afterSequence);

        List<ExecutionEvent> events = [];

        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            events.Add(new ExecutionEvent(
                executionId,
                WireEnum.Parse<ExecutionState>(reader.GetString(1)),
                reader.GetInt32(0),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
        }

        return events;
    }

    /// <summary>
    /// Runtime sopra una composizione gia' aperta. E' il percorso dell'host API quando convive con
    /// il worker nello stesso processo: lo store ha un proprietario solo, e non e' questo runtime.
    /// </summary>
    public static PaRouterRuntime Over(RouterStack stack, Channel channel, string profileId)
    {
        ArgumentNullException.ThrowIfNull(stack);

        return Build(stack, ownsStack: false, channel, profileId);
    }

    private static PaRouterRuntime Open(ConfigurationSnapshot snapshot, Channel channel, string profileId)
    {
        RouterStack stack = RouterStack.Open(snapshot);

        try
        {
            return Build(stack, ownsStack: true, channel, profileId);
        }
        catch
        {
            stack.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private static PaRouterRuntime Build(RouterStack stack, bool ownsStack, Channel channel, string profileId)
    {
        // Senza protector non c'e' un posto dove conservare l'HMAC di un token di sessione: l'host
        // API non parte, invece di conservarlo in chiaro.
        IMetadataDigest digest = stack.Keys ?? throw new PaRouterException(
            ErrorCode.CryptoUnavailable,
            "The API host needs a key protector to store session tokens.");

        return new PaRouterRuntime(
            stack,
            ownsStack,
            digest,
            stack.Store,
            stack.Sessions(),
            channel,
            profileId);
    }

    private static KeyRing? TryOpenKeys(StorePaths paths)
    {
        try
        {
            return KeyRing.OpenOrCreate(paths, PlatformKeyProtector.Create());
        }
        catch (PaRouterException ex) when (ex.Code == ErrorCode.CryptoUnavailable)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        List<Task> pending = [];

        foreach (RunningExecution running in executions.Values)
        {
            running.Cancellation.Cancel();

            if (running.Task is Task task)
            {
                pending.Add(task);
            }
        }

        try
        {
            // I turni scrivono nello store: si aspetta che si chiudano prima di chiudere la composizione sotto di loro.
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Un turno che non si chiude in tempo non impedisce la chiusura: l'alternativa e' restare appesi.
        }

        if (ownsStack)
        {
            await stack.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Esecuzione in corso, con il proprio annullamento e l'esito quando arriva.</summary>
    private sealed class RunningExecution(string executionId, string sessionId)
    {
        public string ExecutionId { get; } = executionId;

        public string SessionId { get; } = sessionId;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task? Task { get; set; }

        public ToolOffer? Offer { get; init; }

        public ExecutionResult? Result { get; private set; }

        /// <summary>Istante in cui l'esecuzione e' diventata terminale, per lo sfratto dalla memoria del processo.</summary>
        public DateTimeOffset? CompletedAt { get; private set; }

        public ExecutionState State => Result?.State ?? ExecutionState.Accepted;

        public bool IsTerminal => Result is not null;

        public void Complete(ExecutionResult result)
        {
            Result = result;
            CompletedAt = SystemClock.Instance.UtcNow;
        }
    }
}
