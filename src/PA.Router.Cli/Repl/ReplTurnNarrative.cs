using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Output;
using PA.Router.Routing;

namespace PA.Router.Cli.Repl;

/// <summary>
/// Che cosa serve per raccontare un turno: la classificazione del testo, le fasi dichiarate dal motore e
/// cio' che il turno ha restituito.
/// </summary>
/// <param name="Classification">Classificazione del piano, con i segnali che dicono chi ha deciso.</param>
/// <param name="Phases">Fasi dichiarate dal motore, con esito e durata.</param>
/// <param name="Usage">Consumo dichiarato dal servizio, se lo ha dichiarato.</param>
/// <param name="MemoryInjected">Ricordi entrati nel contesto.</param>
/// <param name="MemoryBytes">Byte di quei ricordi.</param>
/// <param name="HistoryTurns">
/// Turni precedenti che la configurazione rimanda al massimo. Serve solo se il motore non ha detto quanti ne sono
/// partiti davvero.
/// </param>
/// <param name="Elapsed">Durata del turno vista da questo terminale, dalla partenza della richiesta.</param>
/// <param name="Classifying">Tempo speso a classificare la richiesta prima del turno.</param>
internal sealed record TurnStory(
    RequestClassification? Classification,
    IReadOnlyList<(string Phase, string Outcome, TimeSpan Elapsed)> Phases,
    UsageReport? Usage,
    int MemoryInjected,
    int MemoryBytes,
    int HistoryTurns,
    TimeSpan Elapsed,
    TimeSpan Classifying = default);

/// <summary>
/// Quello che il racconto chiede al registro: il nome di un servizio, il nome del modello di un rilevatore, se un
/// servizio risponde alle richieste, e da dove viene un valore di calibrazione.
/// </summary>
/// <param name="Named">Nome leggibile di un servizio dal suo identificatore.</param>
/// <param name="Detector">Nome del modello di un rilevatore dalla sua versione; null per i deterministici.</param>
/// <param name="Answers">Vero se il servizio risponde alle richieste; falso per embedding e rilevatori.</param>
/// <param name="Source">Origine di una componente di calibrazione («latency», «cost») di un servizio; null se assente.</param>
internal sealed record StoryLookups(
    Func<string, string> Named,
    Func<string, string?> Detector,
    Func<string, bool>? Answers = null,
    Func<string, string, CalibrationSource?>? Source = null);

/// <summary>
/// Il turno raccontato, per il livello verboso (S117).
///
/// Per ogni decisione dice **chi** l'ha presa e **perche'**: quale rilevatore ha trovato che cosa e dove, se il tipo
/// di richiesta l'ha deciso il modello del classificatore o le regole e per quale motivo, da che cosa viene la
/// difficolta', chi e' rimasto fuori, perche' ha risposto quel servizio, dove se n'e' andato il tempo. I modelli si
/// chiamano per nome.
///
/// E' fatto a **sezioni**: un'intestazione con il valore che conta, poi punti brevi, e ogni fatto compare una volta.
/// Un voto si dice sempre in decimi, come l'operatore lo scrive: nello stesso racconto «5 su 10» e «0.6» costringevano
/// a convertire a mente (S116).
///
/// E' una **resa**: i numeri li ha calcolati il motore e qui si mettono in frase, senza ricalcolarli (INV-22).
/// Non contiene mai il testo della richiesta: di un dato trovato si dice il tipo, non il valore.
/// </summary>
internal static class ReplTurnNarrative
{
    /// <summary>Sotto questo tempo il lavoro del router non si racconta: non e' li' che si aspetta.</summary>
    private static readonly TimeSpan RouterWorthTelling = TimeSpan.FromSeconds(1);

    /// <summary>Sotto questo tempo il caricamento del modello non si nomina.</summary>
    private const long LoadWorthTellingMs = 500;

    private static string M(string value) => ReplStory.Mark(value);

    /// <summary>Le sezioni del racconto, con i soli nomi: i servizi valgono tutti come servizi che rispondono.</summary>
    internal static IReadOnlyList<StorySection> Sections(
        TurnStory story,
        Func<string, string> named,
        Func<string, string?> detector) =>
        Sections(story, new StoryLookups(named, detector));

