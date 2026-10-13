namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Profilo di policy validato (policy.schema.json). Qui il documento e' solo letto e verificato:
/// la composizione restrittiva e la valutazione dei candidati appartengono allo step S05.
/// </summary>
/// <param name="SchemaVersion">Versione dello schema, sempre 2.</param>
/// <param name="Revision">Revisione dichiarata dall'operatore.</param>
/// <param name="ProfileId">Identificatore del profilo, unico fra i file caricati.</param>
/// <param name="Constraints">Vincoli del profilo.</param>
public sealed record PolicyDocument(
    int SchemaVersion,
    string Revision,
    string ProfileId,
    PolicyConstraints Constraints)
{
    /// <summary>
    /// Nome dato dall'operatore al profilo, mostrato al posto dell'identificatore (24 settembre 2026), come il nome di un
    /// servizio. E' un dato: il codice non conosce nomi ne' identificatori di profilo. Non sostituisce l'identificatore —
    /// a quello sono legate sessioni e configurazione. Null: si mostra l'identificatore.
    /// </summary>
    public string? Name { get; init; }
}

/// <summary>
/// Vincoli di un profilo. Sono autorizzazioni, non preferenze: nessun punteggio puo' riabilitare
/// cio' che questi campi negano.
///
/// Dal 24 settembre 2026 (ADR-032, fase 2) un profilo non porta piu' limiti di esecuzione ne' il canale: tempo massimo,
/// tentativi, limiti in byte e durata dei ricordi stanno solo nella configurazione (<see cref="LimitsConfig"/>,
/// <see cref="MemoryConfig.RetentionDays"/>), e il canale lo dice il front end che riceve la richiesta.
///
/// Dal 25 settembre 2026 (ADR-032, fase 4) **una sola regola decide dove vanno i dati**: le zone per classe
/// (<see cref="Zones"/>). Collocazioni, autenticazioni, costi API, controlli richiesti, regioni, perimetro e la forma
/// precedente alle zone non esistono piu': la zona di un servizio si ricava da collocazione e conferme
/// (<see cref="ServiceZones"/>), i controlli che un servizio deve avere dal suo tipo e da cio' che il turno chiede
/// (<see cref="AdmissionControls"/>), la spesa la governa il tetto per richiesta della configurazione (fase 5).
/// </summary>
/// <param name="MinimumDataClass">Classe minima attribuita a qualunque payload.</param>
/// <param name="AllowedServiceIds">
/// Servizi ammessi; devono esistere nel registro. **Vuoto significa ogni servizio** (ADR-032, fase 4): un elenco compilato
/// restringe la modalita' ai soli servizi nominati. Si legge solo con <see cref="AllowsService"/>.
/// </param>
/// <param name="Zones">Zone di fiducia: la zona piu' lontana ammessa per ciascuna classe.</param>
/// <param name="AllowedCapabilities">Capacita' ammesse.</param>
/// <param name="RedactContacts">Richiede la redazione dei contatti.</param>
/// <param name="AllowLocalMemoryProcessing">Consente l'elaborazione locale per la memoria.</param>
public sealed record PolicyConstraints(
    DataClass MinimumDataClass,
    IReadOnlySet<string> AllowedServiceIds,
    PolicyZones Zones,
    IReadOnlySet<Capability> AllowedCapabilities,
    bool RedactContacts,
    bool AllowLocalMemoryProcessing)
{
    /// <summary>
    /// Limiti del giro modello-strumento che il profilo impone (ADR-020); restringono quelli della
    /// configurazione e non li allargano. Null non restringe.
    /// </summary>
    public ToolLimitsRestriction? ToolLimits { get; init; }

    /// <summary>
    /// Come il profilo gradua il dato personale (ADR-029): <c>graded</c> distingue ordinario e stretto dai segnali,
    /// <c>strict</c> tratta come stretto qualunque dato personale. I rilevatori danno i segnali, la classe la decide qui.
    /// </summary>
    public PersonalDataGrading PersonalDataGrading { get; init; } = PersonalDataGrading.Graded;

    /// <summary>Vero se il profilo ammette il servizio (<see cref="AllowedServices.Allows"/>).</summary>
    public bool AllowsService(string serviceId) => AllowedServices.Allows(AllowedServiceIds, serviceId);
}

/// <summary>
/// Lettura dell'elenco dei servizi di un profilo (ADR-032, fase 4): vuoto vuol dire ogni servizio, compilato i soli
/// nominati. Unica lettura: una seconda copia che dimenticasse il caso vuoto negherebbe tutto, o ammetterebbe tutto.
/// </summary>
public static class AllowedServices
{
    /// <summary>Vero se l'elenco ammette il servizio.</summary>
    public static bool Allows(IReadOnlySet<string> allowed, string serviceId)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        return allowed.Count == 0 || allowed.Contains(serviceId);
    }
}

/// <summary>Regola del profilo per un perimetro con modelli troppo deboli (ADR-029, decisione I).</summary>
public enum WeakPerimeterRule
{
    /// <summary>Si rifiuta: il router non degrada in silenzio e non manda fuori di propria iniziativa.</summary>
    [WireValue("deny")]
    Deny,

    /// <summary>
    /// Dichiarata e conservata. Dal 24 settembre 2026 (ADR-032, fase 3) non c'e' piu' una qualita' minima da allentare, e
    /// la regola non cambia nessuna decisione.
    /// </summary>
    [WireValue("declare")]
    Declare,
}

