namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Tipi di finding prodotti dai detector locali. L'elenco e' chiuso: un detector nuovo aggiunge
/// un membro qui e la sua stringa wire, cosi' l'audit resta confrontabile nel tempo.
/// </summary>
public enum FindingType
{
    [WireValue("email")]
    Email,

    [WireValue("phone")]
    Phone,

    [WireValue("italian_fiscal_code")]
    ItalianFiscalCode,

    [WireValue("api_key")]
    ApiKey,

    [WireValue("private_key")]
    PrivateKey,

    [WireValue("assigned_password")]
    AssignedPassword,

    /// <summary>
    /// Categoria particolare dell'articolo 9: salute, origine, convinzioni, opinioni politiche,
    /// appartenenza sindacale, vita sessuale, dati biometrici o genetici.
    /// </summary>
    [WireValue("sensitive_category")]
    SensitiveCategory,

    // ── Tipi che solo un modello NER trova ────────────────────────────────────
    // Un'espressione regolare riconosce una forma, non un ruolo: «Mario Rossi» e «Via Roma 12»
    // non hanno una forma. Sono i tipi che il rilevatore locale aggiunge ai sette deterministici,
    // senza sostituirne nessuno (03-specs/PRIVACY.md).

    /// <summary>Nome e cognome di una persona.</summary>
    [WireValue("full_name")]
    FullName,

    /// <summary>Indirizzo postale: via, civico, citta', CAP, provincia, dato catastale.</summary>
    [WireValue("postal_address")]
    PostalAddress,

    /// <summary>Coordinata bancaria.</summary>
    [WireValue("iban")]
    Iban,

    /// <summary>Numero di carta di pagamento.</summary>
    [WireValue("payment_card")]
    PaymentCard,

    /// <summary>Documento di identita' o suo numero.</summary>
    [WireValue("identity_document")]
    IdentityDocument,

    /// <summary>Targa di un veicolo.</summary>
    [WireValue("vehicle_plate")]
    VehiclePlate,

    /// <summary>Partita IVA.</summary>
    [WireValue("vat_number")]
    VatNumber,

    /// <summary>
    /// Denominazione di un'organizzazione. Non e' un dato personale di per se': alza la classe a
    /// <c>internal</c>, non oltre.
    /// </summary>
    [WireValue("organisation")]
    Organisation,

    /// <summary>Eta' o genere dichiarati.</summary>
    [WireValue("demographic")]
    Demographic,

    [WireValue("high_entropy")]
    HighEntropy,
}

/// <summary>
/// Completezza della scansione. <c>Incomplete</c> non e' un dettaglio: rende la classe
/// <see cref="DataClass.Unknown"/> e quindi chiude il passaggio verso il cloud.
/// </summary>
public enum ScanCertainty
{
    [WireValue("complete")]
    Complete,

    [WireValue("incomplete")]
    Incomplete,
}

/// <summary>Provenienza di un elemento di contesto: l'output di un agente resta untrusted.</summary>
public enum Provenance
{
    [WireValue("operator")]
    Operator,

    [WireValue("workspace")]
    Workspace,

    [WireValue("agent_output")]
    AgentOutput,

    [WireValue("memory")]
    Memory,
}

/// <summary>
/// Un match di un detector. Lo span e' in byte UTF-8 e vive solo in memoria: l'audit conserva
/// tipi e conteggi, mai valori o frammenti (03-specs/PRIVACY.md).
/// </summary>
/// <param name="Type">Tipo di dato riconosciuto.</param>
/// <param name="StartByte">Offset del match in byte UTF-8.</param>
/// <param name="ByteLength">Lunghezza del match in byte UTF-8.</param>
/// <param name="Confidence">Confidenza dichiarata dal detector, 0..1.</param>
/// <param name="DetectorVersion">Versione del detector che lo ha prodotto.</param>
public readonly record struct PrivacyFinding(
    FindingType Type,
    int StartByte,
    int ByteLength,
    double Confidence,
    string DetectorVersion);

/// <summary>
/// Elemento di contesto allegato a una richiesta. Il testo e' materiale grezzo: non viene mai
/// registrato, e la classificazione dichiarata puo' solo alzare il livello, mai abbassarlo.
/// </summary>
/// <param name="Id">Identificatore stabile nel payload.</param>
/// <param name="Text">Testo dell'elemento.</param>
/// <param name="Provenance">Origine dell'elemento.</param>
/// <param name="DeclaredClass">Classe dichiarata, se presente.</param>
public sealed record ContextItem(
    string Id,
    string Text,
    Provenance Provenance,
    DataClass? DeclaredClass)
{
    /// <summary>Il testo non compare mai nella resa testuale del DTO.</summary>
    public override string ToString() =>
        "ContextItem { Id = " + Id + ", Provenance = " + Provenance.ToWire() + ", Bytes = "
        + System.Text.Encoding.UTF8.GetByteCount(Text).ToString(System.Globalization.CultureInfo.InvariantCulture) + " }";
}

