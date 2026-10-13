using System.Globalization;
using PA.Router.Registry.Loading;
using PA.Router.Runtime.Ipc;

namespace PA.Router.Cli.Repl;

/// <summary>Esito della scelta di una sessione.</summary>
/// <param name="SessionId">Sessione scelta, quando la risposta ne indica una sola.</param>
/// <param name="Problem">Perche' la risposta non sceglie nulla, quando non sceglie.</param>
internal sealed record SessionChoice(string? SessionId, string? Problem)
{
    /// <summary>Nessuna scelta: l'operatore ha lasciato la riga vuota.</summary>
    internal static SessionChoice Cancelled { get; } = new(null, null);
}

/// <summary>
/// Il selettore delle sessioni (S62): come si mostrano e come si sceglie.
///
/// La **regola** di scelta sta qui una volta sola. Il REPL con `/resume` e `pa session resume`
/// senza identificatore la usano entrambi: se ciascuno interpretasse la risposta a modo suo, lo
/// stesso «2» sceglierebbe due conversazioni diverse a seconda di dove e' stato scritto, e il gate
/// di questo step chiede esattamente il contrario — riprendere dal selettore deve dare lo stesso
/// risultato di riprendere per identificatore.
///
/// L'ingresso e l'uscita restano di chi le possiede: il REPL ha la sua resa, la CLI la sua. Qui non
/// si stampa e non si legge nulla.
/// </summary>
internal static class SessionPicker
{
    /// <summary>Caratteri minimi di un identificatore abbreviato.</summary>
    private const int MinimumPrefix = 4;

    /// <summary>
    /// Tabella del selettore: intestazioni e righe insieme, perche' le colonne dipendono dalle righe.
    ///
    /// Etichetta e stato compaiono solo quando distinguono qualcosa (24 settembre 2026): su ottanta colonne, con la stessa
    /// etichetta su ogni riga, il nome della conversazione — la cosa per cui si legge l'elenco — usciva troncato. Le
    /// intestazioni si calcolano a ogni chiamata, nella lingua di adesso.
    /// </summary>
    internal static (IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) Table(IReadOnlyList<SessionPayload> sessions, ConfigurationSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        bool labels = sessions.Select(session => session.ProfileId).Distinct(StringComparer.Ordinal).Count() > 1;
        bool archived = sessions.Any(session => session.Archived);

        List<string> headers = ["n", Ui.Text("nome"), "id"];
        if (labels) { headers.Add(Ui.Text("session.col.label")); }
        headers.AddRange([Ui.Text("turni"), Ui.Text("creata")]);
        if (archived) { headers.Add(Ui.Text("stato")); }

        // Il nome e' testo dell'operatore e la resa lo sanifica come ogni altra cella: la superficie di S57 e' l'unico
        // punto che decide come si stampa una stringa.
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            .. sessions.Select((session, index) =>
            {
                List<string> row = [(index + 1).ToString(CultureInfo.InvariantCulture), session.DisplayName ?? "—", Short(session.Id)];
                if (labels) { row.Add(ProfilePresentation.Name(snapshot, session.ProfileId)); }
                row.Add(session.Turns.ToString(CultureInfo.InvariantCulture));
                row.Add(session.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                if (archived) { row.Add(session.Archived ? Ui.Text("archiviata") : Ui.Text("attiva")); }
                return (IReadOnlyList<string>)row;
            })
        ];

        return (headers, rows);
    }

    /// <summary>
    /// Traduce la risposta dell'operatore in una sessione.
    ///
    /// Quattro forme, in quest'ordine: il numero di riga, l'identificatore intero, un suo prefisso
    /// di almeno quattro caratteri, il nome. Un prefisso o un nome che corrispondono a piu' di una
    /// sessione **non scelgono**: continuare su una conversazione a caso sarebbe il modo peggiore
    /// di sbagliare, perche' l'errore si vedrebbe solo dopo aver scritto nella storia sbagliata.
    /// </summary>
    internal static SessionChoice Resolve(string? answer, IReadOnlyList<SessionPayload> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        string text = (answer ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return SessionChoice.Cancelled;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int position))
        {
            return position >= 1 && position <= sessions.Count
                ? new SessionChoice(sessions[position - 1].Id, null)
                : new SessionChoice(null, Ui.Text("Non c'e' una riga con quel numero."));
        }

        return Reference(text, [.. sessions.Select(session => (session.Id, session.DisplayName))]);
    }

    /// <summary>
    /// Una sessione indicata per identificatore intero, prefisso di almeno quattro caratteri o nome, **mai** per numero
    /// di riga. E' la regola dei comandi `pa session show|name|archive|transcript|fork|delete|capsule` (23 settembre 2026):
    /// l'elenco mostra otto caratteri e un nome, e quei comandi accettavano solo l'identificatore intero. Con quello che
    /// si legge rispondevano «non esiste», e `delete` con un nome non toglieva niente dicendo di aver finito. Un numero di
    /// riga resta del selettore: la riga 2 di un elenco non visto e' una conversazione a caso.
    /// </summary>
    internal static SessionChoice Reference(string? answer, IReadOnlyList<(string Id, string? Name)> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        string text = (answer ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return SessionChoice.Cancelled;
        }

        if (sessions.Any(session => string.Equals(session.Id, text, StringComparison.OrdinalIgnoreCase)))
        {
            return new SessionChoice(sessions.First(session => string.Equals(session.Id, text, StringComparison.OrdinalIgnoreCase)).Id, null);
        }

        if (sessions.Where(session => string.Equals(session.Name, text, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } named)
        {
            return named.Count == 1
                ? new SessionChoice(named[0].Id, null)
                : new SessionChoice(null, Ui.Text("Quel nome appartiene a piu' di una sessione."));
        }

        if (text.Length < MinimumPrefix)
        {
            return new SessionChoice(null, Ui.Text("Un identificatore abbreviato richiede almeno quattro caratteri."));
        }

        List<(string Id, string? Name)> matches =
            [.. sessions.Where(session => session.Id.StartsWith(text, StringComparison.OrdinalIgnoreCase))];

        return matches.Count switch
        {
            1 => new SessionChoice(matches[0].Id, null),
            0 => new SessionChoice(null, Ui.Text("Nessuna sessione corrisponde.")),
            _ => new SessionChoice(null, Ui.Text("Quell'inizio di identificatore corrisponde a piu' di una sessione.")),
        };
    }

    /// <summary>
    /// Vero se la risposta e' un numero di riga.
    ///
    /// Serve a chi cerca: un numero si riferisce **all'elenco appena mostrato**, quindi non va
    /// cercato in un elenco piu' ampio — la riga 2 diventerebbe un'altra conversazione sotto gli
    /// occhi di chi l'ha appena letta. Un identificatore o un nome invece indicano una conversazione
    /// precisa, e possono essere cercati anche fra le archiviate.
    /// </summary>
    internal static bool IsPosition(string? answer) =>
        int.TryParse((answer ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// Identificatore abbreviato per la resa.
    ///
    /// Otto caratteri bastano a distinguerlo a occhio fra le poche sessioni mostrate, e la scelta
    /// accetta comunque la forma intera: l'elenco e' fatto per essere letto, non per essere copiato.
    /// </summary>
    private static string Short(string id) => id.Length <= 8 ? id : id[..8];
}