    /// <summary>Le sezioni del racconto, nell'ordine in cui il turno le ha attraversate.</summary>
    internal static IReadOnlyList<StorySection> Sections(TurnStory story, StoryLookups lookups)
    {
        ArgumentNullException.ThrowIfNull(story);
        ArgumentNullException.ThrowIfNull(lookups);

        List<StorySection> sections = [];

        IReadOnlyDictionary<string, string> analysis = Phase(story, "Analisi locale");
        IReadOnlyDictionary<string, string> policy = Phase(story, "Policy");
        IReadOnlyDictionary<string, string> target = Phase(story, "Selezione target");

        if (analysis.Count > 0) { sections.Add(Data(analysis, lookups.Detector)); }

        if (story.Classification is RequestClassification classification) { sections.Add(Kind(classification)); }
        IReadOnlyDictionary<string, string> demand = Phase(story, "Profilo di domanda");

        // Con il profilo di domanda che decide (ADR-031) la lunghezza fissa solo lo spazio: la sezione lo dice, e il livello
        // richiesto sta nella sezione del profilo.
        bool decisive = demand.ContainsKey("decisive");

        if (analysis.ContainsKey("difficulty")) { sections.Add(decisive ? Space(analysis, policy) : Difficulty(analysis, policy)); }
        if (demand.Count > 0) { sections.Add(Demand(demand, lookups.Named)); }
        if (policy.Count > 0) { sections.Add(Services(policy, lookups)); }
        if (target.Count > 0) { sections.Add(Answer(target, lookups)); }

        IReadOnlyDictionary<string, string> sufficient = Phase(story, "Minimo sufficiente");
        if (sufficient.Count > 0) { sections.Add(Sufficient(sufficient, lookups.Named)); }

        sections.Add(Context(story, analysis));
        sections.Add(Outcome(story, lookups.Named));

        return sections;
    }

    private static IReadOnlyDictionary<string, string> Phase(TurnStory story, string name) =>
        story.Phases.LastOrDefault(phase => phase.Phase == name) is { Phase: not null } found
            ? ReplTurnObserver.Values(found.Outcome)
            : new Dictionary<string, string>();

    /// <summary>Un decimo di voto: il motore lavora da zero a uno, l'operatore da zero a dieci.</summary>
    private static string Tenths(string value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, Formats.Invariant, out double parsed)
            ? (parsed * 10).ToString("0.#", Formats.Invariant)
            : value;

    private static string Percent(string weight) =>
        double.TryParse(weight, System.Globalization.NumberStyles.Float, Formats.Invariant, out double parsed)
            ? (parsed * 100).ToString("0", Formats.Invariant)
            : weight;

    /// <summary>Un ritrovamento come lo scrive il motore: «versione:tipo[!]@provenienza*quanti».</summary>
    private readonly record struct Found(string Version, string Type, bool Invalid, string Source, int Count);

    private static Found[] Parse(string? found) =>
        [.. (found ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts =>
            {
                string[] counted = parts[1].Split('*', 2);
                int count = counted.Length == 2 && int.TryParse(counted[1], System.Globalization.NumberStyles.Integer, Formats.Invariant, out int parsed) ? parsed : 1;
                string[] placed = counted[0].Split('@', 2);
                string type = placed[0];
                bool invalid = type.EndsWith('!');

                return new Found(parts[0], invalid ? type[..^1] : type, invalid, placed.Length == 2 ? placed[1] : "request", count);
            })];

    /// <summary>La classe dei dati: chi ha guardato il testo, che cosa ha trovato e dove, e il minimo del profilo.</summary>
    private static StorySection Data(IReadOnlyDictionary<string, string> values, Func<string, string?> detector)
    {
        List<string> lines = [];
        Found[] found = Parse(values.GetValueOrDefault("found"));

        string? modelVersion = (values.GetValueOrDefault("detectors") ?? string.Empty).Split('+').Skip(1).FirstOrDefault();

        // Per provenienza, nell'ordine in cui il payload le mette: prima la richiesta di adesso, poi cio' che
        // riparte con lei. Chi legge deve vedere subito se il dato e' nella domanda che ha appena scritto.
        string Things(IEnumerable<Found> items) => string.Join("; ", items
            .GroupBy(item => item.Source)
            .OrderBy(group => SourceOrder(group.Key))
            .Select(group => Ui.Text("narr.origin." + group.Key) + " " + M(string.Join(", ", group.Select(item =>
                Ui.Text("finding." + item.Type)
                + (item.Count > 1 ? " ×" + item.Count.ToString(Formats.Invariant) : string.Empty)
                + (item.Invalid ? " (" + Ui.Text("narr.data.invalid") + ")" : string.Empty))))));

        if (modelVersion is not null)
        {
            string name = M(detector(modelVersion) ?? modelVersion);
            Found[] byModel = [.. found.Where(item => detector(item.Version) is not null)];
            lines.Add(byModel.Length > 0
                ? Ui.Format("narr.data.modelFound {0} {1}", name, Things(byModel))
                : Ui.Format("narr.data.modelNothing {0}", name));
        }

        Found[] deterministic = [.. found.Where(item => detector(item.Version) is null)];
        lines.Add(deterministic.Length > 0
            ? Ui.Format("narr.data.detFound {0}", Things(deterministic))
            : Ui.Text("narr.data.detNothing"));

        if (modelVersion is null) { lines.Add(Ui.Text("narr.data.noModel")); }

        // Il caso che confondeva: nulla nella domanda di adesso, ma dati trovati in cio' che riparte con lei.
        if (found.Length > 0 && found.All(item => item.Source != "request")) { lines.Add(Ui.Text("narr.data.notInRequest")); }

        if (found.Any(item => item.Invalid)) { lines.Add(Ui.Text("narr.data.invalidNote")); }

        if (values.GetValueOrDefault("grade") is string grade) { lines.Add(Ui.Text("narr.data.grade." + grade)); }

        if (values.GetValueOrDefault("declared") is string declared)
        {
            lines.Add(Ui.Format("narr.data.declared {0}", M(Ui.Text("class." + declared))));
        }

        // L'analizzatore puo' solo restringere: se ha visto un dato dell'art. 9 riferito a una persona, lo si dice qui.
        if (values.GetValueOrDefault("analyzer") is string raised)
        {
            lines.Add(Ui.Format("narr.data.analyzer {0}", M(Ui.Text("class." + raised))));
        }

        if (values.GetValueOrDefault("floor") is string floor && floor != DataClass.Public.ToWire())
        {
            lines.Add(Ui.Format("narr.data.floor {0}", M(Ui.Text("class." + floor))));
        }

        if (values.GetValueOrDefault("incomplete") is string incomplete)
        {
            lines.Add(Ui.Format("narr.data.incomplete {0}", M(incomplete.Replace(",", ", ", StringComparison.Ordinal))));
        }

        lines.Add(Ui.Text("narr.data.rule"));

        return new(Ui.Text("narr.sec.data"), Ui.Text("class." + values.GetValueOrDefault("class", "unknown")), lines);
    }

