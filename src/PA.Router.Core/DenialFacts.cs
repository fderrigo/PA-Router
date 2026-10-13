using System.Globalization;
using PA.Router.Abstractions.Contracts;
using PA.Router.Policy;

namespace PA.Router.Core;

/// <summary>
/// Costruisce il <see cref="DenialDetail"/> di un turno negato: i motivi gia' decisi dal valutatore,
/// **piu' i valori** che li hanno decisi.
///
/// Non decide niente e non rivaluta niente: legge le decisioni del valutatore e ci accosta i numeri
/// del candidato e della banda, che in quel momento sono in mano. E' una traduzione, e sta qui
/// perche' e' l'unico punto in cui decisioni, candidati e banda esistono insieme.
/// </summary>
public static class DenialFacts
{
    /// <summary>Capacita' che fanno di un servizio uno che **risponde a una richiesta**.</summary>
    private static readonly ServiceCapability[] Answering = [ServiceCapability.Chat, ServiceCapability.CodeAgent];

    /// <summary>Dettaglio del rifiuto, oppure null se non c'e' nessuna decisione negativa.</summary>
    /// <param name="explain">Decisioni del valutatore.</param>
    /// <param name="candidates">Candidati valutati, con la finestra osservata.</param>
    /// <param name="band">Finestra richiesta, quando il chiamante la stima.</param>
    /// <param name="difficulty">Nome della banda, in forma wire.</param>
    /// <param name="zones">
    /// Zone del profilo, classe della richiesta e istante della decisione (ADR-029): un rifiuto per zona dice la zona del
    /// servizio e quella ammessa per la classe. Null quando il chiamante non valuta zone.
    /// </param>
    /// <param name="cost">
    /// Policy compilata, token stimati della richiesta e valuta del registro (ADR-032, fase 5): un rifiuto per il tetto di
    /// spesa dice quanto puo' costare al massimo la richiesta sul servizio e quanto vale il tetto. Null quando il
    /// chiamante non li ha.
    /// </param>
    public static DenialDetail? Build(
        PolicyExplain explain,
        IReadOnlyList<PolicyCandidate> candidates,
        BandRequirements? band,
        string? difficulty,
        (PolicyZones Zones, DataClass Class, DateTimeOffset Now)? zones = null,
        (CompiledPolicy Policy, int InputTokens, string? Currency)? cost = null)
    {
        ArgumentNullException.ThrowIfNull(explain);
        ArgumentNullException.ThrowIfNull(candidates);

        List<ServiceDenial> services = [];

        foreach (PolicyDecision decision in explain.Decisions.Where(decision => !decision.Allowed))
        {
            PolicyCandidate? candidate = candidates.FirstOrDefault(item => item.TargetId == decision.TargetId);
            bool disabled = decision.Reasons.Contains(PolicyReason.TargetDisabled);
            bool answers = candidate is null || Answering.Contains(candidate.Capability);

            services.Add(new ServiceDenial(
                decision.TargetId,
                decision.TargetId,
                answers,
                disabled,
                // Un servizio spento non ha motivi da elencare: non e' stato valutato, e' stato
                // lasciato fuori. Un ausiliario nemmeno: non risponde alle richieste, e i cinque
                // motivi di policy che raccoglie sono veri e inutili.
                disabled || !answers ? [] : [.. Facts(decision, candidate, band, zones, cost)]));
        }

        return services.Count == 0
            ? null
            : new DenialDetail(
                difficulty,
                band?.MinContextTokens,
                // Prima chi poteva rispondere: chi e' spento non e' un guasto e non va in cima.
                [.. services.OrderBy(service => service.Disabled ? 1 : 0).ThenBy(service => service.ServiceId, StringComparer.Ordinal)]);
    }

    private static IEnumerable<DenialFact> Facts(
        PolicyDecision decision,
        PolicyCandidate? candidate,
        BandRequirements? band,
        (PolicyZones Zones, DataClass Class, DateTimeOffset Now)? zones,
        (CompiledPolicy Policy, int InputTokens, string? Currency)? cost)
    {
        foreach (PolicyReason reason in decision.Reasons)
        {
            yield return reason switch
            {
                PolicyReason.ContextWindowInsufficient when candidate is not null && band is BandRequirements window =>
                    new DenialFact(
                        reason.ToWire(),
                        candidate.ContextWindowTokens.ToString(CultureInfo.InvariantCulture),
                        window.MinContextTokens.ToString(CultureInfo.InvariantCulture)),

                PolicyReason.ZoneNotAllowed or PolicyReason.ExternalForbidden or PolicyReason.DataClassForbidden
                    or PolicyReason.ExternalConfirmationRequired when candidate is not null && zones is { } zone =>
                    new DenialFact(
                        reason.ToWire(),
                        TrustZones.Of(candidate).ToWire(),
                        zone.Zones.For(zone.Class).ToWire()),

                // Quanto puo' costare al massimo contro il tetto, con la valuta. Un prezzo sconosciuto non ha una cifra:
                // resta vuoto, e la resa dice perche' un tetto lo esclude.
                PolicyReason.CostLimit when candidate is not null && cost is { Policy.MaxCostPerRequest: double cap } spend =>
                    new DenialFact(
                        reason.ToWire(),
                        CostCeiling.Of(candidate, spend.InputTokens, spend.Policy.MaxOutputBytes) is double ceiling
                            ? Amount(ceiling, spend.Currency)
                            : null,
                        Amount(cap, spend.Currency)),

                PolicyReason.ControlUnavailable when candidate is not null =>
                    new DenialFact(reason.ToWire(), Controls(candidate)),

                PolicyReason.CapabilityMissing when candidate is not null =>
                    new DenialFact(reason.ToWire(), Capabilities(candidate)),

                _ => new DenialFact(reason.ToWire()),
            };
        }
    }

    /// <summary>
    /// Un importo come l'operatore lo scriverebbe in <c>config.yaml</c>: punto decimale, nessun separatore delle migliaia,
    /// al massimo sei decimali (S116).
    /// </summary>
    private static string Amount(double value, string? currency) =>
        value.ToString("0.######", CultureInfo.InvariantCulture) + (currency is null ? string.Empty : " " + currency);

    private static string Controls(PolicyCandidate candidate) =>
        string.Join(", ", candidate.Controls.Select(control => control.ToWire()).Order(StringComparer.Ordinal));

    private static string Capabilities(PolicyCandidate candidate) =>
        string.Join(", ", candidate.Capabilities.Select(capability => capability.ToWire()).Order(StringComparer.Ordinal));
}