/// <summary>
/// Gli elementi di contesto che portano la cronologia della sessione. Gli identificatori li assegna chi
/// costruisce il turno (<c>turn-&lt;ordinale&gt;-input</c> e <c>-output</c>); la regola per riconoscerli sta
/// qui, in un punto solo, perche' la usano il taglio del contesto e il racconto del turno.
/// </summary>
public static class HistoryItems
{
    /// <summary>Prefisso degli identificatori della cronologia.</summary>
    public const string Prefix = "turn-";

    /// <summary>Identificatore della domanda di un turno precedente.</summary>
    public static string InputId(int ordinal) =>
        Prefix + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-input";

    /// <summary>Identificatore della risposta di un turno precedente.</summary>
    public static string OutputId(int ordinal) =>
        Prefix + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-output";

    /// <summary>Vero se l'elemento viene dalla cronologia.</summary>
    public static bool Is(string itemId) => itemId.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Quanti turni precedenti ci sono fra gli identificatori indicati.</summary>
    public static int Turns(IEnumerable<string> itemIds) =>
        itemIds.Where(Is)
            .Select(id => id[Prefix.Length..].Split('-')[0])
            .Distinct(StringComparer.Ordinal)
            .Count();
}

/// <summary>
/// Da quale parte del payload viene un dato trovato. Serve a chi legge il turno: un codice fiscale scritto
/// tre domande fa riparte con la cronologia e alza la classe di adesso, ma non e' nella domanda di adesso.
/// </summary>
public enum FindingSource
{
    /// <summary>La richiesta di questo turno.</summary>
    [WireValue("request")]
    Request,

    /// <summary>Un turno precedente, domanda o risposta, rimandato con la richiesta.</summary>
    [WireValue("history")]
    History,

    /// <summary>Un ricordo entrato dalla memoria.</summary>
    [WireValue("memory")]
    Memory,

    /// <summary>Un allegato o un contenuto consegnato con la richiesta.</summary>
    [WireValue("attachment")]
    Attachment,

    /// <summary>Le istruzioni dell'applicazione o la descrizione degli strumenti.</summary>
    [WireValue("instructions")]
    Instructions,
}

/// <summary>
/// Un dato trovato, per il racconto del turno: tipo, rilevatore, provenienza e, per i tipi con una forma
/// verificabile, se la forma torna. **Mai il valore** e mai la posizione: restano nel report in memoria.
/// </summary>
/// <param name="Type">Tipo di dato.</param>
/// <param name="DetectorVersion">Versione del rilevatore che lo ha trovato.</param>
/// <param name="Source">Parte del payload in cui sta.</param>
/// <param name="FormValid">
/// Per i tipi con carattere di controllo: falso se il controllo non torna. Un codice fiscale scritto male resta un
/// dato personale — il rilevatore a modello lo trova apposta — ma chi legge deve sapere perche' i deterministici non
/// lo hanno visto. Null quando il tipo non ha una forma da verificare.
/// </param>
public readonly record struct FindingOrigin(FindingType Type, string DetectorVersion, FindingSource Source, bool? FormValid);

/// <summary>
/// Esito della scansione di una superficie. I finding restano in memoria; verso l'audit vanno
/// soltanto i tipi e i conteggi esposti da <see cref="TypeCounts"/>.
/// </summary>
/// <param name="DetectorVersion">Versione dell'insieme di detector.</param>
/// <param name="Findings">Match trovati, solo in memoria.</param>
/// <param name="AggregateClass">Classe risultante dalla scansione.</param>
/// <param name="Certainty">Completezza della scansione.</param>
/// <param name="ScannedBytes">Byte effettivamente analizzati.</param>
/// <param name="IncompleteDetectors">Detector che non hanno concluso, per la diagnosi.</param>
public sealed record PrivacyReport(
    string DetectorVersion,
    IReadOnlyList<PrivacyFinding> Findings,
    DataClass AggregateClass,
    ScanCertainty Certainty,
    int ScannedBytes,
    IReadOnlyList<string> IncompleteDetectors)
{
    /// <summary>Conteggio per tipo: e' l'unica forma ammessa nell'audit.</summary>
    public IReadOnlyDictionary<FindingType, int> TypeCounts =>
        Findings.GroupBy(finding => finding.Type).ToDictionary(group => group.Key, group => group.Count());

    /// <summary>Nessun contenuto nella resa testuale.</summary>
    public override string ToString() =>
        "PrivacyReport { Class = " + AggregateClass.ToWire()
        + ", Certainty = " + Certainty.ToWire()
        + ", Findings = " + Findings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " }";
}

