using System.Text;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Privacy;

/// <summary>Esito della conclusione di un rilascio a blocchi.</summary>
/// <param name="Full">Screening della risposta intera, che resta l'esito del turno.</param>
/// <param name="Remainder">Testo non ancora mostrato, quando l'output e' ammesso.</param>
public sealed record StreamCompletion(ScreenedOutput Full, string? Remainder);

/// <summary>
/// Rilascio a blocchi della risposta, con lo screening applicato **prima** che il testo compaia.
///
/// [ADR-021](../../docs/02-architecture/adr/ADR-021-terminale-moderno.md) supera ADR-008: la
/// risposta puo' comparire mentre arriva, ma solo a blocchi gia' esaminati. Restano vere le due
/// ragioni originali — un segreto puo' essere spezzato fra token, e la redazione token per token
/// senza lookahead non garantisce nulla — e per questo il rilascio segue cinque regole, scritte in
/// `03-specs/PRIVACY.md` prima di questo codice:
///
/// 1. si accumula tutto;
/// 2. si riesamina l'**intero testo accumulato** a ogni confine, non il solo frammento nuovo, cosi'
///    un finding che nasce dall'unione di due frammenti viene visto;
/// 3. si rilascia solo un prefisso di **righe intere**;
/// 4. si trattengono gli ultimi <see cref="CarryChars"/> caratteri finche' la risposta non e'
///    conclusa;
/// 5. un finding bloccante ferma tutto: nulla viene piu' rilasciato.
///
/// La decisione di bloccare si prende sempre sul testo intero; la redazione dei contatti si applica
/// alla porzione rilasciata.
/// </summary>
public sealed class StreamingScreener
{
    /// <summary>
    /// Caratteri trattenuti in coda finche' la risposta non e' conclusa.
    ///
    /// Non e' un numero scelto a sentimento: e' il limite superiore dei tre rilevatori bloccanti.
    /// L'intestazione di una chiave privata sta in una settantina di caratteri, una password
    /// assegnata e' una parola chiave piu' 6..128 caratteri, una chiave API e' un prefisso fisso
    /// piu' almeno sedici. Nessuno dei tre puo' quindi cominciare dentro il prefisso rilasciato e
    /// completarsi dopo.
    /// </summary>
    public const int CarryChars = 256;

    private readonly OutputScreener screener;
    private readonly PrivacyLimits limits;
    private readonly bool redactContacts;
    private readonly DataClass floor;
    private readonly StringBuilder accumulated = new();

    private int released;

    /// <summary>
    /// Verdetto che ha fermato il rilascio, quando e' successo.
    ///
    /// Va **ricordato**, non ricalcolato: la scansione ha un timeout per rilevatore, quindi la
    /// stessa risposta puo' risultare incompleta sotto carico e completa un istante dopo.
    /// Riesaminare alla fine poteva restituire «ammesso» dopo aver bloccato, cioe' due affermazioni
    /// incoerenti nello stesso oggetto: chi si fida del verdetto finale avrebbe creduto che
    /// l'output fosse a posto mentre non ne era uscito niente.
    /// </summary>
    private ScreenedOutput? blockedBy;

    /// <param name="screener">Screening gia' configurato, lo stesso della risposta intera.</param>
    /// <param name="limits">Limiti del profilo.</param>
    /// <param name="redactContacts">Obbligo di redazione dei contatti.</param>
    /// <param name="floor">Classe minima del profilo.</param>
    public StreamingScreener(OutputScreener screener, PrivacyLimits limits, bool redactContacts, DataClass floor)
    {
        ArgumentNullException.ThrowIfNull(screener);
        ArgumentNullException.ThrowIfNull(limits);

        this.screener = screener;
        this.limits = limits;
        this.redactContacts = redactContacts;
        this.floor = floor;
    }

    /// <summary>Vero quando un finding bloccante ha fermato il rilascio.</summary>
    public bool Blocked { get; private set; }

    /// <summary>Caratteri gia' rilasciati, contati sul testo grezzo accumulato.</summary>
    public int ReleasedLength => released;