    private static int SourceOrder(string source) => source switch
    {
        "request" => 0,
        "history" => 1,
        "memory" => 2,
        "attachment" => 3,
        _ => 4,
    };

    /// <summary>
    /// Il tipo di richiesta e che cosa hanno visto le regole. Dal 24 settembre 2026 (ADR-032, fase 2) decidono solo le regole:
    /// il classificatore a embedding non esiste piu'.
    /// </summary>
    private static StorySection Kind(RequestClassification classification)
    {
        // Che cosa hanno visto le regole: parole di elenchi chiusi del router, mai testo libero della richiesta.
        string[] seen = [.. classification.Signals.Select(Signal).OfType<string>()];

        List<string> lines =
        [
            Ui.Text("narr.ability.rules"),
            seen.Length > 0
                ? Ui.Format("narr.rules.sawSentence {0}", string.Join(", ", seen))
                : Ui.Text("narr.rules.noneSentence"),
        ];

        return new(Ui.Text("narr.sec.kind"), Ui.Text("narr.kind." + classification.Intent.ToWire()), lines);
    }

    private static string? Signal(string signal)
    {
        string[] parts = signal.Split(':', 2);
        string value = parts.Length == 2 ? M(parts[1]) : string.Empty;

        return parts[0] switch
        {
            "verbo" => Ui.Format("narr.signal.verb {0}", value),
            "oggetto" => Ui.Format("narr.signal.object {0}", value),
            "comando" => Ui.Format("narr.signal.command {0}", value),
            "percorso" => Ui.Text("narr.signal.path"),
            "blocco-di-codice" => Ui.Text("narr.signal.codeBlock"),
            "ambito-esteso" => Ui.Text("narr.signal.wide"),
            _ => null,
        };
    }

    private static class DifficultyBands
    {
        internal static int Rank(string wire) => wire switch { "trivial" => 0, "standard" => 1, _ => 2 };
    }

    /// <summary>
    /// Lo spazio che serve (ADR-031): con il profilo di domanda decisivo la lunghezza non dice piu' quanto e' difficile una
    /// domanda, dice solo quale finestra serve per contenerla con il suo contesto.
    /// </summary>
    private static StorySection Space(IReadOnlyDictionary<string, string> analysis, IReadOnlyDictionary<string, string> policy)
    {
        List<string> lines =
        [
            Ui.Format("narr.space.size {0} {1}", M(analysis.GetValueOrDefault("tokens", "?")), M(analysis.GetValueOrDefault("totalTokens", analysis.GetValueOrDefault("tokens", "?")))),
        ];

        string window = policy.GetValueOrDefault("minContext", "?");
        lines.Add(Ui.Format("narr.space.window {0}", M(window)));

        return new(Ui.Text("narr.sec.space"), Ui.Format("narr.space.value {0}", window), lines);
    }

