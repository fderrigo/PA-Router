using System.Globalization;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Infrastructure.Storage;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Le conversazioni come cose che si scelgono: elenco, nome, archivio, biforca, trascritto (S62).
///
/// E' un'operazione, non un comando. `pa session ...` e i comandi del REPL sono due **rese** della
/// stessa (INV-22): un selettore che leggesse le sessioni per conto proprio finirebbe per mostrare
/// un elenco diverso da quello di `pa session list`, e la differenza si vedrebbe solo in campo.
///
/// Il confine con la cancellazione resta netto. <see cref="ArchiveAsync"/> toglie dalla vista e non
/// rimuove niente; `pa session delete` rimuove contenuti e revoca la capability. Chi archivia non
/// sta chiedendo di perdere il lavoro, e una sola parola per le due cose sarebbe una trappola.
/// </summary>
public static class SessionCatalog
{
    /// <summary>Quante sessioni mostrare quando nessuno lo dice.</summary>
    public const int DefaultLimit = 50;

    /// <summary>
    /// Sessioni dalla piu' recente, per chi deve sceglierne una.
    ///
    /// Le archiviate restano fuori se non si chiedono: e' il senso dell'archivio.
    /// </summary>
    public static IReadOnlyList<SessionSummary> List(
        RouterStack stack,
        bool includeArchived = false,
        int limit = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(stack);

        // Anche una lettura parte dalla configurazione che c'e' adesso, come ogni altra operazione.
        stack.EnsureCurrent();

        return stack.Sessions().Summaries(includeArchived, limit <= 0 ? DefaultLimit : limit);
    }

    /// <summary>
    /// La sessione piu' recente non archiviata, per «continua l'ultima».
    ///
    /// Null quando non ce n'e' nessuna: una conversazione da riprendere o esiste o no, e inventarne
    /// una vuota per avere qualcosa da restituire sarebbe una ripresa che non riprende nulla.
    /// </summary>
    public static SessionSummary? Latest(RouterStack stack) => List(stack, includeArchived: false, limit: 1).FirstOrDefault();

    /// <summary>
    /// Da' un nome alla sessione, oppure lo toglie con null.
    ///
    /// Il nome e' dell'operatore: non viene dedotto dalla prima domanda, che e' contenuto della
    /// conversazione e finirebbe in un elenco leggibile a colpo d'occhio da chiunque guardi lo
    /// schermo. Un nome gia' preso viene rifiutato, cosi' riprendere per nome sceglie una sola
    /// conversazione.
    /// </summary>
    public static ValueTask NameAsync(
        RouterStack stack,
        string sessionId,
        string? name,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);

        stack.EnsureCurrent();

        return stack.Sessions().NameAsync(sessionId, name, cancellationToken);
    }

    /// <summary>
    /// Archivia la sessione, o la riporta in vista.
    ///
    /// Non cancella nulla: turni, contenuti ed esecuzioni restano dove sono, e la sessione resta
    /// riprendibile per identificatore. Toglie soltanto dall'elenco di cio' che si propone.
    /// </summary>
    public static ValueTask ArchiveAsync(
        RouterStack stack,
        string sessionId,
        bool archived,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);

        stack.EnsureCurrent();

        return stack.Sessions().ArchiveAsync(sessionId, archived, cancellationToken);
    }

    /// <summary>
    /// Turni conservati di una sessione, in ordine.
    ///
    /// E' **contenuto dell'operatore**, non un riassunto: nessun modello viene interrogato per
    /// produrlo, e leggerlo non costa una quota.
    /// </summary>
    public static async ValueTask<IReadOnlyList<SessionTurn>> TranscriptAsync(
        RouterStack stack,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);

        stack.EnsureCurrent();

        SqliteSessionService sessions = stack.Sessions();

        if (await sessions.ReadAsync(sessionId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "That session does not exist.")
                .WithMetadata("reason", "session_unknown");
        }

        return await sessions.ReadTurnsAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Biforca una conversazione: una sessione nuova che parte dalla cronologia di quella indicata.
    ///
    /// Serve a provare una direzione diversa senza perdere quella di prima — l'originale non viene
    /// toccato. Non e' un handover: resta tutto qui, non c'e' nessuna capsule e nessun altro
    /// servizio, e i turni copiati sono gli stessi contenuti gia' conservati da questo router.
    ///
    /// Due rifiuti espliciti invece di una biforca che sembra riuscita:
    /// senza conservazione dei contenuti non esiste cronologia da biforcare, e una sessione di
    /// servizio non si biforca perche' la sua capability appartiene al backend che l'ha creata —
    /// una copia senza token non sarebbe utilizzabile da nessuno.
    /// </summary>
    /// <returns>Identificatore della sessione nuova.</returns>
    public static async Task<string> ForkAsync(
        RouterStack stack,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);

        stack.EnsureCurrent();

        SqliteSessionService sessions = stack.Sessions();

        SessionSummary source = await sessions.ReadAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new PaRouterException(ErrorCode.InputInvalid, "That session does not exist.")
                .WithMetadata("reason", "session_unknown");

        if (source.Channel is not Channel.PersonalCli)
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "Only personal sessions can be forked.")
                .WithMetadata("reason", "channel_not_personal");
        }

        if (!sessions.PersistsHistory)
        {
            throw new PaRouterException(
                ErrorCode.ContextUnavailable,
                "Contents are not persisted: there is no history to fork.")
                .WithMetadata("reason", "history_not_persisted");
        }

        IReadOnlyList<SessionTurn> turns = await sessions
            .ReadTurnsAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        string forkId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);

        // La riga della sessione la crea chi le crea sempre: duplicare qui quell'INSERT sarebbe una
        // seconda definizione di «sessione», e le due divergerebbero al primo campo aggiunto.
        await stack.Store.CreateSessionAsync(
            new SessionRecord(
                forkId,
                source.ProfileId,
                source.Channel,
                source.ContentMode,
                source.ProjectId,
                // Nessun token: una sessione personale non ne ha, e inventarne uno qui
                // significherebbe creare una capability che nessuno ha chiesto.
                null,
                // La copia non vive piu' a lungo dell'originale: i turni sono gli stessi contenuti,
                // e una scadenza piu' lontana li conserverebbe oltre cio' che era stato deciso.
                source.ExpiresAt.ToUnixTimeMilliseconds()),
            cancellationToken).ConfigureAwait(false);

        foreach (SessionTurn turn in turns)
        {
            await sessions
                .AppendTurnAsync(forkId, turn, source.ExpiresAt.ToUnixTimeMilliseconds(), cancellationToken)
                .ConfigureAwait(false);
        }

        return forkId;
    }
}
