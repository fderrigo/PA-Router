using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Probing;

/// <summary>Condizione del verdetto «privacy ready» che non ha tenuto.</summary>
public enum PrivacyReadyCondition
{
    /// <summary>L'inferenza esce dalla macchina dell'operatore.</summary>
    [WireValue("inference_leaves_machine")]
    InferenceLeavesMachine,

    /// <summary>Un controllo richiesto non e' dichiarato nel registro.</summary>
    [WireValue("control_not_declared")]
    ControlNotDeclared,

    /// <summary>Un controllo dichiarato ma non osservabile non ha attestazione.</summary>
    [WireValue("attestation_missing")]
    AttestationMissing,

    /// <summary>
    /// L'attestazione c'e' ma non vale: senza nome o senza data della verifica. Il nome sul filo resta quello di prima;
    /// dal 25 settembre 2026 (ADR-032, fase 4) una conferma non scade.
    /// </summary>
    [WireValue("attestation_expired")]
    AttestationExpired,

    /// <summary>Fra gli argomenti dichiarati compare uno di quelli vietati.</summary>
    [WireValue("forbidden_argument")]
    ForbiddenArgument,

    /// <summary>Il servizio non e' stato osservato di recente.</summary>
    [WireValue("probe_stale")]
    ProbeStale,

    /// <summary>Il servizio non e' stato osservato in salute.</summary>
    [WireValue("probe_unhealthy")]
    ProbeUnhealthy,
}

/// <summary>
/// Una condizione caduta, con il dettaglio che la rende leggibile all'operatore.
/// </summary>
/// <param name="Condition">Quale condizione.</param>
/// <param name="Detail">Controllo o argomento coinvolto, quando ce n'e' uno.</param>
public sealed record PrivacyReadyFailure(PrivacyReadyCondition Condition, string? Detail);

/// <summary>
/// Verdetto «privacy ready» di un servizio agentico.
/// </summary>
/// <param name="Ready">Vero solo se nessuna condizione e' caduta.</param>
/// <param name="Failures">Condizioni cadute, in ordine di verifica.</param>
public sealed record PrivacyReadyVerdict(bool Ready, IReadOnlyList<PrivacyReadyFailure> Failures);

/// <summary>
/// Calcolo del verdetto «privacy ready» (ADR-017, 03-specs/NESTED-AGENTS.md).
///
/// E' **calcolato**, mai scritto nel file: nessun campo della configurazione puo' impostarlo, e
/// nessun percorso del router accetta un verdetto dall'esterno. Non esiste un verdetto parziale —
/// se una sola condizione cade, il verdetto e' falso e si dice **quale**.
///
/// Le condizioni sono quelle della specifica, verificate in quest'ordine: collocazione
/// dell'inferenza, controlli dichiarati, attestazioni valide, argomenti vietati, osservazione
/// fresca e in salute.
/// </summary>
public static class PrivacyReadiness
{
    /// <summary>
    /// Controlli che un agente deve dichiarare per essere utilizzabile con dati riservati.
    ///
    /// Non sono osservabili fino in fondo dal processo: l'argomento si osserva, il comportamento
    /// no. Per questo ciascuno richiede anche una conferma dell'operatore, con nome e data.
    /// </summary>
    public static IReadOnlyList<Control> RequiredControls { get; } =
        [
            Control.ConfigIsolated,
            Control.NoNestedTools,
            Control.NoPersistentTranscript,
            Control.NoNestedFallback,
        ];

    /// <summary>Calcola il verdetto per il servizio indicato.</summary>
    /// <param name="service">Servizio dichiarato nel registro.</param>
    /// <param name="probe">Osservazione piu' recente, null se il servizio non e' stato osservato.</param>
    /// <param name="now">Istante di valutazione.</param>
    public static PrivacyReadyVerdict Evaluate(ServiceDescriptor service, ProbeResult? probe, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(service);

        List<PrivacyReadyFailure> failures = [];

        if (ServiceDescriptor.LeavesMachine(service.InferencePlacement))
        {
            // Il gate guarda dove finiscono i token, non dove gira il processo.
            failures.Add(new PrivacyReadyFailure(
                PrivacyReadyCondition.InferenceLeavesMachine,
                service.InferencePlacement.ToWire()));
        }

        foreach (Control control in RequiredControls)
        {
            if (!service.Controls.Contains(control))
            {
                failures.Add(new PrivacyReadyFailure(PrivacyReadyCondition.ControlNotDeclared, control.ToWire()));
                continue;
            }

            if (!service.Attestations.TryGetValue(control, out PropertyAttestation? attestation))
            {
                // Dichiarare un controllo non lo rende osservato: qualcuno deve averlo visto, e
                // averlo scritto con una data.
                failures.Add(new PrivacyReadyFailure(PrivacyReadyCondition.AttestationMissing, control.ToWire()));
                continue;
            }

            if (!attestation.IsValidAt(now))
            {
                failures.Add(new PrivacyReadyFailure(PrivacyReadyCondition.AttestationExpired, control.ToWire()));
            }
        }

        if (AgentArgumentPolicy.FirstForbidden(service.Adapter, service.Arguments) is string forbidden)
        {
            failures.Add(new PrivacyReadyFailure(PrivacyReadyCondition.ForbiddenArgument, forbidden));
        }

        if (probe is null || !probe.IsFreshAt(now))
        {
            failures.Add(new PrivacyReadyFailure(PrivacyReadyCondition.ProbeStale, null));
        }
        else if (probe.Health is not ProbeHealth.Healthy)
        {
            failures.Add(new PrivacyReadyFailure(PrivacyReadyCondition.ProbeUnhealthy, probe.Health.ToWire()));
        }

        return new PrivacyReadyVerdict(failures.Count == 0, failures);
    }

    /// <summary>
    /// Grado di conoscenza di un controllo, come lo mostra <c>doctor</c>: osservato dal router,
    /// attestato dall'operatore, oppure ignoto. Non si confondono mai in una spunta sola.
    /// </summary>
    public static string GradeOf(ServiceDescriptor service, ProbeResult? probe, Control control, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (probe is not null && probe.IsFreshAt(now) && probe.Controls.Contains(control))
        {
            return "osservato";
        }

        if (service.Attestations.TryGetValue(control, out PropertyAttestation? attestation))
        {
            return attestation.IsValidAt(now) ? "attestato" : "attestazione scaduta";
        }

        return "ignoto";
    }
}
