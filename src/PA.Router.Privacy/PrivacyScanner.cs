using System.Text;
using System.Text.RegularExpressions;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Privacy.Detectors;
using PA.Router.Privacy.Model;

namespace PA.Router.Privacy;

/// <summary>
/// Esegue i detector su una superficie e ne compone il risultato.
///
/// Il comportamento in caso di guasto e' chiuso: se anche un solo detector non conclude, la
/// scansione e' incompleta e la classe diventa <see cref="DataClass.Unknown"/>, che blocca il
/// cloud. Non esiste un percorso in cui un timeout produce un risultato piu' permissivo di una
/// scansione riuscita (AC-03).
/// </summary>
public sealed class PrivacyScanner
{
    /// <summary>Versione dell'insieme di detector, registrata nel report.</summary>
    public const string DetectorSetVersion = "detectors-v1";

    private readonly IReadOnlyList<IPrivacyDetector> detectors;
    private readonly PiiModelStage? model;

    public PrivacyScanner()
        : this(DefaultDetectors())
    {
    }

    public PrivacyScanner(IReadOnlyList<IPrivacyDetector> detectors)
        : this(detectors, model: null)
    {
    }

    /// <summary>
    /// Scanner con lo stadio a modello attivo. Il modello **si aggiunge** ai detector, non li
    /// sostituisce: i sette deterministici restano esatti dove la forma e' verificabile, e nessuna
    /// classe puo' essere abbassata da cio' che il modello non vede.
    /// </summary>
    public PrivacyScanner(IReadOnlyList<IPrivacyDetector> detectors, PiiModelStage? model)
    {
        ArgumentNullException.ThrowIfNull(detectors);

        this.model = model;

        if (detectors.Count == 0)
        {
            // Una pipeline senza detector direbbe «nessun dato personale» su qualunque input.
            throw new ArgumentException("At least one detector is required.", nameof(detectors));
        }

        this.detectors = detectors;

        SetVersion = model is null ? DetectorSetVersion : DetectorSetVersion + "+" + model.Version;
    }

    /// <summary>
    /// Versione dell'insieme in uso. Con lo stadio a modello attivo dichiara anche quello: due
    /// scansioni con insiemi diversi non sono confrontabili, e l'audit deve poterle distinguere.
    /// </summary>
    public string SetVersion { get; }

    /// <summary>Vero se lo stadio a modello e' attivo.</summary>
    public bool HasModel => model is not null;

    /// <summary>
    /// Insieme di detector della beta. Otto dalla versione 2: ai sette deterministici si aggiunge
    /// il rilevamento delle categorie particolari dell'articolo 9, anch'esso deterministico.
    /// </summary>
    public static IReadOnlyList<IPrivacyDetector> DefaultDetectors() =>
    [
        new EmailDetector(),
        new PhoneDetector(),
        new ItalianFiscalCodeDetector(),
        new ApiKeyDetector(),
        new PrivateKeyDetector(),
        new AssignedPasswordDetector(),
        new SensitiveCategoryDetector(),
        new EntropyDetector(),
    ];

