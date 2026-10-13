using System.Text;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Routing;

/// <summary>
/// Profilo di una richiesta, prodotto dall'analisi locale.
/// </summary>
/// <param name="Version">Versione dell'analisi, registrata nell'explain come quella del ranking.</param>
/// <param name="PayloadBytes">Byte UTF-8 dell'input piu' il contesto allegato.</param>
/// <param name="EstimatedTokens">Token stimati con la regola conservativa del prodotto.</param>
/// <param name="Band">
/// Difficolta' della **domanda**: dimensione del solo testo dell'operatore, alzata dalle regole e dalla modifica di
/// file. Non esclude nessun servizio (ADR-032, fase 3): resta per il racconto e per l'analizzatore.
/// </param>
/// <param name="Intent">Intento dedotto dalle capacita' richieste, non dal testo.</param>
/// <param name="RequiredCapabilities">Capacita' che la richiesta dichiara di volere.</param>
/// <param name="InputTokens">Token stimati del solo testo dell'operatore.</param>
/// <param name="SizeBand">Banda della dimensione totale, contesto compreso: decide solo la finestra.</param>
public sealed record RequestProfile(
    string Version,
    int PayloadBytes,
    int EstimatedTokens,
    DifficultyBand Band,
    Intent Intent,
    IReadOnlySet<Capability> RequiredCapabilities,
    int InputTokens,
    DifficultyBand SizeBand);

/// <summary>
/// Analisi locale e deterministica della richiesta (03-specs/ROUTING.md, «Analisi della richiesta»).
///
/// Non usa modelli. Introdurre un'inferenza dentro la decisione su quale modello usare creerebbe un
/// ciclo che nessun audit potrebbe spiegare, e renderebbe non riproducibile una scelta che l'audit
/// deve poter ricostruire. Due installazioni identiche, sullo stesso input, devono stimare la
/// stessa banda.
///
/// Le capacita' richieste **non** vengono indovinate dal testo: le dichiara il chiamante — il
/// comando, l'endpoint, l'operatore. Dedurre «questa frase vuole scrivere file» da una prosa
/// significherebbe promuovere una chat ad agente sulla base di una parola, ed e' esattamente il
/// genere di scorciatoia che la policy dovrebbe poi rincorrere.
/// </summary>
public static class RequestAnalyzer
{
    /// <summary>
    /// Versione dell'analisi, registrata accanto a quella del ranking. In <c>analysis-v3</c> il contesto — ricordi,
    /// cronologia, istruzioni, strumenti — non rende piu' difficile la domanda: con due ricordi un «ciao» era
    /// «normale» e pretendeva un voto di 5 su 10 (ADR-025). Il contesto continua a pretendere la finestra.
    /// <c>analysis-v4</c> (24 settembre 2026, ADR-032 fase 3): la difficolta' non pretende piu' un voto ne' una finestra
    /// per banda; la finestra e' il payload piu' la risposta, mai sotto <see cref="MinimumWindowTokens"/>.
    /// </summary>
    public const string Version = "analysis-v4";

    /// <summary>
    /// Finestra minima pretesa da qualunque richiesta, in token: la soglia che fino al 24 settembre 2026 la configurazione
    /// dichiarava per le richieste banali (<c>routing.bands.trivial.minContextTokens</c>). Una costante, non un parametro.
    /// </summary>
    public const int MinimumWindowTokens = 4096;

    /// <summary>
    /// Tetto in token della banda banale. Sotto questa soglia una richiesta non ha bisogno di
    /// qualita' particolare: e' un saluto, una domanda breve, una correzione di una riga.
    /// </summary>
    public const int TrivialTokens = 256;

    /// <summary>
    /// Tetto in token della banda ordinaria. Oltre, la richiesta porta con se' un documento o una
    /// cronologia lunga, e chiedere una finestra piu' larga smette di essere un lusso.
    /// </summary>
    public const int StandardTokens = 4096;

