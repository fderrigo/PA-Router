using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Privacy.Model;

/// <summary>
/// Stadio di scansione con modello locale, in sinergia con i detector deterministici.
///
/// I due rilevamenti non si sostituiscono: il deterministico e' esatto dove la forma e'
/// verificabile — il carattere di controllo di un codice fiscale e' matematica, una chiave privata
/// ha un'intestazione — e il modello trova cio' che non ha forma: un nome, un indirizzo, una
/// denominazione. Dove entrambi vedono la stessa cosa vince il deterministico: ha una verifica
/// strutturale, il modello una probabilita'.
///
/// Il testo viene esaminato a finestre, perche' il modello ne accetta un numero limitato di token
/// mentre il payload puo' essere molto piu' grande. Le finestre si sovrappongono, cosi' un'entita'
/// a cavallo di un confine resta leggibile in almeno una di esse. Se una finestra risulta
/// comunque troncata viene divisa a meta': una porzione non esaminata sarebbe una scansione
/// incompleta, non un risultato.
///
/// Il budget di latenza dichiarato nella configurazione limita il lavoro **aggiuntivo**: prima di
/// aprire una finestra oltre la prima si guarda il tempo speso, e oltre il budget la scansione si
/// interrompe dichiarandosi incompleta — `unknown`, che chiude il cloud. La prima finestra si fa
/// sempre: interrompere dopo aver gia' pagato il costo non restituirebbe il tempo a nessuno.
/// </summary>
public sealed class PiiModelStage
{
    /// <summary>Nome dello stadio, usato nei report di scansione incompleta.</summary>
    public const string StageName = "pii-model";

    /// <summary>
    /// Probabilita' minima perche' un'entita' venga accettata. Sotto questa soglia il modello non
    /// sta scegliendo fra due classi vicine: sta tirando a indovinare fra quarantacinque.
    /// </summary>
    public const double MinimumConfidence = 0.5;

    /// <summary>Caratteri di sovrapposizione fra due finestre consecutive.</summary>
    private const int OverlapChars = 256;

    /// <summary>Divisioni massime di una finestra ancora troppo lunga.</summary>
    private const int MaxSplitDepth = 8;

    /// <summary>
    /// Pezzi ricordati oltre i quali la memoria si svuota. Una conversazione ne produce due per turno; il tetto
    /// esiste perche' un processo che vive a lungo non accumuli senza fine.
    /// </summary>
    private const int MaxRemembered = 1024;

    private readonly IPiiModel model;

    /// <summary>
    /// Che cosa il modello ha trovato in un pezzo gia' esaminato, per impronta del pezzo (S117).
    ///
    /// La cronologia riparte a ogni turno con la richiesta, e rifare la scansione a modello di tutti i turni
    /// precedenti costava ogni volta di piu': con una conversazione lunga il budget di latenza si esauriva e la
    /// scansione si dichiarava incompleta, cioe' il turno si negava da solo. Lo stesso testo con lo stesso modello
    /// da' le stesse entita': si ricordano, e si esamina solo cio' che e' nuovo. Vive in memoria con lo stadio,
    /// quindi con la configurazione: un modello diverso e' uno stadio nuovo e ricomincia da capo. Non contiene
    /// testo: l'impronta e' un hash, le entita' sono etichetta, posizione e confidenza.
    /// </summary>
    private readonly ConcurrentDictionary<string, PiiEntity[]> remembered = new(StringComparer.Ordinal);
    private readonly int windowChars;
    private readonly TimeSpan budget;
    private readonly TimeSpan attachmentBudget;

    /// <param name="model">Modello che esegue il riconoscimento.</param>
    /// <param name="budget">
    /// Tempo massimo che l'operatore accetta di spendere su una superficie. Zero o negativo
    /// significa nessun limite dichiarato: il testo viene esaminato per intero.
    /// </param>
    /// <param name="attachmentBudget">
    /// Tempo massimo per i pezzi che sono allegati (S124). Separato dal primo perche' un allegato di qualche pagina, su un
    /// PC comune, costa secondi: con un budget unico da un secondo e mezzo ogni allegato oltre i 2 KB rendeva la scansione
    /// incompleta e il turno negato. Zero o negativo: nessun limite.
    /// </param>
    public PiiModelStage(IPiiModel model, TimeSpan budget = default, TimeSpan attachmentBudget = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        this.model = model;
        this.budget = budget;
        this.attachmentBudget = attachmentBudget;

        // Due caratteri per token e' una stima prudente: il vocabolario del modello contiene
        // parole intere, quindi il rapporto reale e' piu' alto e la finestra non si riempie. Una
        // stima ottimistica costerebbe una seconda passata su meta' testo.
        windowChars = Math.Max(512, (model.MaxTokens - 2) * 2);
    }

