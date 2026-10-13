using PA.Router.Abstractions.Contracts;

namespace PA.Router.Sdk;

/// <summary>
/// Cio' che un'applicazione esterna consegna per un turno (03-specs/HEADLESS-API-SDK.md, ADR-020).
///
/// Nulla qui sceglie profilo, servizio o capacita': li fissa la fabbrica del runtime. Domanda, contesto e
/// istruzioni sono **contenuto**, e riattraversano privacy e policy come ogni altro testo.
/// </summary>
/// <param name="Input">Domanda dell'utente dell'applicazione.</param>
/// <param name="ContextItems">Contenuti allegati; restano dati non fidati.</param>
/// <param name="Instructions">
/// Istruzioni dell'applicazione, null se assenti. Vanno al modello nel ruolo di sistema del fornitore:
/// descrivono un comportamento chiesto al modello, non una garanzia del router.
/// </param>
/// <param name="Tools">
/// Sorgenti degli strumenti, dichiarazioni e credenziali del turno (ADR-020); null usa le sorgenti della
/// configurazione del router, se ce ne sono.
/// </param>
public sealed record ApplicationRequest(
    string Input,
    IReadOnlyList<ContextItem> ContextItems,
    string? Instructions = null,
    ToolsRequest? Tools = null)
{
    /// <summary>Nessun contenuto nella resa testuale.</summary>
    public override string ToString() =>
        "ApplicationRequest { ContextItems = "
        + ContextItems.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ", Instructions = " + (Instructions is null ? "no" : "yes")
        + ", Tools = " + (Tools is null ? "no" : "yes") + " }";
}
