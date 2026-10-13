using System.Text;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Memory;

/// <summary>Esito della costruzione del contesto di memoria.</summary>
/// <param name="Items">Elementi da aggiungere al payload, gia' entro il budget.</param>
/// <param name="Selected">Identificatori scelti, nell'ordine di punteggio.</param>
/// <param name="Excluded">Identificatori esclusi per budget, dal punteggio piu' basso.</param>
/// <param name="UsedBytes">Byte occupati dagli elementi scelti.</param>
/// <param name="Status">Stato della memoria da mostrare a chi chiede.</param>
/// <param name="Mode">Modo con cui i ricordi sono stati trovati.</param>
public sealed record MemoryContext(
    IReadOnlyList<ContextItem> Items,
    IReadOnlyList<string> Selected,
    IReadOnlyList<string> Excluded,
    int UsedBytes,
    MemoryIndexStatus Status,
    MemorySearchMode Mode)
{
    /// <summary>Contesto vuoto, con il motivo per cui non c'e' memoria.</summary>
    public static MemoryContext Empty(MemoryIndexStatus status, MemorySearchMode mode) =>
        new([], [], [], 0, status, mode);
}

/// <summary>
/// Costruzione del contesto di memoria da iniettare in un turno (03-specs/MEMORY.md,
/// «Auto-injection e handover»).
///
/// Il budget e' 8 KiB e i ricordi in eccesso escono dal fondo della classifica: prima il punteggio
/// piu' basso, poi l'identificatore maggiore. Cio' che non viene mai tolto e' l'input dell'utente o
/// un vincolo — la memoria e' un'aggiunta, e se non entra e' la memoria a restare fuori.
///
/// Ogni elemento porta identificatore, tipo, stato di verifica e classe: chi legge deve poter dire
/// da dove viene una frase e se qualcuno l'ha verificata. Le istruzioni contenute in un vecchio
/// ricordo restano dati, non diventano policy: il testo viene marcato come provenienza memoria e
/// riattraversa la privacy insieme al resto del payload.
/// </summary>
public static class MemoryContextBuilder
{
    /// <summary>Budget dell'auto-injection.</summary>
    public const int BudgetBytes = 8 * 1024;

    /// <summary>
    /// Quanto deve valere un ricordo, rispetto al migliore, per entrare nel turno (23 settembre 2026). Nella ricerca ibrida
    /// un ricordo che solo il lessicale trova vale un ventesimo di uno che trovano i vettori
    /// (<see cref="MemoryLimits.HybridLexicalWeight"/>): e' una parola in comune, «della» o «qual». Per «qual e' la
    /// capitale della Francia?» entravano, dopo la risposta giusta, quattro saluti del modello — 600 token che su un
    /// processore costano venti secondi di lettura, e che un modello piccolo ripeteva invece di rispondere. Senza vettori
    /// le liste sono una sola e i punteggi restano vicini: la soglia non taglia.
    /// </summary>
    public const double RelevanceRatio = 0.25;

    /// <summary>
    /// Il punteggio piu' basso di un ricordo che i vettori hanno trovato: l'ultimo posto della loro lista. Nella ricerca
    /// ibrida un ricordo sotto questa soglia l'ha trovato solo il lessicale, e i vettori — che l'hanno giudicato — non lo
    /// ritenevano simile. Quando nessun ricordo e' simile i saluti trovati per «della» valgono tutti uguale, e la soglia
    /// relativa da sola li lasciava entrare.
    /// </summary>
    public static double VectorSupport => 1.0 / (MemoryLimits.FusionConstant + MemoryLimits.CandidatesPerList);

    /// <summary>
    /// Parole in comune oltre le quali un ricordo e' un doppione di uno gia' scelto (23 settembre 2026). La stessa domanda
    /// fatta in due conversazioni lascia due risposte quasi uguali, e mandarle entrambe costa token senza dire niente di
    /// nuovo. Si confrontano gli insiemi delle parole (Jaccard): nessun elenco per lingua, nessun modello.
    /// </summary>
    public const double DuplicateOverlap = 0.6;

    /// <summary>
    /// Quota delle parole del ricordo piu' corto che stanno nell'altro, oltre la quale sono doppioni anche se uno dei due ha
    /// parole in piu'. Un modello che commenta un ricordo («sembra che ne abbiamo gia' parlato, ecco la risposta: …») lascia
    /// un ricordo nuovo che contiene quello vecchio, e il confronto sul totale non lo riconosceva: i commenti si
    /// accumulavano e alimentavano altri commenti.
    /// </summary>
    public const double DuplicateContainment = 0.8;

    /// <summary>
    /// Parole minime del ricordo piu' corto perche' valga il contenimento: un «Si'.» e' contenuto in quasi tutto.
    /// </summary>
    public const int ContainmentMinimumWords = 4;

    /// <summary>
    /// Similarita' lessicale storica: non prova l'equivalenza e non governa piu' l'esclusione dei ricordi.
    /// Gli insiemi perdono ordine e ripetizioni; una negazione o un valore diverso possono superare le soglie.
    /// </summary>
    public static bool Duplicates(IReadOnlySet<string> first, IReadOnlySet<string> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (Overlap(first, second) >= DuplicateOverlap) { return true; }

        int smaller = Math.Min(first.Count, second.Count);
        if (smaller < ContainmentMinimumWords) { return false; }

        int shared = first.Count <= second.Count ? first.Count(second.Contains) : second.Count(first.Contains);
        return (double)shared / smaller >= DuplicateContainment;
    }

    /// <summary>Le parole di un testo, minuscole, senza punteggiatura: la base del confronto fra ricordi.</summary>
    public static HashSet<string> Words(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        HashSet<string> words = new(StringComparer.Ordinal);
        StringBuilder current = new();

        foreach (char character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0) { words.Add(current.ToString()); }

        return words;
    }