    /// <summary>Versione del modello, registrata in ogni finding che produce.</summary>
    public string Version => model.Version;

    /// <summary>Nome del modello, per le diagnosi.</summary>
    public string ModelName => model.Name;

    /// <summary>
    /// Aggiunge a <paramref name="findings"/> le entita' che il modello riconosce e che il
    /// prodotto traduce, e restituisce la classe che comportano. Un'eccezione non viene
    /// intercettata qui: la pipeline la traduce in scansione incompleta, che chiude il cloud.
    /// </summary>
    /// <param name="text">Superficie intera.</param>
    /// <param name="offsets">Mappa caratteri-byte della superficie.</param>
    /// <param name="findings">Ritrovamenti gia' presenti, quelli deterministici; qui si aggiungono quelli del modello.</param>
    /// <param name="segments">
    /// Pezzi della superficie da esaminare uno per uno, in caratteri: la richiesta e ogni elemento di contesto. Un
    /// pezzo gia' esaminato prima non si riesamina. Null esamina la superficie intera, senza ricordare niente.
    /// </param>
    /// <param name="attachments">Indici dei pezzi che sono allegati: il loro tempo si conta a parte.</param>
    public DataClass Scan(
        string text,
        ByteOffsetMap offsets,
        List<PrivacyFinding> findings,
        IReadOnlyList<(int Start, int Length)>? segments = null,
        IReadOnlySet<int>? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(offsets);
        ArgumentNullException.ThrowIfNull(findings);

        // I finding gia' presenti sono quelli deterministici: servono a non duplicare cio' che e'
        // gia' stato riconosciuto con certezza.
        List<PrivacyFinding> deterministic = [.. findings];
        List<PrivacyFinding> produced = [];
        DataClass aggregate = DataClass.Public;
        TimeSpan spent = TimeSpan.Zero;
        TimeSpan spentOnAttachments = TimeSpan.Zero;
        bool first = true;
        bool firstAttachment = true;
        IReadOnlyList<(int Start, int Length)> regions = segments ?? [(0, text.Length)];

        for (int index = 0; index < regions.Count; index++)
        {
            (int Start, int Length) region = regions[index];
            bool attachment = attachments?.Contains(index) == true;
            if (region.Length <= 0 || region.Start < 0 || region.Start + region.Length > text.Length)
            {
                continue;
            }

            string part = text.Substring(region.Start, region.Length);
            string? key = segments is null ? null : KeyOf(part);

            if (key is null || !remembered.TryGetValue(key, out PiiEntity[]? entities))
            {
                List<PiiEntity> scanned = [];

                foreach ((int Start, int Length) window in Windows(part))
                {
                    // Ogni budget protegge la prima finestra del suo tipo: interrompere dopo averla pagata non
                    // restituirebbe tempo a nessuno.
                    if (attachment
                        ? !firstAttachment && attachmentBudget > TimeSpan.Zero && spentOnAttachments >= attachmentBudget
                        : !first && budget > TimeSpan.Zero && spent >= budget)
                    {
                        throw new TimeoutException(
                            "The local PII model did not finish the surface within the declared latency budget.");
                    }

                    long windowStart = Stopwatch.GetTimestamp();

                    foreach (PiiEntity entity in ScanWindow(part.Substring(window.Start, window.Length), MaxSplitDepth))
                    {
                        scanned.Add(entity with { Start = window.Start + entity.Start });
                    }

                    if (attachment) { spentOnAttachments += Stopwatch.GetElapsedTime(windowStart); firstAttachment = false; }
                    else { spent += Stopwatch.GetElapsedTime(windowStart); first = false; }
                }

                entities = [.. scanned];

                // Si ricorda solo un pezzo esaminato per intero: un'eccezione qui sopra non arriva fin qui.
                if (key is not null)
                {
                    if (remembered.Count >= MaxRemembered) { remembered.Clear(); }
                    remembered[key] = entities;
                }
            }

            // Una citta' da sola e' un luogo: conta come indirizzo solo se lo stesso pezzo porta anche la via, il civico,
            // il CAP o i dati catastali (PiiLabels.IsPlace).
            bool streetLevel = entities.Any(entity => entity.Confidence >= MinimumConfidence && PiiLabels.IsStreetLevel(entity.Label));

            foreach (PiiEntity entity in entities)
            {
                if (entity.Confidence < MinimumConfidence)
                {
                    continue;
                }

                if (PiiLabels.IsPlace(entity.Label) && !streetLevel)
                {
                    continue;
                }

                if (PiiLabels.Translate(entity.Label) is not FindingType type)
                {
                    continue;
                }

                int start = region.Start + entity.Start;

                if (entity.Length <= 0 || start < 0 || start + entity.Length > text.Length)
                {
                    continue;
                }

                int startByte = offsets.ByteOffset(start);
                int byteLength = offsets.ByteLength(start, entity.Length);

                if (Overlaps(deterministic, type, startByte, byteLength)
                    || Overlaps(produced, type, startByte, byteLength))
                {
                    // Gia' visto: dal detector deterministico, oppure dalla finestra precedente
                    // nella parte sovrapposta.
                    continue;
                }

                produced.Add(new PrivacyFinding(type, startByte, byteLength, entity.Confidence, Version));
                aggregate = DataClasses.Max(aggregate, PiiLabels.ClassOf(type));
            }
        }

        findings.AddRange(produced);

        return aggregate;
    }

