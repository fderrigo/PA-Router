using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Probing;

/// <summary>
/// Servizio dichiarato piu' osservazione, con capacita' e controlli effettivi. La dichiarazione da
/// sola non abilita nulla: l'effettivo e' l'intersezione fra cio' che l'operatore dichiara e cio'
/// che il probe osserva (03-specs/REGISTRY.md).
/// </summary>
/// <param name="Service">Servizio dichiarato nel registro.</param>
/// <param name="Probe">Osservazione, null se il servizio non e' mai stato osservato.</param>
/// <param name="Capabilities">Capacita' effettive.</param>
/// <param name="Controls">Controlli effettivi.</param>
/// <param name="ContextWindowTokens">Finestra effettiva, minimo fra dichiarata e osservata.</param>
/// <param name="Blockers">Motivi per cui il servizio non e' utilizzabile adesso.</param>
public sealed record EffectiveService(
    ServiceDescriptor Service,
    ProbeResult? Probe,
    IReadOnlySet<Capability> Capabilities,
    IReadOnlySet<Control> Controls,
    int ContextWindowTokens,
    IReadOnlyList<string> Blockers)
{
    /// <summary>Vero se nessun motivo ne impedisce l'uso.</summary>
    public bool IsUsable => Blockers.Count == 0;
}

/// <summary>Motivi stabili per cui un servizio non e' utilizzabile.</summary>
public static class ServiceBlockers
{
    /// <summary>L'operatore non lo ha abilitato.</summary>
    public const string Disabled = "SERVICE_DISABLED";

    /// <summary>Non esiste alcuna osservazione.</summary>
    public const string NotProbed = "SERVICE_NOT_PROBED";

    /// <summary>L'osservazione e' scaduta.</summary>
    public const string ProbeExpired = "PROBE_EXPIRED";

    /// <summary>Il servizio e' stato osservato non disponibile.</summary>
    public const string Unhealthy = "SERVICE_UNHEALTHY";

    /// <summary>L'autenticazione osservata non e' attiva.</summary>
    public const string AuthInactive = "AUTH_INACTIVE";

    /// <summary>La quota risulta esaurita.</summary>
    public const string QuotaExhausted = "QUOTA_EXHAUSTED";

    /// <summary>Il modello osservato non e' quello dichiarato.</summary>
    public const string ModelMismatch = "MODEL_MISMATCH";

    /// <summary>Il programma osservato non e' quello dichiarato.</summary>
    public const string ExecutableMismatch = "EXECUTABLE_MISMATCH";
}

/// <summary>
/// Proiezione del registro sulle osservazioni. Non decide nulla: prepara i fatti su cui policy e
/// ranking lavoreranno negli step successivi.
/// </summary>
public static class RegistryProjection
{
    /// <summary>Calcola l'effettivo di un servizio a un certo istante.</summary>
    public static EffectiveService Project(ServiceDescriptor service, ProbeResult? probe, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(clock);

        List<string> blockers = [];

        if (!service.Enabled)
        {
            blockers.Add(ServiceBlockers.Disabled);
        }

        if (probe is null)
        {
            blockers.Add(ServiceBlockers.NotProbed);

            return new EffectiveService(
                service,
                null,
                new HashSet<Capability>(),
                new HashSet<Control>(),
                service.ContextWindowTokens,
                blockers);
        }

        if (!probe.IsFreshAt(clock.UtcNow))
        {
            blockers.Add(ServiceBlockers.ProbeExpired);
        }

        if (probe.Health is not ProbeHealth.Healthy)
        {
            blockers.Add(ServiceBlockers.Unhealthy);
        }

        if (service.AuthMode is not AuthMode.None && probe.AuthStatus is not AuthStatus.Active)
        {
            blockers.Add(ServiceBlockers.AuthInactive);
        }

        if (probe.QuotaState is QuotaState.Exhausted)
        {
            blockers.Add(ServiceBlockers.QuotaExhausted);
        }

        // Un modello dichiarato deve corrispondere all'osservato: un alias che cambia sotto la
        // dichiarazione renderebbe la decisione presa su un altro modello. Se la probe non osserva
        // alcun modello — e' il caso degli agenti, che scelgono al lancio — non c'e' nulla da
        // confrontare, e assumere un disallineamento bloccherebbe un servizio che va benissimo.
        if (service.Model is not null
            && probe.Model is not null
            && !string.Equals(service.Model, probe.Model, StringComparison.Ordinal))
        {
            blockers.Add(ServiceBlockers.ModelMismatch);
        }

        if (service.Executable is not null
            && probe.ExecutablePath is not null
            && !string.Equals(service.Executable, probe.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(ServiceBlockers.ExecutableMismatch);
        }

        HashSet<Capability> capabilities = [.. service.Capabilities];
        capabilities.IntersectWith(probe.Capabilities);

        // Controlli effettivi: dichiarati ∩ (osservati ∪ attestati e non scaduti).
        //
        // Alcuni controlli il processo non li osserva e non li osservera' mai — che un programma
        // non tenga un transcript persistente non si vede da fuori. Per quelli l'attestazione
        // dell'operatore, con una data e una scadenza, e' cio' che li rende effettivi. Resta
        // un'attestazione: `doctor` la mostra come tale e non come un'osservazione (ADR-017).
        HashSet<Control> known = [.. probe.Controls];

        foreach ((Control control, PropertyAttestation attestation) in service.Attestations)
        {
            if (attestation.IsValidAt(clock.UtcNow))
            {
                known.Add(control);
            }
        }

        HashSet<Control> controls = [.. service.Controls];
        controls.IntersectWith(known);

        int contextWindow = probe.ContextWindowTokens is int observed
            ? Math.Min(service.ContextWindowTokens, observed)
            : service.ContextWindowTokens;

        return new EffectiveService(service, probe, capabilities, controls, contextWindow, blockers);
    }
}
