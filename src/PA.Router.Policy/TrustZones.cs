using PA.Router.Abstractions.Contracts;

namespace PA.Router.Policy;

/// <summary>
/// Zone di fiducia (ADR-029, decisione H): la zona di una destinazione si ricava, non si dichiara.
///
/// Dal 25 settembre 2026 (ADR-032, fase 4) la zona di un servizio la calcola <see cref="ServiceZones"/> — l'unica
/// implementazione, che usa anche la conversione di <c>pa config migrate</c> — e la forma dei profili precedente alle zone
/// non si traduce piu' qui: la lettura la rifiuta, e la converte la sola migrazione.
/// </summary>
public static class TrustZones
{
    /// <summary>
    /// Zona di un servizio da collocazione dell'inferenza e controlli **effettivi** (dichiarati e osservati o confermati):
    /// un servizio locale senza l'elaborazione locale confermata vale come un terzo (<see cref="ServiceZones.Of"/>).
    /// </summary>
    public static TrustZone Of(Placement placement, IReadOnlySet<Control> controls, IReadOnlySet<ProcessingRegion> regions) =>
        ServiceZones.Of(placement, controls, regions);

    /// <summary>Zona di un candidato della policy.</summary>
    public static TrustZone Of(PolicyCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return ServiceZones.Of(candidate.InferencePlacement, candidate.Controls, candidate.ProcessingRegions);
    }

    /// <summary>
    /// Zona della sorgente di uno strumento (ADR-020). Di una sorgente si conoscono collocazione, luoghi e la scadenza della
    /// dichiarazione dell'operatore sugli strumenti, non conferme per servizio: una sorgente su questo computer o in casa
    /// e' configurata dall'operatore e vale per la sua collocazione; una remota e' <c>private_eu</c> solo con tutti i
    /// luoghi dentro l'Unione e la dichiarazione non scaduta, altrimenti un terzo generico.
    /// </summary>
    public static TrustZone OfDestination(
        Placement placement,
        IReadOnlySet<ProcessingRegion> regions,
        DateTimeOffset? evidenceExpiresAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(regions);

        return placement switch
        {
            Placement.Loopback => TrustZone.SameHost,
            Placement.Lan => TrustZone.Lan,
            Placement.PrivateRemote => ServiceZones.InEuropeanUnion(regions) && evidenceExpiresAt is DateTimeOffset expiry && expiry > now
                ? TrustZone.PrivateEu
                : TrustZone.ThirdParty,
            _ => TrustZone.ThirdParty,
        };
    }

    /// <summary>
    /// Zona di una destinazione di cui si conosce la sola collocazione — la sorgente di uno strumento indicata da
    /// un'applicazione: senza luoghi ne' dichiarazione, una destinazione remota vale come un terzo generico.
    /// </summary>
    public static TrustZone OfPlacement(Placement placement) =>
        OfDestination(placement, new HashSet<ProcessingRegion>(), null, DateTimeOffset.MinValue);

    /// <summary>
    /// Composizione restrittiva: per ogni classe la zona piu' vicina, le conferme di entrambi, e gli allegati fuori dal
    /// perimetro solo se lo ammettono tutti e due. Il perimetro e' lo stesso per tutti.
    /// </summary>
    public static PolicyZones Restrict(PolicyZones first, PolicyZones second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        Dictionary<DataClass, TrustZone> maxZone = [];

        foreach (DataClass dataClass in Enum.GetValues<DataClass>())
        {
            TrustZone a = first.For(dataClass);
            TrustZone b = second.For(dataClass);
            maxZone[dataClass] = a < b ? a : b;
        }

        HashSet<DataClass> confirm = [.. first.ConfirmOutside];
        confirm.UnionWith(second.ConfirmOutside);

        return new PolicyZones(maxZone, confirm, first.AttachmentsOutsidePerimeter && second.AttachmentsOutsidePerimeter)
        {
            // Dichiarare una risposta piu' debole e' un'apertura: vale solo se la ammettono entrambi.
            WeakPerimeter = first.WeakPerimeter is WeakPerimeterRule.Declare && second.WeakPerimeter is WeakPerimeterRule.Declare
                ? WeakPerimeterRule.Declare
                : WeakPerimeterRule.Deny,
        };
    }
}
