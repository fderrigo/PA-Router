using System.Text;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Privacy;

/// <summary>Motivo per cui un output non viene restituito.</summary>
public enum OutputBlockReason
{
    [WireValue("none")]
    None,

    [WireValue("credential")]
    Credential,

    [WireValue("size")]
    Size,

    [WireValue("scan_incomplete")]
    ScanIncomplete,
}

/// <summary>
/// Esito dello screening dell'output.
/// </summary>
/// <param name="Text">Testo restituibile, null quando l'output e' bloccato.</param>
/// <param name="Reason">Motivo del blocco.</param>
/// <param name="Report">Report della scansione dell'output.</param>
/// <param name="RedactedCount">Sostituzioni applicate.</param>
public sealed record ScreenedOutput(
    string? Text,
    OutputBlockReason Reason,
    PrivacyReport Report,
    int RedactedCount)
{
    /// <summary>Vero se il testo puo' essere restituito.</summary>
    public bool IsAllowed => Reason is OutputBlockReason.None;

    /// <summary>Codice errore corrispondente al blocco.</summary>
    public ErrorCode? ErrorCode => Reason switch
    {
        OutputBlockReason.None => null,
        OutputBlockReason.Size => Abstractions.ErrorCode.OutputLimit,
        _ => Abstractions.ErrorCode.OutputBlocked,
    };

    /// <summary>Nessun contenuto nella resa testuale.</summary>
    public override string ToString() =>
        "ScreenedOutput { Allowed = " + IsAllowed.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ", Reason = " + Reason.ToWire() + " }";
}

/// <summary>
/// Screening dell'output prima che raggiunga il chiamante.
///
/// Un provider puo' emettere un segreto spezzato fra token: per questo la scansione avviene sulla
/// risposta completa, dopo il buffering, e mai sui frammenti. Durante l'esecuzione il client
/// riceve solo stati, quindi non esiste un percorso in cui un token non ancora esaminato sia
/// gia' uscito (03-specs/PRIVACY.md).
/// </summary>
public sealed class OutputScreener
{
    private readonly PrivacyScanner scanner;

    public OutputScreener(PrivacyScanner scanner)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        this.scanner = scanner;
    }

    /// <summary>Lunghezza massima di un pezzo esaminato dal modello, in caratteri.</summary>
    private const int MaxSegmentChars = 2000;

    /// <summary>
    /// Pezzi della risposta per lo stadio a modello: paragrafi (separati da una riga vuota), e un paragrafo troppo lungo
    /// diviso a fine riga. I confini dipendono solo dal testo che li precede, quindi restano gli stessi mentre la risposta
    /// cresce e la memoria dello stadio li riconosce.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> Segments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<(int Start, int Length)> segments = [];
        int start = 0;

        while (start < text.Length)
        {
            int blank = text.IndexOf("\n\n", start, StringComparison.Ordinal);
            int end = blank < 0 ? text.Length : blank + 2;

            while (end - start > MaxSegmentChars)
            {
                int newline = text.LastIndexOf('\n', start + MaxSegmentChars - 1, MaxSegmentChars);
                int cut = newline < start ? start + MaxSegmentChars : newline + 1;
                segments.Add((start, cut - start));
                start = cut;
            }

            if (end > start) { segments.Add((start, end - start)); }

            start = end;
        }

        return segments;
    }

    /// <summary>Tipi che comportano il blocco dell'intero output.</summary>
    public static IReadOnlySet<FindingType> BlockingTypes { get; } = new HashSet<FindingType>
    {
        FindingType.ApiKey,
        FindingType.PrivateKey,
        FindingType.AssignedPassword,
    };

    /// <summary>
    /// Esamina l'output completo.
    /// </summary>
    /// <param name="text">Risposta completa del provider.</param>
    /// <param name="limits">Limiti del profilo.</param>
    /// <param name="redactContacts">Obbligo di redazione dei contatti.</param>
    /// <param name="floor">Classe minima del profilo.</param>
    /// <param name="final">
    /// Vero per l'esame della risposta intera, quando non arrivera' altro testo: il modello dei dati personali ha il budget
    /// lungo degli allegati invece di quello del turno. Vedi <see cref="Segments"/>.
    /// </param>
    public ScreenedOutput Screen(string text, PrivacyLimits limits, bool redactContacts, DataClass floor, bool final = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(limits);

        if (Encoding.UTF8.GetByteCount(text) > limits.MaxOutputBytes)
        {
            // Nessun troncamento: un output parziale sarebbe testo mai esaminato per intero.
            return new ScreenedOutput(
                null,
                OutputBlockReason.Size,
                new PrivacyReport(PrivacyScanner.DetectorSetVersion, [], DataClass.Unknown, ScanCertainty.Incomplete, 0, ["size"]),
                0);
        }

        // A paragrafi, con chiavi stabili (23 settembre 2026): lo stadio a modello ricorda i pezzi gia' esaminati, e a ogni
        // riga rilasciata si esamina solo cio' che e' nuovo. Prima ogni confine riesaminava l'intera risposta accumulata:
        // il costo cresceva col quadrato della lunghezza, oltre qualche KB il budget del turno non bastava, nulla veniva
        // rilasciato e alla fine una risposta pagata veniva bloccata per scansione incompleta. L'esame finale ha il budget
        // degli allegati: l'operatore sta gia' aspettando, e una scansione conclusa vale piu' di una risposta buttata.
        // Nessuna regola si allenta: cio' che non conclude resta incompleto e bloccato.
        IReadOnlyList<(int Start, int Length)> segments = Segments(text);
        IReadOnlySet<int>? everything = final ? new HashSet<int>(Enumerable.Range(0, segments.Count)) : null;

        PrivacyReport report = scanner.Scan(text, floor, segments, everything);

        // **D14, chiuso in S68: fra due motivi veri vince il piu' specifico.**
        //
        // Un ritrovamento deterministico e' un **fatto**; l'incompletezza riguarda cio' che *altro*
        // potrebbe esserci, e i rilevatori deterministici sono per contratto gli unici a coprire le
        // credenziali (S28). Fino a S67 questi due controlli erano invertiti: una chiave privata gia'
        // trovata veniva dichiarata «scansione incompleta» quando un altro rilevatore superava il
        // proprio timeout di 50 ms — cosa che sotto carico accade davvero, ed e' il motivo per cui il
        // difetto si vedeva solo nel gate completo.
        //
        // L'output era bloccato in entrambi i casi e resta bloccato: **non era un difetto di
        // contenimento**. A cambiare e' cio' che l'operatore e l'audit leggono, e «non so, riprova»
        // porta a una decisione diversa da «vista una credenziale».
        if (report.Findings.Any(finding => BlockingTypes.Contains(finding.Type)))
        {
            return new ScreenedOutput(null, OutputBlockReason.Credential, report, 0);
        }

        // La certezza non viene toccata: il rapporto continua a dichiarare `Incomplete` anche nel
        // ramo qui sopra, perche' la scansione davvero non ha concluso. Il motivo del blocco e' piu'
        // preciso, non piu' ottimista.
        if (report.Certainty is ScanCertainty.Incomplete)
        {
            return new ScreenedOutput(null, OutputBlockReason.ScanIncomplete, report, 0);
        }

        if (!redactContacts)
        {
            // Senza obbligo di redazione l'output conserva i dati che il profilo locale ammette.
            return new ScreenedOutput(text, OutputBlockReason.None, report, 0);
        }

        RedactionResult redaction = ContactRedactor.Redact(text, report.Findings);

        return new ScreenedOutput(redaction.Text, OutputBlockReason.None, report, redaction.ReplacementCount);
    }
}
