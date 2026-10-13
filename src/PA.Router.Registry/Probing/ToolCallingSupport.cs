using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Probing;

/// <summary>
/// Chi sa usare strumenti (ADR-020). Un modello locale lo dice il modello installato; un'API remota non lo
/// lascia osservare senza consumare quota, e resta dichiarato nel registro. Agenti e altre capacita' non
/// ricevono strumenti delle applicazioni.
/// </summary>
public static class ToolCallingSupport
{
    /// <summary>Vero se il servizio puo' ricevere una richiesta con strumenti.</summary>
    public static bool Of(ServiceDescriptor service, ProbeResult? probe)
    {
        ArgumentNullException.ThrowIfNull(service);

        return service.Capability is ServiceCapability.Chat
            && service.Adapter switch
            {
                AdapterKind.Ollama => probe?.ToolCalling == true,
                AdapterKind.OpenAiCompatible or AdapterKind.Anthropic => service.ToolCalling,
                _ => false,
            };
    }
}
