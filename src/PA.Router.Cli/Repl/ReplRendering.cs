using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Cli.Repl;

/// <summary>
/// Resa dell'esito di un turno.
///
/// Estratta da <see cref="ReplSession"/> in S60 per due ragioni. La prima e' verificabile: finche'
/// viveva dentro un metodo privato della sessione, l'unico modo di provarla era pilotare un REPL
/// intero. La seconda e' che la stessa resa servira' alla coda dei messaggi di S65, e una seconda
/// copia divergerebbe.
///
/// Qui non c'e' stato: solo un esito e una resa. Cio' che dipende dalla sessione — i blocchi gia'
/// mostrati mentre la risposta arrivava — resta nella sessione, perche' e' memoria del turno e non
/// del suo esito.
/// </summary>
internal static class ReplRendering
{
    /// <summary>
    /// Mostra l'esito. Su deny la specifica chiede una ragione concreta e i target esclusi, mai il
    /// suggerimento di abbassare la privacy: il rimedio e' cambiare la regola, consapevolmente.
    /// </summary>
    /// <param name="renderer">Resa da usare.</param>
    /// <param name="result">Esito del turno.</param>
    /// <param name="answer">Testo da mostrare, quando non e' gia' comparso a blocchi.</param>
    /// <param name="verbose">Vero per ogni motivo con i suoi parametri; falso per la sola cosa che decide.</param>
    internal static void Present(
        IReplRenderer renderer,
        ReplTurnResult result,
        string? answer = null,
        bool verbose = false,
        Func<string, string>? nameOf = null,
        Func<string, string?>? quotaOf = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(result);

        string? text = answer ?? result.Output;

        if (text is { Length: > 0 })
        {
            renderer.Answer(text);
        }

        renderer.Outcome(result.State);

        if (string.Equals(result.State, ExecutionState.Completed.ToWire(), StringComparison.Ordinal))
        {
            // La regola «perimetro debole» del profilo (ADR-029): ha risposto un modello interno sotto la soglia.
            if (result.WeakPerimeter)
            {
                renderer.Warn("  " + Ui.Text("turn.weakPerimeter"));
            }

            // Nessun modello acceso bastava per la domanda e ha risposto il piu' capace (ADR-031): la risposta c'e', ma va
            // letta sapendolo.
            if (result.BelowDemand)
            {
                renderer.Warn("  " + Ui.Text("turn.belowDemand"));
            }

            return;
        }

        // Una risposta comparsa a meta' va dichiarata tale: un racconto che si interrompe a meta'
        // frase senza una riga che lo dica sembra un modello che ha smesso, non un turno fermato.
        if (text is { Length: > 0 })
        {
            renderer.Warn("  " + Ui.Text("turn.partial"));
        }

        renderer.Error(
            Ui.Text("Esito: ") + result.State
            + (result.ErrorCode is string code ? " (" + code + ")" : string.Empty));

        // Il codice dice che cosa e' successo; questa riga dice quale limite l'ha deciso e dove si
        // cambia. Senza, un timeout e' una parola e basta.
        if (result.ErrorCode is string failure && Ui.Contains("fail." + failure))
        {
            renderer.Warn("  " + Ui.Text("fail." + failure));
        }

        // `permission_required`: la specifica lo prevede dalla S13 e nessuna resa lo mostrava. Un
        // agente che pretende un'approvazione nativa non integrabile headless chiude il turno, e
        // l'operatore deve sapere che non c'e' nulla da riprovare qui — mai un «si» automatico.
        if (string.Equals(result.ErrorCode, ErrorCode.ControlUnavailable.ToWire(), StringComparison.Ordinal))
        {
            renderer.Warn(
                Ui.Text("  permission_required: il programma chiede un'approvazione che questo canale non sa esprimere."));

            renderer.Warn(
                Ui.Text("  Il turno si chiude qui: il router non inserisce un consenso al posto tuo. ")
                + Ui.Text("Esegui il programma nel suo modo interattivo, oppure usa un servizio che sappia esprimere la concessione."));
        }

        // Con i numeri si disegna il rifiuto nuovo; senza — un worker piu' vecchio, un percorso che
        // non li produce — resta la forma a soli codici, che e' sempre meglio di niente.
        if (result.Denial is DenialDetail denial)
        {
            ReplDenialView.Write(renderer, denial, verbose, nameOf, quotaOf: quotaOf);
            return;
        }

        // I servizi esclusi spiegano **un rifiuto della policy**. Un turno che e' stato instradato e
        // poi e' andato in timeout non ha nulla a che vedere con loro: elencarli faceva sfilare
        // quindici righe su embedding e rilevatori davanti a un operatore che aveva solo aspettato
        // troppo, e nascondeva l'unica cosa vera — il tempo massimo di una richiesta.
        if (!string.Equals(result.ErrorCode, ErrorCode.NoAdmissibleTarget.ToWire(), StringComparison.Ordinal))
        {
            return;
        }

        Exclusions(renderer, result.Reasons);

        if (result.Reasons.Count > 0)
        {
            renderer.Info(Ui.Text("Per i dettagli della decisione: /explain, poi riscrivi la richiesta."));
        }
    }

    /// <summary>
    /// Dice perche' nessun servizio ha risposto, nella lingua di chi legge e con il nome del parametro da cambiare.
    ///
    /// Prima comparivano undici righe di codici, tutte uguali per peso: l'unico servizio acceso, rifiutato per un
    /// voto troppo basso, stava in fondo dopo dieci servizi semplicemente spenti. Qui i servizi accesi vengono
    /// prima, un motivo per riga con il suo rimedio; quelli spenti sono una riga sola, perche' «non l'hai attivato»
    /// non e' un guasto. Il codice resta fra parentesi: serve a chi cerca nei documenti o chiede aiuto.
    ///
    /// E' una resa: legge i motivi che il valutatore ha gia' scritto e non ne decide nessuno.
    /// </summary>
    private static void Exclusions(IReplRenderer renderer, IReadOnlyList<string> reasons)
    {
        List<(string Id, string[] Codes)> parsed = [];

        foreach (string reason in reasons)
        {
            int open = reason.IndexOf('[', StringComparison.Ordinal);
            int close = reason.LastIndexOf(']');
            int space = reason.IndexOf(' ', StringComparison.Ordinal);

            if (open < 0 || close < open || space < 0)
            {
                // Una forma che non si riconosce si mostra com'e': meglio un codice che una riga persa.
                renderer.Warn(Ui.Text("  escluso: ") + reason);
                continue;
            }

            parsed.Add((reason[..space], reason[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }

        const string disabled = "TARGET_DISABLED";

        foreach ((string id, string[] codes) in parsed.Where(item => !item.Codes.Contains(disabled)))
        {
            renderer.Warn("  " + Ui.Format("deny.service {0}", id));

            foreach (string code in codes)
            {
                string named = "deny." + code + " {0}";
                string plain = "deny." + code;
                string text = Ui.Contains(named) ? Ui.Format(named, id) : Ui.Contains(plain) ? Ui.Text(plain) : Ui.Text("deny.unknown");
                renderer.Warn("    - " + text + " (" + code + ")");
            }
        }

        string[] off = [.. parsed.Where(item => item.Codes.Contains(disabled)).Select(item => item.Id)];

        if (off.Length > 0)
        {
            renderer.Info("  " + Ui.Format("deny.disabled {0}", string.Join(", ", off)));
        }

        if (parsed.Count > 0 && off.Length == parsed.Count)
        {
            renderer.Warn("  " + Ui.Text("deny.nothingEnabled"));
        }
    }
}