    /// <summary>Impronta di un pezzo: hash del testo, che non si puo' rileggere.</summary>
    private static string KeyOf(string part) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(part)));

    /// <summary>
    /// Finestre di caratteri che coprono il testo per intero, sovrapposte di
    /// <see cref="OverlapChars"/> caratteri e tagliate su uno spazio quando ce n'e' uno: spezzare
    /// una parola a meta' cambierebbe la tokenizzazione di entrambe le finestre.
    /// </summary>
    private IEnumerable<(int Start, int Length)> Windows(string text)
    {
        if (text.Length <= windowChars)
        {
            if (text.Length > 0)
            {
                yield return (0, text.Length);
            }

            yield break;
        }

        int start = 0;

        while (start < text.Length)
        {
            int end = Math.Min(text.Length, start + windowChars);

            if (end < text.Length)
            {
                int boundary = LastBoundary(text, start, end);

                if (boundary > start)
                {
                    end = boundary;
                }
            }

            yield return (start, end - start);

            if (end >= text.Length)
            {
                yield break;
            }

            start = Math.Max(start + 1, end - OverlapChars);
        }
    }

    /// <summary>Ultimo spazio della porzione indicata, o zero se non ne contiene.</summary>
    private static int LastBoundary(string text, int start, int end)
    {
        for (int index = end - 1; index > start; index--)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                return index;
            }
        }

        return 0;
    }

    /// <summary>
    /// Esamina una porzione. Se il modello dichiara di averla troncata, la porzione viene divisa e
    /// riesaminata: un risultato parziale verrebbe scambiato per un'assenza di dati personali.
    /// </summary>
    private IReadOnlyList<PiiEntity> ScanWindow(string window, int depth)
    {
        PiiScanResult result = model.Scan(window);

        if (!result.Truncated)
        {
            return result.Entities;
        }

        if (depth == 0 || window.Length < 2)
        {
            throw new InvalidOperationException(
                "The local PII model truncated a window that cannot be split further.");
        }

        int middle = window.Length / 2;
        int boundary = LastBoundary(window, 0, middle + 1);

        if (boundary <= 0)
        {
            boundary = middle;
        }

        List<PiiEntity> entities = [.. ScanWindow(window[..boundary], depth - 1)];

        foreach (PiiEntity entity in ScanWindow(window[boundary..], depth - 1))
        {
            entities.Add(entity with { Start = entity.Start + boundary });
        }

        return entities;
    }

    private static bool Overlaps(IReadOnlyList<PrivacyFinding> existing, FindingType type, int startByte, int byteLength)
    {
        foreach (PrivacyFinding finding in existing)
        {
            if (finding.Type != type)
            {
                continue;
            }

            if (startByte < finding.StartByte + finding.ByteLength && finding.StartByte < startByte + byteLength)
            {
                return true;
            }
        }

        return false;
    }
}
