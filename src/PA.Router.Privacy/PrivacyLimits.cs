namespace PA.Router.Privacy;

/// <summary>
/// Limiti applicati dalla pipeline privacy. Sono byte UTF-8, non caratteri: lo schema JSON conta
/// caratteri Unicode, la pipeline conta byte, e i due valori differiscono su testo non ASCII
/// (06-contracts/README.md).
/// </summary>
/// <param name="MaxInputBytes">Input massimo, 64 KiB nella beta.</param>
/// <param name="MaxContextBytes">Contesto aggregato massimo, 512 KiB.</param>
/// <param name="MaxContextItemBytes">Singolo elemento di contesto, 128 KiB.</param>
/// <param name="MaxOutputBytes">Output massimo, 512 KiB.</param>
///
/// <remarks>
/// **Il tempo per rilevatore non sta qui** (tolto in S76, difetto trovato dall'audit).
///
/// Fino alla S75 questo record dichiarava un <c>DetectorTimeout</c> di 50 ms, documentato come
/// «tempo massimo per detector su una superficie» — e **non aveva un solo lettore**. Il tempo vero
/// e' il terzo argomento di <c>[GeneratedRegex(..., 50)]</c> in ciascuno dei sette rilevatori
/// deterministici: un <c>Regex</c> porta il proprio <c>MatchTimeout</c> fissato alla costruzione, e
/// con il generatore quella costante e' decisa a compilazione. Cambiare il campo non cambiava nulla.
///
/// Reso vivo non e' stato, ed e' una scelta: servirebbe abbandonare il generatore per tutti e sette,
/// perdendo la verifica dei pattern a compilazione, per rendere regolabile un valore che nessun
/// chiamante chiede di cambiare. Un campo che promette un budget e non lo governa e' peggio di un
/// valore compilato e dichiarato: il primo si crede, il secondo si legge.
///
/// Lo stadio a modello ha un budget suo e vero, <c>routing.piiLatencyBudgetMs</c>, che vive nella
/// configurazione e viene letto davvero.
/// </remarks>
public sealed record PrivacyLimits(
    int MaxInputBytes,
    int MaxContextBytes,
    int MaxContextItemBytes,
    int MaxOutputBytes)
{
    /// <summary>Valori di 00-baseline.md, «Scelte quantitative iniziali».</summary>
    public static PrivacyLimits Default { get; } = new(
        MaxInputBytes: 64 * 1024,
        MaxContextBytes: 512 * 1024,
        MaxContextItemBytes: 128 * 1024,
        MaxOutputBytes: 512 * 1024);

    /// <summary>
    /// Istruzioni dell'applicazione, 64 KiB: lo stesso tetto dell'input. Non entrano nel budget del
    /// payload, perche' non si tagliano: un'istruzione a meta' cambierebbe il comportamento chiesto.
    /// </summary>
    public int MaxInstructionsBytes { get; init; } = 64 * 1024;

    /// <summary>Descrizioni e schemi degli strumenti offerti al modello, 256 KiB insieme.</summary>
    public int MaxToolDefinitionsBytes { get; init; } = 256 * 1024;

    /// <summary>Budget totale del payload: input piu' contesto.</summary>
    public int MaxPayloadBytes => MaxInputBytes + MaxContextBytes;
}