    /// <summary>
    /// Analizza una superficie gia' delimitata. Il chiamante ha il compito di rispettare i
    /// limiti di byte: qui l'input e' considerato bounded.
    /// </summary>
    /// <param name="text">Testo da analizzare.</param>
    /// <param name="floor">Classe minima gia' nota, tipicamente quella del profilo.</param>
    /// <param name="segments">
    /// Pezzi della superficie per lo stadio a modello, in caratteri: vedi <see cref="PiiModelStage.Scan"/>. I
    /// rilevatori deterministici esaminano sempre la superficie intera, cosi' un segreto a cavallo di due pezzi resta
    /// visibile.
    /// </param>
    /// <param name="attachments">Indici dei pezzi che sono allegati, per il loro budget separato.</param>
    /// <param name="classified">
    /// Pezzi gia' classificati, in caratteri: ricordi e risposte della cronologia, che portano la classe con cui sono nati.
    /// Li' l'elenco di parole delle categorie particolari non guarda (23 settembre 2026): una risposta sul teorema di
    /// Bayes che parlava di «malati» rendeva sensibile tutta la conversazione, e le domande dopo non potevano piu' uscire.
    /// L'art. 9 riguarda una persona; se la domanda di quel turno lo era, la risposta ne porta gia' la classe.
    /// </param>
    public PrivacyReport Scan(
        string text,
        DataClass floor,
        IReadOnlyList<(int Start, int Length)>? segments = null,
        IReadOnlySet<int>? attachments = null,
        IReadOnlyList<(int Start, int Length)>? classified = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        ByteOffsetMap offsets = ByteOffsetMap.For(text);
        List<PrivacyFinding> findings = [];
        List<string> incomplete = [];
        DataClass aggregate = floor;

        foreach (IPrivacyDetector detector in detectors)
        {
            int before = findings.Count;

            try
            {
                detector.Scan(text, offsets, findings);
            }
            catch (RegexMatchTimeoutException)
            {
                // Il detector non ha concluso: non sappiamo che cosa contenga il testo.
                //
                // I match che aveva gia' scritto prima di scadere vengono tolti (S80): sono il
                // frutto di una ricerca interrotta a meta', e la ripetizione ripercorre il testo
                // dall'inizio e li ritrova tutti. Lasciarli qui li conterebbe due volte.
                findings.RemoveRange(before, findings.Count - before);
                incomplete.Add(detector.Name);
                continue;
            }

            if (classified is { Count: > 0 } && detector.Type is FindingType.SensitiveCategory)
            {
                List<(int Start, int End)> spans = [.. classified.Select(span =>
                    (offsets.ByteOffset(span.Start), offsets.ByteOffset(span.Start) + offsets.ByteLength(span.Start, span.Length)))];

                for (int index = findings.Count - 1; index >= before; index--)
                {
                    PrivacyFinding found = findings[index];

                    if (spans.Any(span => found.StartByte >= span.Start && found.StartByte + found.ByteLength <= span.End))
                    {
                        findings.RemoveAt(index);
                    }
                }
            }

            if (findings.Count > before)
            {
                aggregate = DataClasses.Max(aggregate, detector.ClassContribution);
            }
        }

        if (model is not null)
        {
            // Lo stadio a modello gira per ultimo: vede i finding deterministici e non li
            // duplica. Un suo guasto vale come quello di un detector — scansione incompleta,
            // classe indeterminata, cloud chiuso — perche' con il modello attivo la scansione
            // che l'operatore ha configurato non e' avvenuta.
            int beforeModel = findings.Count;

            try
            {
                aggregate = DataClasses.Max(aggregate, model.Scan(text, offsets, findings, segments, attachments));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Stessa regola dei detector: uno stadio che non conclude non lascia match a meta'.
                findings.RemoveRange(beforeModel, findings.Count - beforeModel);
                incomplete.Add(PiiModelStage.StageName);
            }
        }

        if (incomplete.Count > 0)
        {
            // **Una seconda occasione, e una sola** (S80).
            //
            // Un rilevatore che supera i propri 50 ms non dice che il testo sia impossibile da
            // classificare: dice che **questa volta** non ha concluso. Sotto carico capita, ed e'
            // la ragione per cui mezza suite tollera due esiti — «la classe attesa **oppure**
            // unknown» — e per cui un'evidenza di step attribuiva a un campo morto i fallimenti
            // sotto carico.
            //
            // Riprovando una volta, un'incompletezza transitoria sparisce e il turno usa la classe
            // vera. Se il secondo giro non conclude, l'incompletezza e' **persistente**: due
            // tentativi falliti non sono piu' un caso, e da li' in avanti `Incomplete` significa
            // «non si riesce a classificare questo testo», che e' cio' che la policy puo' negare
            // anche verso un servizio locale.
            //
            // **Un giro che non conclude non lascia niente.** Un rilevatore scaduto a meta' puo'
            // avere gia' scritto qualche match: quelli vengono tolti quando scade, qui come nella
            // ripetizione. Non e' prudenza, e' aritmetica — la ricerca ripetuta ripercorre il testo
            // dall'inizio e ritrova anche quei match, quindi tenerli li' conterebbe due volte ogni
            // dato personale trovato da un rilevatore lento.
            //
            // **Lo stadio a modello non si ripete.** La sua incompletezza non e' un guasto
            // transitorio: e' il budget di latenza dichiarato dall'operatore che ha chiuso la
            // scansione dopo la prima finestra. Riaprirla spenderebbe una seconda volta proprio il
            // tempo che quel budget esiste per limitare, e il secondo giro finirebbe come il primo.
            incomplete = Retry(text, offsets, findings, ref aggregate, incomplete);
        }

        return incomplete.Count == 0
            ? new PrivacyReport(SetVersion, findings, aggregate, ScanCertainty.Complete, offsets.TotalBytes, [])
            : new PrivacyReport(SetVersion, findings, DataClass.Unknown, ScanCertainty.Incomplete, offsets.TotalBytes, incomplete);
    }

    /// <summary>
    /// Ripete i soli rilevatori che non hanno concluso, una volta sola, e restituisce quelli che
    /// non concludono nemmeno adesso (S80).
    ///
    /// Non e' un ripiego permissivo: e' l'unico modo di distinguere «questa volta non ha concluso»
    /// da «questo testo non si riesce a classificare», e sono due fatti che portano a decisioni
    /// diverse. Una sola ripetizione, perche' un ciclo che insiste sposterebbe il costo del carico
    /// dentro il turno di chi sta aspettando.
    /// </summary>
    private List<string> Retry(
        string text,
        ByteOffsetMap offsets,
        List<PrivacyFinding> findings,
        ref DataClass aggregate,
        List<string> incomplete)
    {
        List<string> stillIncomplete = [];

        foreach (IPrivacyDetector detector in detectors)
        {
            if (!incomplete.Contains(detector.Name, StringComparer.Ordinal))
            {
                continue;
            }

            int before = findings.Count;

            try
            {
                detector.Scan(text, offsets, findings);
            }
            catch (RegexMatchTimeoutException)
            {
                // Come nel primo giro: cio' che ha scritto prima di scadere non resta.
                findings.RemoveRange(before, findings.Count - before);
                stillIncomplete.Add(detector.Name);
                continue;
            }

            if (findings.Count > before)
            {
                aggregate = DataClasses.Max(aggregate, detector.ClassContribution);
            }
        }

        // Lo stadio a modello non viene ripetuto (vedi `Scan`): se non ha concluso, resta fra gli
        // incompleti cosi' com'e'.
        if (incomplete.Contains(PiiModelStage.StageName, StringComparer.Ordinal))
        {
            stillIncomplete.Add(PiiModelStage.StageName);
        }

        return stillIncomplete;
    }

    /// <summary>
    /// Verifica che una superficie stia dentro il limite di byte indicato. Il testo non viene
    /// troncato in silenzio: superare il limite e' un errore tipizzato.
    /// </summary>
    public static void EnsureWithinLimit(string text, int maxBytes, string surface)
    {
        ArgumentNullException.ThrowIfNull(text);

        int bytes = Encoding.UTF8.GetByteCount(text);

        if (bytes > maxBytes)
        {
            throw new PaRouterException(
                ErrorCode.InputInvalid,
                "The " + surface + " exceeds the byte limit of the profile.")
                .WithMetadata("surface", surface)
                .WithMetadata("bytes", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithMetadata("maxBytes", maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
