using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Probing;

/// <summary>
/// Controlli che la probe di un adapter osserva da sola quando il servizio risponde (S09, S14, S28, S33). Non concede
/// nulla: un controllo diventa effettivo solo quando la probe lo riporta davvero. Serve al setup per non chiedere
/// all'operatore cio' che il router vede da se', e alla conversione della fase 4 di ADR-032 per confrontare le regole
/// vecchie e nuove sia prima sia dopo l'osservazione. Se una probe comincia a osservare un controllo nuovo, va aggiunto
/// qui.
/// </summary>
public static class ProbeObservations
{
    /// <summary>Controlli osservabili dalla probe dell'adapter.</summary>
    public static IReadOnlySet<Control> Controls(AdapterKind adapter) => adapter switch
    {
        AdapterKind.Ollama or AdapterKind.OllamaEmbed or AdapterKind.OpenAiCompatible or AdapterKind.Anthropic or AdapterKind.OnnxLocal
            => new HashSet<Control> { Control.ClosedPayload },
        _ => new HashSet<Control>(),
    };
}