    /// <summary>La difficolta': le voci che il motore confronta, e che cosa pretende quella che vince.</summary>
    private static StorySection Difficulty(IReadOnlyDictionary<string, string> analysis, IReadOnlyDictionary<string, string> policy)
    {
        List<string> lines = [];
        string Band(string key) => M(Ui.Text("band." + analysis.GetValueOrDefault(key, "trivial")));

        if (analysis.ContainsKey("tokens"))
        {
            // Il motore stima un token per byte, per prudenza: si dice che cosa conta davvero, senza chiamarli token.
            lines.Add(Ui.Format("narr.band.size {0} {1} {2} {3}", M(analysis["tokens"]), Band("sizeBand"),
                RequestAnalyzer.TrivialTokens.ToString(Formats.Invariant), RequestAnalyzer.StandardTokens.ToString(Formats.Invariant)));

            int voices = 1;

            if (analysis.GetValueOrDefault("rulesFloor") is string floor && floor != DifficultyBand.Trivial.ToWire())
            {
                lines.Add(Ui.Format("narr.band.rules {0}", Band("rulesFloor")));
                voices++;
            }

            if (analysis.GetValueOrDefault("write") == "1") { lines.Add(Ui.Text("narr.band.write")); voices++; }

            // «Vale la piu' alta» ha senso solo se le voci sono piu' d'una.
            if (voices > 1) { lines.Add(Ui.Format("narr.band.final {0}", Band("difficulty"))); }

            // Il contesto non rende la domanda piu' difficile (analysis-v3): pretende solo la finestra, e lo si dice quando
            // e' lui a deciderla.
            if (analysis.GetValueOrDefault("totalTokens") is string total
                && int.TryParse(analysis.GetValueOrDefault("windowTokens"), System.Globalization.NumberStyles.Integer, Formats.Invariant, out int needed)
                && int.TryParse(analysis.GetValueOrDefault("bandWindow"), System.Globalization.NumberStyles.Integer, Formats.Invariant, out int bandWindow)
                && needed > bandWindow)
            {
                lines.Add(Ui.Format("narr.band.window {0} {1}", M(total), M(analysis["windowTokens"])));
            }
        }

        // Dal 24 settembre 2026 (ADR-032, fase 3) la difficolta' non pretende un voto: resta la finestra.
        if (policy.GetValueOrDefault("minContext") is string window)
        {
            lines.Add(Ui.Format("narr.band.needsWindow {0}", M(window)));
        }

        return new(Ui.Text("narr.sec.difficulty"), Ui.Text("band." + analysis.GetValueOrDefault("difficulty", "trivial")), lines);
    }


    /// <summary>
    /// Il profilo di domanda in ombra (ADR-025, ADR-026): che cosa ha visto il pre-check, che cosa ha detto
    /// l'analizzatore, che profilo ne segue — e che oggi decide ancora la banda.
    /// </summary>
    private static StorySection Demand(IReadOnlyDictionary<string, string> values, Func<string, string> named)
    {
        List<string> lines = [];
        string Level(string key) => M(values.GetValueOrDefault(key, "?"));

        lines.Add(values.GetValueOrDefault("precheck") switch
        {
            "trivial" => Ui.Text("narr.demand.trivial"),
            "forced" => Ui.Format("narr.demand.forced {0}", Level("tier")),
            _ => Ui.Text("narr.demand.unknown"),
        });

        string model = values.GetValueOrDefault("service") is string service ? M(named(service)) : Ui.Text("narr.demand.analyzer");

        if (values.GetValueOrDefault("signals") is { Length: 5 } signals)
        {
            string said = Ui.Format("narr.demand.signals {0} {1} {2} {3} {4} {5}", model,
                M(Ui.Text("narr.task." + values.GetValueOrDefault("task", "qa"))),
                M(signals[0].ToString()), M(signals[1].ToString()), M(signals[2].ToString()), M(signals[3].ToString()));

            if (signals[4] == '3') { said += " " + Ui.Text("narr.demand.personal"); }
            if (values.GetValueOrDefault("confidence") is string confidence) { said += " " + Ui.Format("narr.demand.confidence {0}", M(confidence)); }

            lines.Add(said);
        }
        else if (values.GetValueOrDefault("unavailable") is string reason)
        {
            lines.Add(Ui.Format("narr.demand.unavailable {0} {1}", model,
                Ui.Contains("narr.demand.why." + reason) ? Ui.Text("narr.demand.why." + reason) : reason));
        }

        if (values.ContainsKey("raised")) { lines.Add(Ui.Text("narr.demand.raised")); }

        lines.Add(Ui.Format("narr.demand.profile {0} {1} {2} {3}", Level("tier"), Level("reasoning"), Level("coding"), Level("context")));

        if (values.ContainsKey("decisive"))
        {
            lines.Add(Ui.Text("narr.demand.decides"));
        }
        else if (values.GetValueOrDefault("bandToday") is string band)
        {
            int today = band switch { "trivial" => 1, "standard" => 2, _ => 3 };
            int tier = int.TryParse(values.GetValueOrDefault("tier"), System.Globalization.NumberStyles.Integer, Formats.Invariant, out int parsed) ? parsed : today;
            string compare = tier == today ? "narr.demand.same {0}" : tier > today ? "narr.demand.higher {0}" : "narr.demand.lower {0}";
            lines.Add(Ui.Format(compare, M(Ui.Text("band." + band))));
        }

        return new(Ui.Text(values.ContainsKey("decisive") ? "narr.sec.demandDecisive" : "narr.sec.demand"),
            Ui.Format("narr.demand.value {0}", values.GetValueOrDefault("tier", "?")), lines);
    }

