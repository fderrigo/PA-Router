
namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Perche' un servizio non ha potuto prendere una richiesta, **con i numeri che lo hanno deciso**.
///
/// Fino alla S116 il rifiuto viaggiava come <c>servizio [CODICE,CODICE]</c>: la resa traduceva il
/// codice in una frase che nominava il parametro da cambiare — `ratings`, `minQuality` — ma non
/// diceva quanto vale adesso ne' quanto servirebbe. L'operatore leggeva «il voto e' piu' basso di
/// quanto serve» senza sapere ne' il voto ne' la soglia, e per scoprirli doveva aprire due file.
///
/// I valori esistono nel momento in cui la decisione viene presa: stanno in
/// <c>PolicyCandidate</c> e nella policy compilata. Qui vengono **portati via insieme al codice**
/// invece di essere buttati.
///
/// Contiene numeri e nomi di parametri, mai testo della richiesta, indirizzi o credenziali: e' un
/// dettaglio che finisce sullo schermo e, in forma ridotta, nell'audit.
/// </summary>
/// <param name="Code">Codice stabile del motivo, in forma wire.</param>
/// <param name="Observed">Valore osservato sul servizio, gia' reso leggibile. Null quando il motivo non ne ha uno.</param>
/// <param name="Required">Valore che sarebbe servito. Null quando il motivo non e' una soglia.</param>
public sealed record DenialFact(string Code, string? Observed = null, string? Required = null);

/// <summary>
/// Rifiuto di un servizio: che cosa e' e perche' non ha preso la richiesta.
/// </summary>
/// <param name="ServiceId">Identificatore del servizio.</param>
/// <param name="Name">Nome leggibile: il modello quando c'e', altrimenti l'identificatore.</param>
/// <param name="AnswersRequests">
/// Falso per i servizi **ausiliari** — embedding, rilevatori, classificatore. Lavorano per il router,
/// non per la richiesta: elencare per loro cinque motivi di policy e' vero e inutile, e sono meta'
/// del muro che l'operatore ha visto. Per loro basta una riga: non rispondono alle richieste.
/// </param>
/// <param name="Disabled">Vero se il servizio e' semplicemente spento: non e' un guasto.</param>
/// <param name="Facts">Motivi con i loro numeri, in ordine di codice.</param>
public sealed record ServiceDenial(
    string ServiceId,
    string Name,
    bool AnswersRequests,
    bool Disabled,
    IReadOnlyList<DenialFact> Facts);

/// <summary>
/// Perche' il turno e' stato negato: la difficolta' che ha deciso le soglie, e un rifiuto per servizio.
///
/// La difficolta' e' la prima cosa da dire. Senza, cinque motivi ripetuti su tre servizi sembrano
/// cinque guasti; con la difficolta' davanti si vede che sono **una causa sola** vista da tre
/// servizi, ed e' quella che l'operatore deve cambiare.
/// </summary>
/// <param name="Difficulty">Banda assegnata alla richiesta, in forma wire, se ne e' stata assegnata una.</param>
/// <param name="MinContextTokens">Finestra minima pretesa dalla richiesta (payload piu' risposta).</param>
/// <param name="Services">Rifiuti, i servizi accesi prima di quelli spenti.</param>
public sealed record DenialDetail(
    string? Difficulty,
    int? MinContextTokens,
    IReadOnlyList<ServiceDenial> Services);