    /// <summary>Analizza input e contesto gia' assemblato.</summary>
    /// <param name="input">Testo dell'operatore.</param>
    /// <param name="context">Contesto allegato, gia' ridotto al budget.</param>
    /// <param name="requiredCapabilities">Capacita' dichiarate dal chiamante.</param>
    /// <param name="bandFloor">
    /// Banda minima dalla classificazione del testo (<see cref="RequestClassifier"/>): la dimensione
    /// puo' alzarla, mai abbassarla.
    /// </param>
    public static RequestProfile Analyze(
        string input,
        IReadOnlyList<ContextItem> context,
        IReadOnlySet<Capability> requiredCapabilities,
        DifficultyBand bandFloor = DifficultyBand.Trivial)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requiredCapabilities);

        int inputBytes = Encoding.UTF8.GetByteCount(input);
        int bytes = inputBytes + context.Sum(item => Encoding.UTF8.GetByteCount(item.Text));

        // La stima dei token e' conservativa: sovrastimare chiede una finestra piu' larga del
        // necessario, sottostimare produce una richiesta che il provider rifiuta.
        int tokens = bytes / ContextLimits.BytesPerToken;
        int inputTokens = inputBytes / ContextLimits.BytesPerToken;

        return new RequestProfile(
            Version,
            bytes,
            tokens,
            Highest(Highest(BandOf(inputTokens), bandFloor), IntentOf(requiredCapabilities) is Intent.CodeChange ? DifficultyBand.Standard : DifficultyBand.Trivial),
            IntentOf(requiredCapabilities),
            requiredCapabilities,
            inputTokens,
            BandOf(tokens));
    }

    /// <summary>
    /// Requisito del turno: la finestra che il payload pretende (<see cref="WindowTokens"/>), mai sotto
    /// <see cref="MinimumWindowTokens"/>. Il contesto deve starci, ma non rende la domanda piu' difficile.
    /// </summary>
    public static BandRequirements Requirements(RequestProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new BandRequirements(Math.Max(MinimumWindowTokens, WindowTokens(profile)));
    }

    /// <summary>
    /// Spazio per la risposta che la finestra deve lasciare oltre al payload. Coincide con la riserva che il coordinatore
    /// usa per la finestra di lavoro, cosi' un servizio ammesso non viene poi tagliato.
    /// </summary>
    public const int ReplyReserveTokens = 2048;

    /// <summary>
    /// Finestra che il payload pretende: la sua stima piu' la risposta (23 settembre 2026). Fino ad allora si prendeva la
    /// finestra della banda della dimensione, e 4479 byte di cronologia pretendevano 32768 token: il modello piu' piccolo
    /// usciva dai candidati e quello scelto si ricaricava con una finestra da 7 GB.
    /// </summary>
    public static int WindowTokens(RequestProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return (int)Math.Min(int.MaxValue, (long)profile.EstimatedTokens + ReplyReserveTokens);
    }

    /// <summary>La piu' esigente fra due bande.</summary>
    public static DifficultyBand Highest(DifficultyBand first, DifficultyBand second) =>
        Rank(first) >= Rank(second) ? first : second;

    private static int Rank(DifficultyBand band) => band switch
    {
        DifficultyBand.Trivial => 0,
        DifficultyBand.Standard => 1,
        _ => 2,
    };

    /// <summary>Banda corrispondente ai token stimati.</summary>
    public static DifficultyBand BandOf(int estimatedTokens) => estimatedTokens switch
    {
        <= TrivialTokens => DifficultyBand.Trivial,
        <= StandardTokens => DifficultyBand.Standard,
        _ => DifficultyBand.Heavy,
    };

    /// <summary>
    /// L'intento segue le capacita' dichiarate: scrivere file o eseguire comandi e' una modifica,
    /// leggere codice e' un'analisi, il resto e' chat.
    /// </summary>
    public static Intent IntentOf(IReadOnlySet<Capability> requiredCapabilities)
    {
        ArgumentNullException.ThrowIfNull(requiredCapabilities);

        if (requiredCapabilities.Contains(Capability.FileWrite) || requiredCapabilities.Contains(Capability.Shell))
        {
            return Intent.CodeChange;
        }

        return requiredCapabilities.Contains(Capability.CodeRead) ? Intent.CodeAnalysis : Intent.Chat;
    }
}