    /// <summary>Quota di parole in comune fra due insiemi: intersezione su unione.</summary>
    public static double Overlap(IReadOnlySet<string> first, IReadOnlySet<string> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first.Count == 0 && second.Count == 0) { return 1; }

        int shared = first.Count(second.Contains);
        return (double)shared / (first.Count + second.Count - shared);
    }

    /// <summary>
    /// Intestazione che precede ogni ricordo iniettato.
    ///
    /// Dice per prima cosa **che cos'e'**: una risposta data in una conversazione precedente, non una
    /// consegna e non un testo da commentare. Fino al 23 settembre 2026 diceva «appunto», e un modello
    /// piccolo commentava l'appunto («sembra che tu abbia gia' provato l'appunto su questo tema»): il
    /// ricordo contiene solo la risposta di allora, e chiamarlo per quello che e' non lascia niente da
    /// commentare. L'etichetta sola — identificatore, tipo, verifica, classe, data
    /// — la legge un programma, non un modello piccolo: quello riceveva un blocco marcato e lo
    /// trattava come il compito, e rispondeva «non ho ricevuto il testo che hai fornito come
    /// memoria» invece di rispondere alla domanda. La frase non e' un'istruzione e non concede
    /// niente: e' l'etichetta del dato, e la richiesta resta l'ultima cosa del payload (S15).
    /// </summary>
    public static string Header(ObservationRecord observation, Verification verification) =>
        "[risposta data in una conversazione precedente, non una richiesta | " + observation.Id + " | " + observation.Kind.ToWire() + " | " + verification.ToWire()
        + " | " + observation.Classification.ToWire() + " | "
        + observation.CreatedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "]\n";

    /// <summary>
    /// Costruisce gli elementi di contesto dai risultati gia' autorizzati.
    /// </summary>
    /// <param name="result">Esito della ricerca.</param>
    /// <param name="bodies">Corpi autorizzati, riletti dopo il punteggio.</param>
    /// <param name="topK">Numero massimo di ricordi da iniettare.</param>
    /// <param name="budgetBytes">Budget in byte; il default e' quello della specifica.</param>
    /// <param name="alreadyInContext">
    /// Esecuzioni i cui turni stanno gia' nel payload come cronologia. Un ricordo nato da uno di
    /// quei turni arriverebbe **due volte**: una come turno e una come «memoria». Non aggiunge
    /// niente, costa token, e a un modello piccolo fa credere che gli sia stato consegnato un
    /// documento da commentare — e' cosi' che rispondeva «non ho ricevuto il testo che hai fornito
    /// come memoria» invece di rispondere alla domanda.
    /// </param>
    public static MemoryContext Build(
        MemorySearchResult result,
        IReadOnlyList<ObservationRecord> bodies,
        int topK,
        int budgetBytes = BudgetBytes,
        IReadOnlySet<string>? alreadyInContext = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(bodies);

        if (topK < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topK));
        }

        Dictionary<string, ObservationRecord> byId = new(StringComparer.Ordinal);

        foreach (ObservationRecord body in bodies)
        {
            byId[body.Id] = body;
        }

        List<ContextItem> items = [];
        List<(string Statement, ObservationKind Kind, Verification Verification, DataClass Classification)> chosen = [];
        List<string> selected = [];
        List<string> excluded = [];
        int used = 0;
        double floor = Math.Max(
            result.Hits.Count > 0 ? result.Hits.Max(hit => hit.Score) * RelevanceRatio : 0,
            result.Mode is MemorySearchMode.Hybrid ? VectorSupport : 0);

        foreach (MemoryHit hit in result.Hits)
        {
            if (!byId.TryGetValue(hit.ObservationId, out ObservationRecord? observation))
            {
                // Trovato dal punteggio ma assente dalla rilettura autorizzata: non entra e non
                // viene nemmeno riportato fra gli esclusi, qualunque sia il punteggio o topK.
                continue;
            }

            if (selected.Count >= topK)
            {
                excluded.Add(hit.ObservationId);
                continue;
            }

            if (hit.Score < floor)
            {
                // Troppo lontano dal migliore: una parola in comune, non un ricordo che serve.
                excluded.Add(hit.ObservationId);
                continue;
            }

            if (observation.ExecutionId is string origin && alreadyInContext?.Contains(origin) == true)
            {
                // Gia' nel payload come turno della conversazione: ripeterlo non e' ricordare.
                continue;
            }

            // Al modello va l'affermazione osservata; tipo e identificatore stanno nell'intestazione.
            string statement = ObservationFactory.Statement(observation.Content);
            string text = Header(observation, hit.Verification) + statement;
            int size = Encoding.UTF8.GetByteCount(text);
            bool duplicate = chosen.Contains((statement, observation.Kind, hit.Verification, observation.Classification));

            if (duplicate)
            {
                // Identita' ordinale dello statement e stessi attributi: resta la prima fonte in classifica. Maiuscole
                // e spazi possono essere significativi nel codice. Un testo piu' corto o contenuto nell'altro non prova
                // equivalenza e non puo' cancellarne condizioni o negazioni.
                excluded.Add(observation.Id);
                continue;
            }

            if (used + size > budgetBytes)
            {
                excluded.Add(observation.Id);
                continue;
            }

            used += size;
            selected.Add(observation.Id);
            chosen.Add((statement, observation.Kind, hit.Verification, observation.Classification));

            items.Add(new ContextItem(
                observation.Id,
                text,
                Provenance.Memory,
                observation.Classification));
        }

        return new MemoryContext(items, selected, excluded, used, result.IndexStatus, result.Mode);
    }
}