    /// <summary>Quanti servizi che rispondono il profilo ammette, e perche' gli altri sono rimasti fuori.</summary>
    private static StorySection Services(IReadOnlyDictionary<string, string> policy, StoryLookups lookups)
    {
        Func<string, bool> answers = lookups.Answers ?? (_ => true);
        Func<string, string> named = lookups.Named;

        var excluded = (policy.GetValueOrDefault("excluded") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => (Service: parts[0], Reasons: parts[1].Split('+', StringSplitOptions.RemoveEmptyEntries)))
            .ToArray();

        // Embedding e rilevatori lavorano per il router e non rispondono a nessuno: sono fuori dalla gara per
        // costruzione, e i motivi di policy che raccolgono — finestra troppo piccola per un rilevatore di dati
        // personali — sono veri e inutili (S116). Non si contano fra i servizi e si dicono in una riga.
        var auxiliary = excluded.Where(item => !answers(item.Service)).ToArray();
        var competing = excluded.Where(item => answers(item.Service)).ToArray();

        // Anche fra gli ammessi possono esserci servizi che lavorano per il router — l'analizzatore, un embedding: la policy
        // li ammette, ma non rispondono. Contarli e nominarli fra gli ammessi faceva comparire lo stesso modello due volte.
        string[] admittedIds = (policy.GetValueOrDefault("admitted") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
        string[] admittedAnswering = [.. admittedIds.Where(answers)];
        int helpersAdmitted = admittedIds.Length - admittedAnswering.Length;

        string admissible = policy.ContainsKey("admitted")
            ? admittedAnswering.Length.ToString(Formats.Invariant)
            : policy.GetValueOrDefault("admissible", "0");
        string total = int.TryParse(policy.GetValueOrDefault("total"), System.Globalization.NumberStyles.Integer, Formats.Invariant, out int all)
            ? (all - auxiliary.Length - helpersAdmitted).ToString(Formats.Invariant)
            : "?";
        string value = admissible switch
        {
            "0" => Ui.Format("narr.admit.none {0}", total),
            "1" => Ui.Format("narr.admit.one {0}", total),
            _ => Ui.Format("narr.admit.many {0} {1}", admissible, total),
        };

        List<string> lines = [];

        string[] admitted = [.. admittedAnswering.Select(named).Order(StringComparer.OrdinalIgnoreCase)];

        if (admitted.Length > 0) { lines.Add(Ui.Format("narr.admitted {0}", string.Join(", ", admitted.Select(M)))); }

        // Con nessun servizio ammesso il rifiuto qui sopra ha gia' detto ogni esclusione con i suoi numeri e il
        // rimedio: ripeterle sarebbe la stessa cosa due volte.
        if (admissible == "0")
        {
            lines.Add(Ui.Text("narr.seeDenial"));
            return new(Ui.Text("narr.sec.services"), value, lines);
        }

        foreach (var item in competing.Where(item => !item.Reasons.Contains("TARGET_DISABLED"))
            .OrderBy(item => named(item.Service), StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(Ui.Format("narr.excluded {0} {1}", M(named(item.Service)),
                string.Join("; ", item.Reasons.Select(reason => Ui.Contains("narr.why." + reason) ? Ui.Text("narr.why." + reason) : reason))));
        }

        // Gli spenti in una frase sola: non sono stati esclusi da una regola, non sono mai entrati in gara.
        string[] disabled = [.. competing.Where(item => item.Reasons.Contains("TARGET_DISABLED")).Select(item => named(item.Service))
            .Order(StringComparer.OrdinalIgnoreCase)];

        if (disabled.Length > 0) { lines.Add(Ui.Format("narr.disabled {0}", string.Join(", ", disabled))); }

        string[] helpers = [.. auxiliary.Where(item => !item.Reasons.Contains("TARGET_DISABLED")).Select(item => item.Service)
            .Concat(admittedIds.Where(id => !answers(id)))
            .Distinct(StringComparer.Ordinal)
            .Select(named)
            .Order(StringComparer.OrdinalIgnoreCase)];

        if (helpers.Length > 0) { lines.Add(Ui.Format("narr.auxiliary {0}", string.Join(", ", helpers))); }

        return new(Ui.Text("narr.sec.services"), value, lines);
    }

    /// <summary>Chi ha risposto e perche' lui: il punteggio in decimi, ogni componente con peso e origine.</summary>
    private static StorySection Answer(IReadOnlyDictionary<string, string> values, StoryLookups lookups)
    {
        Func<string, string> named = lookups.Named;

        if (values.GetValueOrDefault("service") is not string service || service == "-")
        {
            // Dopo un guasto il secondo giro non trova nessun altro: si dice chi era stato scelto e non ha risposto.
            string[] tried = [.. (values.GetValueOrDefault("tried") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(named)];

            return tried.Length > 0
                ? new(Ui.Text("narr.sec.answer"), Ui.Text("narr.noAnswer"), [Ui.Format("narr.triedFailed {0}", string.Join(", ", tried.Select(M)))])
                : new(Ui.Text("narr.sec.answer"), Ui.Text("narr.noAnswer"), [Ui.Text("turn.noTarget")]);
        }

        HashSet<string> neutral = [.. (values.GetValueOrDefault("neutral") ?? string.Empty).Split('+', StringSplitOptions.RemoveEmptyEntries)];

        // Da dove viene ogni numero: un 5 che nasce da un'assenza non e' una preferenza, e un valore scritto nel
        // registro non e' una misura. La qualita' viene dal posto nell'ordine dei modelli (ADR-032, fase 3).
        string Origin(string component) => component switch
        {
            _ when neutral.Contains(component) => Ui.Text(component == "quality" ? "narr.src.noOrder" : "narr.src.neutral"),
            "quality" => Ui.Text("narr.src.order"),
            _ => lookups.Source?.Invoke(service, component) switch
            {
                CalibrationSource.Measured => Ui.Text("narr.src.measured"),
                CalibrationSource.Declared => Ui.Text("narr.src.declared"),
                _ => Ui.Text("narr.src.given"),
            },
        };

        string Component(string component, string weight) => Ui.Format(
            "narr.comp.item {0} {1} {2} {3}",
            Ui.Text("narr.comp." + component),
            M(Tenths(values.GetValueOrDefault(component, "?"))),
            Percent(values.GetValueOrDefault(weight, "?")),
            Origin(component));

        List<string> lines =
        [
            Ui.Format("narr.score2 {0} {1}", M(Tenths(values.GetValueOrDefault("score", "?"))), string.Join(" · ", new[]
            {
                Component("quality", "wQuality"),
                Component("latency", "wLatency"),
                Component("cost", "wCost"),
                Component("quota", "wQuota"),
            })),
        ];

        string[] others = (values.GetValueOrDefault("next") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);

        if (others.Length == 0) { lines.Add(Ui.Text("narr.onlyOne")); }
        else
        {
            string[] first = others[0].Split(':', 2);
            lines.Add(Ui.Format("narr.runnerUp {0} {1}", M(named(first[0])), M(first.Length > 1 ? Tenths(first[1]) : "?")));

            if (others.Length > 1)
            {
                lines.Add(Ui.Format("narr.rest {0}", string.Join(", ", others.Skip(1).Select(item =>
                {
                    string[] parts = item.Split(':', 2);
                    return named(parts[0]) + " " + (parts.Length > 1 ? Tenths(parts[1]) : "?");
                }))));
            }
        }

        if (values.ContainsKey("tie")) { lines.Add(Ui.Text("turn.tie")); }

        return new(Ui.Text("narr.sec.answer"), named(service), lines);
    }

    /// <summary>
    /// Il minimo sufficiente in ombra (ADR-027): chi sceglierebbe fra gli ammessi abbastanza capaci, e che cosa manca agli
    /// altri — accanto a chi risponde oggi.
    /// </summary>
    private static StorySection Sufficient(IReadOnlyDictionary<string, string> values, Func<string, string> named)
    {
        List<string> lines = [];
        string service = values.GetValueOrDefault("service", "-");
        string? today = values.GetValueOrDefault("today");
        bool decisive = values.ContainsKey("decisive");

        if (decisive)
        {
            // Il profilo di domanda decide (ADR-031): chi risponde e perche', oppure che nessuno bastava.
            lines.Add(values.GetValueOrDefault("fallback") is string fallback
                ? Ui.Format("narr.min.below {0}", M(named(fallback)))
                : values.ContainsKey("continued")
                    ? Ui.Format("narr.min.continued {0}", M(named(service)))
                    : Ui.Format("narr.min.chosen {0}", M(named(service))));
        }
        else
        {
            lines.Add(service == "-"
                ? Ui.Format("narr.min.none {0}", M(today is null ? "?" : named(today)))
                : service == today
                    ? Ui.Format("narr.min.same {0}", M(named(service)))
                    : Ui.Format("narr.min.other {0} {1}", M(named(service)), M(today is null ? "?" : named(today))));
        }

        string[] compatible = (values.GetValueOrDefault("compatible") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (compatible.Length > 1) { lines.Add(Ui.Format("narr.min.compatible {0}", string.Join(", ", compatible.Select(id => named(id))))); }

        foreach (string entry in (values.GetValueOrDefault("missing") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split(':', 2);
            if (parts.Length != 2) { continue; }

            IEnumerable<string> dims = parts[1].Split('+').Select(item =>
            {
                int less = item.IndexOf('<', StringComparison.Ordinal);
                string head = less > 0 ? item[..less] : item;
                string need = less > 0 ? item[(less + 1)..] : "?";
                string name = new(head.TakeWhile(char.IsLetter).ToArray());
                string has = head[name.Length..];

                return name == "levels"
                    ? Ui.Text("narr.min.nolevels")
                    : Ui.Format("narr.min.dim {0} {1} {2}", Ui.Text("narr.dim." + name), M(has), M(need));
            });

            lines.Add(Ui.Format("narr.min.missing {0} {1}", M(named(parts[0])), string.Join("; ", dims)));
        }

        if (decisive)
        {
            string answering = values.GetValueOrDefault("fallback") ?? today ?? service;
            return new(Ui.Text("narr.sec.choice"), values.ContainsKey("below") ? Ui.Format("narr.min.belowValue {0}", named(answering)) : named(answering), lines);
        }

        return new(Ui.Text("narr.sec.sufficient"), service == "-" ? Ui.Text("narr.min.noneValue") : named(service), lines);
    }

    /// <summary>Che cosa ha accompagnato la richiesta: ricordi e turni precedenti partiti davvero.</summary>
    private static StorySection Context(TurnStory story, IReadOnlyDictionary<string, string> analysis)
    {
        string memory = story.MemoryInjected switch
        {
            0 => Ui.Text("narr.memory.none"),
            1 => Ui.Format("narr.memory.one {0}", M(story.MemoryBytes.ToString(Formats.Invariant))),
            _ => Ui.Format("narr.memory.many {0} {1}", M(story.MemoryInjected.ToString(Formats.Invariant)), M(story.MemoryBytes.ToString(Formats.Invariant))),
        };

        // I turni li conta il motore su cio' che e' partito, dopo il taglio al budget. Il limite della
        // configurazione resta solo se il motore non l'ha detto.
        string history = analysis.GetValueOrDefault("historyTurns") is string sent
            ? sent switch
            {
                "0" => Ui.Text("narr.historyNone"),
                "1" => Ui.Text("narr.historyOne"),
                _ => Ui.Format("narr.history {0}", M(sent)),
            }
            : story.HistoryTurns > 0
                ? Ui.Format("narr.historyUpTo {0}", M(story.HistoryTurns.ToString(Formats.Invariant)))
                : Ui.Text("narr.historyNone");

        return new(Ui.Text("narr.sec.context"), null, [memory, history]);
    }

    /// <summary>Com'e' finita: quanto ha lavorato il servizio, quanto il router, consumo e costo.</summary>
    private static StorySection Outcome(TurnStory story, Func<string, string> named)
    {
        List<string> lines = [];

        // Un tentativo fallito prima della risposta si dice (24 settembre 2026): a fine credito Anthropic rispondeva «quota
        // esaurita» e il turno passava a un altro servizio, ma il racconto diceva solo «il servizio ha risposto», e chi
        // leggeva non sapeva che il modello scelto non era quello che aveva risposto. A ogni tentativo il motore emette una
        // scelta e un'esecuzione, nello stesso ordine.
        string?[] chosen = [.. story.Phases.Where(phase => phase.Phase == "Selezione target")
            .Select(phase => ReplTurnObserver.Values(phase.Outcome).GetValueOrDefault("service"))];
        (string Phase, string Outcome, TimeSpan Elapsed)[] attempts = [.. story.Phases.Where(phase => phase.Phase == "Esecuzione")];

        foreach ((string Phase, string Outcome, TimeSpan Elapsed) quota in story.Phases.Where(phase => phase.Phase == "Quota"))
        {
            IReadOnlyDictionary<string, string> values = ReplTurnObserver.Values(quota.Outcome);
            lines.Add(Output.QuotaText.Paused(M(named(values.GetValueOrDefault("service", "?"))), Output.QuotaText.Until(values.GetValueOrDefault("until"))));
        }

        for (int index = 0; index < attempts.Length - 1; index++)
        {
            string code = attempts[index].Outcome.Split(' ', 2)[0];
            string http = attempts[index].Outcome.Length > code.Length ? attempts[index].Outcome[code.Length..] : string.Empty;
            string why = (Ui.Contains("narr.attempt." + code) ? Ui.Text("narr.attempt." + code) : code) + http;

            lines.Add(Ui.Format("narr.retried {0} {1} {2}",
                M(named(chosen.ElementAtOrDefault(index) ?? "?")),
                M(why),
                M(named(chosen.ElementAtOrDefault(index + 1) ?? "?"))));
        }
        TimeSpan total = story.Elapsed + story.Classifying;
        TimeSpan service = TimeSpan.Zero;

        if (story.Phases.LastOrDefault(phase => phase.Phase == "Esecuzione") is { Phase: not null } execution)
        {
            service = execution.Elapsed;
            long? load = story.Usage?.LoadMilliseconds;

            lines.Add(execution.Outcome != "ok"
                ? Ui.Format("narr.stopped {0} {1}", M(Duration(execution.Elapsed)), M(execution.Outcome))
                : load is long loading && loading >= LoadWorthTellingMs
                    ? Ui.Format("narr.doneLoad {0} {1}", M(Duration(execution.Elapsed)), M(Duration(TimeSpan.FromMilliseconds(loading))))
                    : Ui.Format("narr.done {0}", M(Duration(execution.Elapsed))));

            // Il ragionamento che il modello ha scritto senza mostrarlo: senza questa riga «ha risposto in 275 s» a un saluto
            // sembra un servizio rotto, ed e' un modello che ha pensato 1300 token prima di dire «Ciao!» (23 settembre 2026).
            // Circa quattro caratteri per token: e' una stima, e si dice tale.
            if (story.Usage?.ReasoningCharacters is long reasoning && reasoning > 0)
            {
                lines.Add(Ui.Format("narr.reasoning {0}", M((reasoning / 4).ToString(Formats.Invariant))));
            }
        }

        // Il tempo che non e' del servizio e' del router: classificazione, analisi, controllo della risposta. Si
        // dice quando pesa, con le fasi piu' lunghe: undici secondi senza un nome sembrano un servizio lento.
        TimeSpan router = total - service;

        if (router >= RouterWorthTelling)
        {
            var spent = story.Phases
                .Where(phase => phase.Phase != "Esecuzione")
                .GroupBy(phase => phase.Phase)
                .Select(group => (Name: Ui.Text(group.Key), Elapsed: TimeSpan.FromTicks(group.Sum(phase => phase.Elapsed.Ticks))))
                .Append((Name: Ui.Text("narr.phase.classification"), Elapsed: story.Classifying))
                .Where(item => item.Elapsed >= TimeSpan.FromMilliseconds(300))
                .OrderByDescending(item => item.Elapsed)
                .Take(3)
                .Select(item => item.Name + " " + M(Duration(item.Elapsed)))
                .ToArray();

            lines.Add(spent.Length > 0
                ? Ui.Format("narr.routerTime {0} {1}", M(Duration(router)), string.Join(", ", spent))
                : Ui.Format("narr.routerTimeOnly {0}", M(Duration(router))));
        }

        if (story.Usage is UsageReport usage && (usage.InputTokens is not null || usage.OutputTokens is not null))
        {
            lines.Add(Ui.Format("narr.usage {0} {1}",
                M(usage.InputTokens?.ToString(Formats.Invariant) ?? "?"), M(usage.OutputTokens?.ToString(Formats.Invariant) ?? "?"))
                + (usage.BilledCost is decimal cost ? " " + Ui.Format("narr.cost {0}", M(cost.ToString("0.####", Formats.Invariant))) : string.Empty)
                + (usage.BilledCost is null && usage.PricedCost is decimal priced
                    ? " " + Ui.Format("narr.pricedCost {0} {1}", M(priced.ToString("0.######", Formats.Invariant)), usage.Currency ?? string.Empty)
                    : string.Empty));

            if (usage.Truncated) { lines.Add(Ui.Text("narr.truncated")); }
        }
        else
        {
            lines.Add(Ui.Text("narr.noUsage"));
        }

        // Una risposta fermata dal controllo: perche', con i tipi di dato e mai il contenuto (23 settembre 2026).
        if (story.Phases.LastOrDefault(phase => phase.Phase == "Controllo risposta") is { Phase: not null } check
            && check.Outcome.StartsWith("bloccata", StringComparison.Ordinal))
        {
            Dictionary<string, string> values = check.Outcome.Split(';').Skip(1)
                .Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
            string reason = values.GetValueOrDefault("reason", "other");
            string types = string.Join(", ", (values.GetValueOrDefault("types") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(type => Ui.Contains("finding." + type) ? Ui.Text("finding." + type) : type));

            lines.Add(Ui.Format("narr.blocked." + reason + " {0}", types.Length == 0 ? "-" : types));
        }

        return new(Ui.Text("narr.sec.outcome"), Duration(total), lines);
    }

    private static string Duration(TimeSpan elapsed) => elapsed.TotalSeconds >= 1
        ? elapsed.TotalSeconds.ToString("0.0", Formats.Invariant) + " s"
        : elapsed.TotalMilliseconds.ToString("0", Formats.Invariant) + " ms";
}
