using System.Collections.Frozen;

namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Luogo dichiarato dell'elaborazione: un paese in forma ISO 3166-1 alpha-2 minuscola
/// (<c>fr</c>, <c>cn</c>, <c>us</c>), oppure un gruppo che ne rappresenta molti (<c>eu</c>,
/// <c>eea</c>).
///
/// Fino alla versione precedente i valori ammessi erano tre — <c>eu</c>, <c>us</c>, <c>other</c> —
/// e <c>other</c> metteva nello stesso insieme giurisdizioni che non si somigliano affatto. Un
/// profilo non poteva distinguere un servizio in Francia da uno in Cina, che e' esattamente la
/// distinzione per cui il campo esiste.
///
/// Un elenco vuoto **non** significa <c>eu</c> e non significa nemmeno «ovunque»: significa che il
/// luogo non e' dichiarato, e un luogo non dichiarato non soddisfa mai un vincolo geografico.
/// </summary>
/// <param name="Code">Termine normalizzato in minuscolo.</param>
public readonly record struct ProcessingRegion(string Code)
{
    /// <inheritdoc />
    public override string ToString() => Code;
}

/// <summary>
/// Vocabolario dei luoghi di elaborazione e sua espansione in paesi.
///
/// Non esiste qui una tabella dei paesi del mondo, ed e' deliberato: mantenerla significherebbe
/// inseguire le revisioni di uno standard per non guadagnare nulla. Cio' che conta e' la
/// **forma** — due lettere minuscole — perche' la sicurezza della decisione non viene dal
/// riconoscere il codice: viene dal confronto fra insiemi. Il luogo di un servizio deve essere
/// contenuto in quelli che il profilo ammette, quindi un codice sbagliato semplicemente non
/// combacia e il servizio viene negato. Un refuso sbaglia sempre dalla parte del no.
///
/// I gruppi invece vanno conosciuti, perche' si espandono: sono pochi, cambiano di rado e ogni
/// modifica e' una decisione, non un aggiornamento meccanico.
/// </summary>
public static class Regions
{
    /// <summary>Stati membri dell'Unione europea.</summary>
    public const string EuropeanUnion = "eu";

    /// <summary>Spazio economico europeo: Unione europea piu' Islanda, Liechtenstein e Norvegia.</summary>
    public const string EuropeanEconomicArea = "eea";

    private static readonly string[] EuMembers =
    [
        "at", "be", "bg", "cy", "cz", "de", "dk", "ee", "es", "fi", "fr", "gr", "hr", "hu",
        "ie", "it", "lt", "lu", "lv", "mt", "nl", "pl", "pt", "ro", "se", "si", "sk",
    ];

    private static readonly string[] EeaOnlyMembers = ["is", "li", "no"];

    private static readonly FrozenDictionary<string, FrozenSet<ProcessingRegion>> GroupMembers =
        new Dictionary<string, FrozenSet<ProcessingRegion>>(StringComparer.Ordinal)
        {
            [EuropeanUnion] = Set(EuMembers),
            [EuropeanEconomicArea] = Set([.. EuMembers, .. EeaOnlyMembers]),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Termini di gruppo riconosciuti, in ordine.</summary>
    public static IReadOnlyList<string> GroupNames { get; } = [EuropeanEconomicArea, EuropeanUnion];

    /// <summary>
    /// Normalizza un termine scritto in un file. Non verifica che il paese esista: verifica che
    /// abbia la forma di un codice o che sia un gruppo noto.
    /// </summary>
    public static ProcessingRegion Parse(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        return new ProcessingRegion(term.Trim().ToLowerInvariant());
    }

    /// <summary>Vero se il termine e' un gruppo che si espande in piu' paesi.</summary>
    public static bool IsGroup(ProcessingRegion region) =>
        region.Code is not null && GroupMembers.ContainsKey(region.Code);

    /// <summary>
    /// Vero se il termine e' scrivibile in un file: un gruppo noto, oppure due lettere minuscole.
    /// Un termine non riconosciuto viene rifiutato alla lettura della configurazione, non ignorato:
    /// ignorarlo allargherebbe in silenzio cio' che un profilo ammette.
    /// </summary>
    public static bool IsWellFormed(ProcessingRegion region)
    {
        if (region.Code is not string code)
        {
            return false;
        }

        if (GroupMembers.ContainsKey(code))
        {
            return true;
        }

        return code.Length == 2 && code[0] is >= 'a' and <= 'z' && code[1] is >= 'a' and <= 'z';
    }

    /// <summary>
    /// Espande un insieme di termini nei paesi che rappresenta. Un paese vale per se stesso, un
    /// gruppo per tutti i suoi membri.
    ///
    /// L'espansione rende conservativo il confronto, ed e' voluto: un servizio che dichiara
    /// <c>eu</c> — «da qualche parte nell'Unione» — non soddisfa un profilo che ammette la sola
    /// Francia, perche' quel servizio potrebbe stare altrove. Il contrario invece passa.
    /// </summary>
    public static IReadOnlySet<ProcessingRegion> Expand(IReadOnlySet<ProcessingRegion> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        HashSet<ProcessingRegion> expanded = [];

        foreach (ProcessingRegion region in declared)
        {
            if (region.Code is not null && GroupMembers.TryGetValue(region.Code, out FrozenSet<ProcessingRegion>? members))
            {
                expanded.UnionWith(members);
                continue;
            }

            expanded.Add(region);
        }

        return expanded;
    }

    private static FrozenSet<ProcessingRegion> Set(IEnumerable<string> codes) =>
        codes.Select(code => new ProcessingRegion(code)).ToFrozenSet();
}
