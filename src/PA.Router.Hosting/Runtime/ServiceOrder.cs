using PA.Router.Abstractions.Contracts;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>Posto di un servizio nell'ordine dei modelli: 0 il piu' capace, e quanti servizi hanno un posto.</summary>
/// <param name="Position">Quanti servizi hanno un posto strettamente migliore: pari posto, pari posizione.</param>
/// <param name="Count">Servizi ordinati.</param>
public readonly record struct OrderPosition(int Position, int Count);

/// <summary>
/// L'ordine dei modelli (<c>registry.services[].rank</c>) e' l'unica scala della qualita' (ADR-032, fase 3). Da qui, e da
/// nessun altro posto, vengono il posto di ciascun servizio, i livelli del minimo sufficiente
/// (<see cref="ServiceCapabilityLevels"/>) e la componente di qualita' del ranking (<see cref="StackView.CandidatesFor"/>).
///
/// Contano i soli servizi **accesi che rispondono** alle richieste — chat e agenti — e che hanno un posto: un servizio
/// spento non sposta nessuno, e un embedding non e' confrontabile con una chat.
/// </summary>
public static class ServiceOrder
{
    /// <summary>Posto di ogni servizio acceso che risponde e ha un <c>rank</c>, per identificatore.</summary>
    public static IReadOnlyDictionary<string, OrderPosition> Positions(ConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ServiceDescriptor[] ranked = [.. snapshot.Registry.Services
            .Where(service => service.Enabled && ServiceSetupCatalog.AnswersRequests(service.Capability) && service.Rank is not null)];

        int[] ranks = [.. ranked.Select(service => service.Rank!.Value)];

        return ranked.ToDictionary(
            service => service.Id,
            service => new OrderPosition(ranks.Count(other => other < service.Rank!.Value), ranks.Length),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Qualita' ricavata dal posto, da 0 a 1: il posto p (1 il primo) su n vale 1 - (p-1)/n, quindi il primo vale 1. Senza
    /// posto null: il ranking usa il neutro (<see cref="TargetCalibration.NeutralScore"/>) e lo dichiara.
    /// </summary>
    public static double? QualityScore(OrderPosition? position) =>
        position is OrderPosition { Count: > 0 } place
            ? Math.Round(1.0 - (double)place.Position / place.Count, 6, MidpointRounding.ToEven)
            : null;
}