/// <summary>
/// Zone di un profilo (ADR-029).
/// </summary>
/// <param name="MaxZone">
/// Zona piu' lontana ammessa per ciascuna classe. Una classe assente vale <c>lan</c>; <c>secret</c> e <c>unknown</c> non
/// superano mai <c>lan</c>, qualunque cosa dica il file.
/// </param>
/// <param name="ConfirmOutside">
/// Classi che escono dal perimetro solo con la conferma dell'operatore nel terminale; l'API non la chiede mai e nega.
/// </param>
/// <param name="AttachmentsOutsidePerimeter">
/// Vero se un turno con allegati puo' uscire dal perimetro. Per default no; l'uscita si registra sempre, e toglie il
/// solo tetto di zona dovuto all'allegato, mai la classe che la scansione del suo contenuto ha trovato.
/// </param>
public sealed record PolicyZones(
    IReadOnlyDictionary<DataClass, TrustZone> MaxZone,
    IReadOnlySet<DataClass> ConfirmOutside,
    bool AttachmentsOutsidePerimeter)
{
    /// <summary>Classi che un profilo dichiara in <c>maxZone</c>; secret e unknown non escono dalla rete locale.</summary>
    public static IReadOnlyList<DataClass> DeclaredClasses { get; } =
        [DataClass.Public, DataClass.Internal, DataClass.Personal, DataClass.Sensitive];

    /// <summary>
    /// Il perimetro, uguale per ogni profilo dal 25 settembre 2026 (ADR-032, fase 4): questo computer, la rete di casa e
    /// un proprio server in UE. Prima era un campo del profilo (<c>zones.perimeter</c>) che la guida scriveva sempre cosi'.
    /// </summary>
    public static IReadOnlySet<TrustZone> FixedPerimeter { get; } =
        new HashSet<TrustZone> { TrustZone.SameHost, TrustZone.Lan, TrustZone.PrivateEu };

    /// <summary>Zone che il profilo tratta come interne: il perimetro fisso.</summary>
    public IReadOnlySet<TrustZone> Perimeter => FixedPerimeter;

    /// <summary>
    /// Regola del perimetro debole. Dal 24 settembre 2026 (ADR-032, fase 3) non allenta piu' nulla: resta per non perdere
    /// la scelta dell'operatore.
    /// </summary>
    public WeakPerimeterRule WeakPerimeter { get; init; } = WeakPerimeterRule.Deny;

    /// <summary>
    /// Traduzione della forma precedente alle zone (<c>externalProcessing</c>, <c>maxCloudDataClass</c>), usata **solo**
    /// da <c>pa config migrate</c> prima della conversione della fase 4: dal 25 settembre 2026 la lettura rifiuta quella
    /// forma. Non allarga: una classe entro il tetto va a una macchina propria in UE, e ai terzi solo se l'elaborazione
    /// esterna era ammessa; oltre il tetto resta sulla macchina e sulla sua rete.
    /// </summary>
    public static PolicyZones FromLegacy(ExternalProcessing external, DataClass maxCloudDataClass)
    {
        Dictionary<DataClass, TrustZone> maxZone = [];

        foreach (DataClass dataClass in DeclaredClasses)
        {
            maxZone[dataClass] = dataClass > maxCloudDataClass
                ? TrustZone.Lan
                : external is ExternalProcessing.Allowed ? TrustZone.ThirdParty : TrustZone.PrivateEu;
        }

        return new PolicyZones(maxZone, new HashSet<DataClass>(), AttachmentsOutsidePerimeter: false);
    }

    /// <summary>Zona piu' lontana ammessa per una classe; secret e unknown mai oltre la rete locale.</summary>
    public TrustZone For(DataClass dataClass)
    {
        TrustZone declared = MaxZone.TryGetValue(dataClass, out TrustZone zone) ? zone : TrustZone.Lan;
        return dataClass is DataClass.Secret or DataClass.Unknown && declared > TrustZone.Lan ? TrustZone.Lan : declared;
    }
}

/// <summary>Regola del profilo per il dato personale (ADR-029, decisione G).</summary>
public enum PersonalDataGrading
{
    /// <summary>
    /// Un nome da solo e' dato personale ordinario (<c>personal</c>); un identificativo diretto — codice fiscale, documento,
    /// IBAN, carta — oppure un nome con un indirizzo e' dato stretto (<c>sensitive</c>). L'art. 9 resta stretto.
    /// </summary>
    [WireValue("graded")]
    Graded,

    /// <summary>Qualunque dato personale e' stretto: la regola di chi vuole ogni dato personale trattato con la massima cautela.</summary>
    [WireValue("strict")]
    Strict,
}

/// <summary>
/// Requisito che un servizio deve soddisfare per ricevere una richiesta: la finestra di contesto minima, cioe' il payload
/// stimato piu' la riserva per la risposta, mai sotto <c>RequestAnalyzer.MinimumWindowTokens</c>. Si nega, non si tronca.
///
/// Fino al 24 settembre 2026 portava anche una qualita' minima per difficolta', dichiarata nella configurazione
/// (<c>routing.bands</c>) e alzabile dal profilo (<c>minimumBandRequirements</c>): tolte da ADR-032, fase 3. La qualita'
/// e' il posto nell'ordine dei modelli e non esclude nessuno.
/// </summary>
/// <param name="MinContextTokens">Finestra di contesto minima richiesta.</param>
public readonly record struct BandRequirements(int MinContextTokens);