    /// <summary>
    /// Aggiunge un frammento e restituisce il testo che si puo' mostrare **adesso**, oppure null
    /// quando non c'e' nulla da mostrare o il rilascio e' fermo.
    /// </summary>
    /// <param name="chunk">Frammento appena arrivato dal provider.</param>
    public string? Append(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (Blocked)
        {
            return null;
        }

        accumulated.Append(chunk);

        string text = accumulated.ToString();

        // Si esamina solo quando c'e' qualcosa da rilasciare: una riga intera nuova oltre la coda trattenuta (regola 3).
        // Fino al 23 settembre 2026 si esaminava a ogni frammento, cioe' a ogni token, e l'esame comprende il rilevatore a
        // modello: duecento inferenze ONNX per una risposta, sei core su dodici tolti al modello che stava scrivendo, e la
        // generazione locale scendeva a meno della meta'. Fra due confini nulla compare, quindi nulla va deciso; un
        // finding bloccante si vede al confine dopo o alla conclusione, prima di qualunque rilascio.
        if (SafeCut(text) <= released)
        {
            return null;
        }

        // Regola 2: il verdetto si prende sempre sull'intero testo accumulato. Un segreto a cavallo
        // di due frammenti esiste solo nell'unione, e guardare il solo frammento nuovo non lo
        // troverebbe mai.
        ScreenedOutput verdict = screener.Screen(text, limits, redactContacts, floor);

        if (verdict.Reason is OutputBlockReason.ScanIncomplete)
        {
            // **Non sapere non e' un finding.** Un rilevatore che supera il proprio timeout — 50 ms
            // per superficie — rende la scansione incompleta, e in quel momento non si rilascia
            // nulla: e' la scelta conservativa e resta. Ma spegnere il rilascio per **tutto il resto
            // della risposta** sarebbe un'altra cosa, e la regola 5 parla di un *finding* bloccante,
            // non di una scansione non conclusa.
            //
            // Succedeva davvero: sotto il carico del gate un timeout transitorio faceva comparire
            // l'intera risposta solo alla fine, senza che nessuno lo dichiarasse. Al confine
            // successivo si riprova; se l'incompletezza persiste, e' `Complete()` a chiudere
            // l'output. Nessun byte non esaminato raggiunge lo schermo in nessuno dei due casi.
            return null;
        }

        if (!verdict.IsAllowed)
        {
            // Regola 5: da qui non esce piu' niente, nemmeno cio' che era gia' accumulato. Il
            // motivo si conserva: e' quello che la conclusione dovra' dichiarare.
            //
            // Vale per un finding bloccante e per il tetto sui byte: il testo accumulato puo' solo
            // crescere, quindi una volta superato il limite non tornera' dentro.
            Blocked = true;
            blockedBy = verdict;
            return null;
        }

        int cut = SafeCut(text);

        if (cut <= released)
        {
            return null;
        }

        string slice = text[released..cut];

        // La redazione si applica alla porzione che sta per comparire. Il taglio e' su riga intera,
        // e un contatto non attraversa un a capo.
        ScreenedOutput screened = screener.Screen(slice, limits, redactContacts, floor);

        if (!screened.IsAllowed)
        {
            // **Il cursore non avanza su cio' che non e' comparso** (difetto trovato in S69).
            //
            // Prima `released` veniva spostato **prima** di questo esame: se la fetta non passava —
            // anche solo per un'incompletezza transitoria, che i 50 ms per rilevatore rendono
            // possibile — il chiamante riceveva null, ma quel testo risultava gia' rilasciato.
            // `Complete()` emette solo cio' che sta dopo `released`, quindi la fetta spariva per
            // sempre e la risposta arrivava all'operatore **con un buco**, mentre il verdetto finale
            // diceva ammesso.
            //
            // Trattenere e' conservativo; perdere output non lo e': chi legge non ha modo di sapere
            // che manca qualcosa. Al confine successivo si riprova, come per la regola 5bis.
            return null;
        }

        released = cut;

        return screened.Text;
    }

    /// <summary>
    /// Conclude il rilascio: esamina la risposta intera e restituisce cio' che resta da mostrare.
    /// </summary>
    public StreamCompletion Complete()
    {
        // Un rilascio gia' fermo dichiara il **motivo di allora**, senza riesaminare: la scansione
        // ha un timeout per rilevatore, e rileggere sotto carico diverso poteva dare «ammesso» dopo
        // aver bloccato.
        if (blockedBy is ScreenedOutput stopped)
        {
            return new StreamCompletion(stopped, null);
        }

        string text = accumulated.ToString();
        ScreenedOutput full = screener.Screen(text, limits, redactContacts, floor, final: true);

        if (!full.IsAllowed)
        {
            Blocked = true;
            blockedBy = full;
            return new StreamCompletion(full, null);
        }

        if (released >= text.Length)
        {
            return new StreamCompletion(full, null);
        }

        string rest = text[released..];

        ScreenedOutput screened = screener.Screen(rest, limits, redactContacts, floor, final: true);

        if (!screened.IsAllowed)
        {
            // **Non si dichiara ammesso cio' che non e' stato esaminato con esito** (difetto trovato
            // in S70).
            //
            // Prima il cursore avanzava **prima** di questo esame e il verdetto restituito era quello
            // del testo intero: se la coda non passava — anche solo per un'incompletezza transitoria,
            // che i 50 ms per rilevatore rendono possibile — il chiamante riceveva `Full` **ammesso**
            // e `Remainder` nullo. L'operatore vedeva una risposta che finisce a meta' e un verdetto
            // che diceva che era tutto a posto.
            //
            // E' lo stesso difetto chiuso in S69 in `Append`, rimasto nel metodo gemello. Qui e'
            // peggio: la conclusione e' terminale, quindi non c'e' un confine successivo a cui
            // riprovare — chiudere in sicurezza e' l'unica uscita onesta.
            Blocked = true;
            blockedBy = screened;

            return new StreamCompletion(screened, null);
        }

        released = text.Length;

        return new StreamCompletion(full, screened.Text);
    }

    /// <summary>
    /// Confine del rilascio: regole 3 e 4 insieme. Si torna indietro di
    /// <see cref="CarryChars"/> e poi si scende all'ultimo a capo, perche' una riga parziale puo'
    /// contenere la prima meta' di un segreto.
    /// </summary>
    private static int SafeCut(string text)
    {
        int safe = text.Length - CarryChars;

        if (safe <= 0)
        {
            return 0;
        }

        int newline = text.LastIndexOf('\n', safe - 1);

        return newline < 0 ? 0 : newline + 1;
    }
}
