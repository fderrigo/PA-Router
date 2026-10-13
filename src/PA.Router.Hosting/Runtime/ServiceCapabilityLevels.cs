using PA.Router.Abstractions.Contracts;
using PA.Router.Registry.Loading;
using PA.Router.Routing.Demand;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// I livelli di capacita' dei servizi che rispondono, ricavati una volta per turno dal registro (ADR-027): correzioni
/// dell'operatore, poi il suo ordine dei modelli (<see cref="ServiceOrder"/>), poi il livello neutro. Un solo punto per la
/// regola: <see cref="MinimumSufficient.Resolve"/>.
/// </summary>
public static class ServiceCapabilityLevels
{
    /// <summary>Livelli per identificatore, solo per chat e agenti abilitati.</summary>
    /// <param name="snapshot">Configurazione del turno.</param>
    public static IReadOnlyDictionary<string, CapabilityLevels> For(ConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        IReadOnlyDictionary<string, OrderPosition> order = ServiceOrder.Positions(snapshot);

        return snapshot.Registry.Services
            .Where(service => service.Enabled && ServiceSetupCatalog.AnswersRequests(service.Capability))
            .ToDictionary(
                service => service.Id,
                service => MinimumSufficient.Resolve(
                    service.Levels,
                    order.TryGetValue(service.Id, out OrderPosition place) ? (place.Position, place.Count) : null,
                    service.ContextWindowTokens),
                StringComparer.Ordinal);
    }
}
