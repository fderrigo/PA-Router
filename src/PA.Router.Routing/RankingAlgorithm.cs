using PA.Router.Abstractions.Contracts;

namespace PA.Router.Routing;

/// <summary>
/// Componenti del punteggio di un candidato, gia' normalizzate. Vengono restituite per intero
/// perche' l'explain deve poter mostrare da dove viene il numero, non solo il numero.
/// </summary>
/// <param name="Quality">Componente di qualita' usata.</param>
/// <param name="Latency">Componente di latenza usata.</param>
/// <param name="Cost">Componente di costo usata.</param>
/// <param name="Quota">Componente di quota usata.</param>
/// <param name="Score">Punteggio composto, non arrotondato.</param>
public readonly record struct ScoreComponents(
    double Quality,
    double Latency,
    double Cost,
    double Quota,
    double Score)
{
    /// <summary>Punteggio arrotondato alle sei cifre usate per il confronto.</summary>
    public double RoundedScore => Math.Round(Score, RankingAlgorithm.ScoreDecimals, MidpointRounding.ToEven);
}

/// <summary>
/// Formula di ranking <c>rank-v2</c>.
///
/// I pesi sono fissi e non configurabili per richiesta: cambiarli significa cambiare versione
/// dell'algoritmo, con ADR e fixture aggiornate. Qui non entrano ne' il prompt, ne' un modello,
/// ne' un apprendimento online: la funzione e' pura e deterministica (03-specs/ROUTING.md).
///
/// Il ranking non e' un controllo: riceve soltanto target gia' ammessi dalla policy e non puo'
/// riabilitare nulla. Capacita', privacy, regioni, billing e confini sono gate, non componenti.
/// </summary>
public static class RankingAlgorithm
{
    /// <summary>Versione dell'algoritmo, registrata in ogni decisione.</summary>
    public const string Version = "rank-v3";

    /// <summary>Cifre decimali usate per confrontare due punteggi.</summary>
    public const int ScoreDecimals = 6;

    /// <summary>Peso della qualita'.</summary>
    public const double QualityWeight = 0.50;

    /// <summary>Peso della latenza.</summary>
    public const double LatencyWeight = 0.20;

    /// <summary>Peso del costo.</summary>
    public const double CostWeight = 0.20;

    /// <summary>Peso della quota.</summary>
    public const double QuotaWeight = 0.10;

    /// <summary>
    /// Calcola le componenti di un candidato.
    ///
    /// Una misura assente vale 0.5 e non viene stimata: la beta non aggiorna la calibrazione
    /// online, quindi un valore inventato resterebbe li' senza che nessuno lo corregga.
    /// </summary>
    public static ScoreComponents Score(TargetCalibration calibration)
    {
        double quality = Normalize(calibration.Quality);
        double latency = Normalize(calibration.LatencyScore);
        double cost = Normalize(calibration.CostScore);
        double quota = QuotaComponent(calibration);

        double score = (QualityWeight * quality)
            + (LatencyWeight * latency)
            + (CostWeight * cost)
            + (QuotaWeight * quota);

        return new ScoreComponents(quality, latency, cost, quota, score);
    }

    /// <summary>
    /// Componente di quota. <c>unknown</c> vale il neutro: non e' quota residua, ma nemmeno una
    /// penalita' inventata. <c>exhausted</c> non arriva qui, perche' la policy lo ha gia' negato.
    /// </summary>
    private static double QuotaComponent(TargetCalibration calibration) => calibration.QuotaState switch
    {
        QuotaState.Available => Normalize(calibration.QuotaScore),
        QuotaState.Unknown => TargetCalibration.NeutralScore,
        QuotaState.Exhausted => 0,
        _ => TargetCalibration.NeutralScore,
    };

    private static double Normalize(double? value) =>
        value is double score ? Math.Clamp(score, 0, 1) : TargetCalibration.NeutralScore;
}
