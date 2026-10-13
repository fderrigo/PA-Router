using System.Globalization;
using System.Text;

namespace PA.Router.Abstractions.Contracts;

/// <summary>Un messaggio da consegnare a un modello di chat: chi parla e che cosa dice.</summary>
/// <param name="Role"><c>user</c> oppure <c>assistant</c>. Mai <c>system</c>: quel ruolo e' delle sole istruzioni.</param>
/// <param name="Text">Testo gia' esaminato.</param>
public readonly record struct ChatMessage(string Role, string Text);

/// <summary>
/// La forma in cui un payload esaminato si consegna a un modello di chat (S116).
///
/// Il payload e' un testo solo: la richiesta, poi ogni elemento di contesto sotto la sua intestazione
/// <c>[context:id]</c>. E' la forma giusta per esaminarlo — un segreto puo' nascere dall'unione di due frammenti —
/// ma consegnata cosi' a un modello diventa un documento da commentare: la richiesta in cima, e sotto una pila di
/// blocchi etichettati. Un modello piccolo rispondeva «sembra che tu abbia iniziato una conversazione con
/// un'intelligenza artificiale» a qualunque domanda, perche' descriveva cio' che leggeva invece di continuarlo.
///
/// Qui i turni precedenti tornano a essere turni: cio' che ha scritto l'operatore nel ruolo <c>user</c>, cio' che
/// ha risposto il servizio nel ruolo <c>assistant</c>, e la richiesta attuale per ultima, con il resto del contesto
/// — ricordi, allegati, skill — ancora sotto le sue intestazioni, perche' quelli sono dati e non battute.
///
/// **Non cambia che cosa parte**: ogni byte consegnato viene dal payload esaminato, redatto e ridotto, e nessuno
/// viene aggiunto. Cambia solo come e' diviso fra i messaggi. Un turno precedente non diventa un'istruzione: i
/// ruoli sono due, e nessuno dei due e' quello di sistema.
///
/// I confini si cercano seguendo il manifest, nell'ordine in cui gli elementi sono stati inclusi. Se anche uno solo
/// non si trova dove dovrebbe, si rinuncia e si consegna il testo intero come prima: una divisione sbagliata
/// sarebbe peggio di nessuna divisione. Un testo che imiti un'intestazione puo' al piu' spostare un confine fra
/// due blocchi che partono comunque: non puo' far comparire contenuti ne' un ruolo di sistema.
/// </summary>
public static class ChatTranscript
{
    /// <summary>Divide il payload in messaggi. Un solo messaggio <c>user</c> quando non c'e' cronologia.</summary>
    public static IReadOnlyList<ChatMessage> Split(SafePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        string text = Encoding.UTF8.GetString(payload.Bytes.Span);

        if (!payload.Manifest.Any(IsTurn))
        {
            return [new ChatMessage("user", text)];
        }

        // Dove comincia ciascun elemento, nell'ordine del manifest.
        List<(ContextManifestEntry Entry, int Header, int Body)> marks = [];
        int from = 0;

        foreach (ContextManifestEntry entry in payload.Manifest)
        {
            string marker = "\n\n[context:" + entry.ItemId + "]\n";
            int at = text.IndexOf(marker, from, StringComparison.Ordinal);

            if (at < 0)
            {
                return [new ChatMessage("user", text)];
            }

            marks.Add((entry, at, at + marker.Length));
            from = at + marker.Length;
        }

        List<ChatMessage> history = [];
        StringBuilder current = new(marks.Count == 0 ? text : text[..marks[0].Header]);

        for (int index = 0; index < marks.Count; index++)
        {
            (ContextManifestEntry entry, int header, int body) = marks[index];
            int end = index + 1 < marks.Count ? marks[index + 1].Header : text.Length;

            // Una conversazione comincia da chi chiede. Se il taglio per budget ha tolto la domanda e lasciato la
            // risposta, quella risposta non e' una battuta a cui agganciarsi: resta un dato, con la sua intestazione.
            bool orphan = entry.Provenance is Provenance.AgentOutput && history.Count == 0;

            if (IsTurn(entry) && !orphan)
            {
                history.Add(new ChatMessage(entry.Provenance is Provenance.AgentOutput ? "assistant" : "user", text[body..end]));
            }
            else
            {
                // Dato, non battuta: resta con la richiesta, sotto la sua intestazione.
                current.Append(text, header, end - header);
            }
        }

        return [.. history, new ChatMessage("user", current.ToString())];
    }

    /// <summary>
    /// Un turno della cronologia: l'identificatore che gli da' chi costruisce il turno <b>e</b> la provenienza
    /// che gli corrisponde. L'identificatore da solo non basta: un contenuto qualunque non diventa una battuta
    /// del servizio perche' si chiama come una.
    /// </summary>
    private static bool IsTurn(ContextManifestEntry entry)
    {
        string[] parts = entry.ItemId.Split('-');

        return parts.Length == 3
            && parts[0] == "turn"
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && ((parts[2] == "input" && entry.Provenance is Provenance.Operator)
                || (parts[2] == "output" && entry.Provenance is Provenance.AgentOutput));
    }
}