/// <summary>
/// Manifest del contesto incluso nel payload: identificatori, provenienza, classe e digest, mai
/// il testo. E' cio' che rende verificabile un <c>closed_payload</c> senza conservare i dati.
/// </summary>
/// <param name="ItemId">Identificatore dell'elemento incluso.</param>
/// <param name="Provenance">Origine dell'elemento.</param>
/// <param name="EffectiveClass">Classe effettiva attribuita all'elemento.</param>
/// <param name="ByteLength">Byte inclusi nel payload.</param>
/// <param name="Digest">HMAC locale del testo incluso.</param>
public readonly record struct ContextManifestEntry(
    string ItemId,
    Provenance Provenance,
    DataClass EffectiveClass,
    int ByteLength,
    string Digest);

/// <summary>
/// Payload autorizzabile. Il nome non promette anonimato: significa che il contenuto e' stato
/// analizzato, minimizzato secondo il profilo e delimitato, non che sia privo di dati personali
/// (03-specs/PRIVACY.md).
/// </summary>
/// <param name="Bytes">Byte UTF-8 effettivamente inviabili.</param>
/// <param name="PayloadDigest">HMAC locale del payload.</param>
/// <param name="ContextManifestDigest">HMAC locale del manifest del contesto.</param>
/// <param name="EffectiveClass">Classe effettiva, gia' comprensiva del floor di profilo.</param>
/// <param name="Manifest">Voci del manifest, senza testo.</param>
/// <param name="RedactedCount">Sostituzioni applicate.</param>
/// <param name="TrimmedBytes">Byte esclusi dal budget dopo la scansione.</param>
/// <param name="Instructions">
/// Istruzioni dell'applicazione, gia' analizzate con il resto (ADR-020). Vanno al modello nel ruolo di
/// sistema del fornitore e **non sono policy**: la classe del dato le comprende, il digest del payload
/// le comprende, e dove vanno i dati resta deciso dalla policy. Vuoto se la richiesta non ne porta.
/// </param>
/// <param name="InstructionsDigest">HMAC locale delle istruzioni, per l'audit; null se assenti.</param>
/// <param name="Tools">Strumenti offerti al modello, gia' esaminati con il resto; null se nessuno.</param>
public sealed record SafePayload(
    ReadOnlyMemory<byte> Bytes,
    string PayloadDigest,
    string ContextManifestDigest,
    DataClass EffectiveClass,
    IReadOnlyList<ContextManifestEntry> Manifest,
    int RedactedCount,
    int TrimmedBytes,
    ReadOnlyMemory<byte> Instructions = default,
    string? InstructionsDigest = null,
    IReadOnlyList<ToolDefinition>? Tools = null)
{
    /// <summary>Vero se il payload porta istruzioni dell'applicazione.</summary>
    public bool HasInstructions => !Instructions.IsEmpty;

    /// <summary>Nessun contenuto nella resa testuale, nemmeno troncato.</summary>
    public override string ToString() =>
        "SafePayload { Class = " + EffectiveClass.ToWire()
        + ", Bytes = " + Bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ", Digest = " + PayloadDigest + " }";
}

/// <summary>
/// Digest locale per deduplica e audit. E' un HMAC con chiave locale, non un hash pubblico:
/// un hash semplice di testo breve sarebbe attaccabile a dizionario (02-architecture/DOMAIN-MODEL.md).
/// </summary>
public interface IMetadataDigest
{
    /// <summary>HMAC esadecimale dei byte indicati.</summary>
    string Compute(ReadOnlySpan<byte> data);
}

/// <summary>Ordinamento conservativo delle classi di dato.</summary>
public static class DataClasses
{
    /// <summary>
    /// Classe piu' alta fra le due. L'ordine e' quello di dichiarazione dell'enum e
    /// <see cref="DataClass.Unknown"/> e' il massimo: un dato non classificabile e' trattato come
    /// il piu' vincolante, non come il piu' permissivo.
    /// </summary>
    public static DataClass Max(DataClass first, DataClass second) =>
        (DataClass)Math.Max((int)first, (int)second);

    /// <summary>Classe piu' alta di una sequenza, oppure <paramref name="seed"/> se vuota.</summary>
    public static DataClass Max(DataClass seed, IEnumerable<DataClass> classes)
    {
        ArgumentNullException.ThrowIfNull(classes);

        DataClass result = seed;

        foreach (DataClass current in classes)
        {
            result = Max(result, current);
        }

        return result;
    }
}
