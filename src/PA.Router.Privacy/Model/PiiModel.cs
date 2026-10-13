using PA.Router.Abstractions.Contracts;

namespace PA.Router.Privacy.Model;

/// <summary>
/// Entita' riconosciuta da un modello di rilevamento, con lo span in caratteri del testo di
/// partenza.
/// </summary>
/// <param name="Label">Etichetta del modello, senza il prefisso BIO.</param>
/// <param name="Start">Indice del primo carattere.</param>
/// <param name="Length">Lunghezza in caratteri.</param>
/// <param name="Confidence">Fiducia media dei token che compongono l'entita'.</param>
public sealed record PiiEntity(string Label, int Start, int Length, double Confidence);

/// <summary>
/// Esito di una scansione con modello.
/// </summary>
/// <param name="Entities">Entita' riconosciute, in ordine di posizione.</param>
/// <param name="Truncated">
/// Vero se il testo superava la finestra del modello ed e' stato analizzato solo in parte. Un
/// risultato parziale non e' un risultato completo, e chi lo riceve deve poterlo sapere.
/// </param>
public sealed record PiiScanResult(IReadOnlyList<PiiEntity> Entities, bool Truncated);

/// <summary>
/// Rilevatore di dati personali basato su un modello locale.
///
/// Esiste perche' un'espressione regolare riconosce una **forma**, non un **ruolo**: «Mario Rossi»
/// e «Via Roma 12» non hanno una forma riconoscibile, e nessun regex ragionevole li trova senza
/// travolgere di falsi positivi il testo ordinario. Il modello li trova, e per questo si affianca
/// ai rilevatori deterministici invece di sostituirli: quelli restano esatti dove serve esattezza
/// — il carattere di controllo di un codice fiscale e' matematica, e nessuna delle categorie di un
/// modello PII copre le credenziali (03-specs/PRIVACY.md).
///
/// L'implementazione gira **in processo**: nessun servizio da avviare, nessuna porta, nessun
/// egress. Se il modello non e' installato, il rilevatore lo dichiara e non inventa un ripiego.
/// </summary>
public interface IPiiModel : IDisposable
{
    /// <summary>Identificatore del modello, per fingerprint e diagnosi.</summary>
    string Name { get; }

    /// <summary>Versione del rilevatore, registrata in ogni finding.</summary>
    string Version { get; }

    /// <summary>Token massimi che il modello accetta in una passata.</summary>
    int MaxTokens { get; }

    /// <summary>
    /// Cerca le entita' nel testo indicato.
    ///
    /// L'operazione e' sincrona perche' l'inferenza lo e': girare in processo su un tensore gia'
    /// in memoria non ha attese di I/O, e avvolgerla in un <c>Task</c> fingerebbe un'asincronia
    /// che non esiste. Chi non vuole bloccare il proprio thread la sposta lui su un altro.
    /// </summary>
    PiiScanResult Scan(string text);
}

/// <summary>
/// Traduzione fra le etichette del modello e i tipi di finding del prodotto.
///
/// La mappa e' esplicita e parziale di proposito. Cio' che il modello chiama `DATE`, `TIME` o
/// `AMOUNT` non entra: una data non e' un dato personale, e trattarla come tale alzerebbe la
/// classe di quasi ogni testo rendendo il rilevamento inutile. Un'etichetta che non compare qui
/// viene ignorata, non tradotta a occhio.
/// </summary>
public static class PiiLabels
{
    private static readonly Dictionary<string, FindingType> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Dove il deterministico e' gia' esatto, il modello concorda invece di creare un tipo
        // parallelo: due nomi per la stessa cosa renderebbero illeggibile un report.
        ["EMAIL"] = FindingType.Email,
        ["TELEPHONENUM"] = FindingType.Phone,
        ["CF"] = FindingType.ItalianFiscalCode,

        // Cio' che solo il modello trova.
        ["FULLNAME"] = FindingType.FullName,
        ["STREET"] = FindingType.PostalAddress,
        ["BUILDINGNUM"] = FindingType.PostalAddress,
        ["CITY"] = FindingType.PostalAddress,
        ["ZIPCODE"] = FindingType.PostalAddress,
        ["PROVINCE"] = FindingType.PostalAddress,
        ["CATASTO"] = FindingType.PostalAddress,
        ["IBAN"] = FindingType.Iban,
        ["CREDITCARDNUMBER"] = FindingType.PaymentCard,
        ["DOCID"] = FindingType.IdentityDocument,
        ["ID_DOC"] = FindingType.IdentityDocument,
        ["TARGA"] = FindingType.VehiclePlate,
        ["PIVA"] = FindingType.VatNumber,
        ["ORG"] = FindingType.Organisation,
        ["AGE"] = FindingType.Demographic,
        ["GENDER"] = FindingType.Demographic,
    };

    /// <summary>
    /// Etichette di un luogo, non di un indirizzo (23 settembre 2026): «Milano» in «quanti abitanti ha Milano?» rendeva
    /// personale una domanda di geografia, e la conversazione restava personale da li' in poi. Una citta' o una provincia
    /// sono parte di un indirizzo solo accanto a un elemento che porta a una casa: via, numero civico, CAP, dati catastali.
    /// </summary>
    private static readonly HashSet<string> PlaceLabels = new(StringComparer.OrdinalIgnoreCase) { "CITY", "PROVINCE" };

    private static readonly HashSet<string> StreetLabels = new(StringComparer.OrdinalIgnoreCase) { "STREET", "BUILDINGNUM", "ZIPCODE", "CATASTO" };

    /// <summary>Vero per le etichette di un luogo, che contano come indirizzo solo con un elemento della via.</summary>
    public static bool IsPlace(string label) => PlaceLabels.Contains(label);

    /// <summary>Vero per le etichette che portano a una casa: via, numero civico, CAP, dati catastali.</summary>
    public static bool IsStreetLevel(string label) => StreetLabels.Contains(label);

    /// <summary>Tipo corrispondente all'etichetta, o null se il prodotto non la traduce.</summary>
    public static FindingType? Translate(string label) =>
        Map.TryGetValue(label, out FindingType type) ? type : null;

    /// <summary>
    /// Classe che il tipo comporta. Un'organizzazione non e' un dato personale: alza a
    /// <c>internal</c> e non oltre. Tutto il resto e' <c>personal</c>.
    /// </summary>
    public static DataClass ClassOf(FindingType type) =>
        type is FindingType.Organisation ? DataClass.Internal : DataClass.Personal;

    /// <summary>Etichette tradotte, per le diagnosi.</summary>
    public static IReadOnlySet<string> KnownLabels { get; } =
        new HashSet<string>(Map.Keys, StringComparer.OrdinalIgnoreCase);
}
