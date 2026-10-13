namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Calibrazione dichiarata dall'operatore per un target, piu' lo stato di quota osservato.
///
/// Sono preferenze, non misure: <c>costScore</c> non e' un prezzo e <c>quality</c> non e' un
/// benchmark. Un valore assente non viene stimato e non viene aggiornato online: vale 0.5, cioe'
/// «nessuna preferenza», cosi' l'assenza di dati non finge di essere un dato
/// (03-specs/ROUTING.md).
/// </summary>
/// <param name="Quality">
/// Qualita' ricavata dal posto del servizio nell'ordine dei modelli dell'operatore, 0..1 (il primo vale 1); null se il
/// servizio non ha un posto. Dal 24 settembre 2026 (ADR-032, fase 3) i voti per abilita' non esistono piu': l'ordine e'
/// l'unica scala, e il ranking confronta un numero solo.
/// </param>
/// <param name="LatencyScore">Preferenza di latenza 0..1, null se non dichiarata.</param>
/// <param name="CostScore">
/// Componente di costo 0..1. Non si dichiara piu' (ADR-032): la calcola il ranking dal prezzo, relativa al piu'
/// economico; null vale il neutro.
/// </param>
/// <param name="QuotaScore">
/// Quota residua osservata 0..1, null se non osservata. Nessuna sonda la misura oggi e la calibrazione scritta a mano
/// non esiste piu' (ADR-032): con quota disponibile la componente vale il neutro.
/// </param>
/// <param name="QuotaState">Stato della quota osservato.</param>
/// <param name="Price">
/// Prezzo marginale per milione di token, null se sconosciuto. Quando c'e', il ranking stima il costo
/// della richiesta e lo usa al posto di <paramref name="CostScore"/>.
/// </param>
public readonly record struct TargetCalibration(
    double? Quality,
    double? LatencyScore,
    double? CostScore,
    double? QuotaScore,
    QuotaState QuotaState,
    TokenPrice? Price = null)
{
    /// <summary>Valore usato quando una preferenza non e' disponibile.</summary>
    public const double NeutralScore = 0.5;

    /// <summary>Calibrazione priva di dati: ogni componente vale il neutro.</summary>
    public static TargetCalibration Unknown { get; } = new(null, null, null, null, QuotaState.Unknown);
}
