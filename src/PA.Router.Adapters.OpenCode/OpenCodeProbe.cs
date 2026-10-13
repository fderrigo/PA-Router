using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Adapters.OpenCode;

/// <summary>
/// Probe del programma ufficiale OpenCode.
///
/// Come per gli altri due, osserva percorso, digest e versione e **non dichiara alcun controllo**.
/// Qui la riserva e' piu' forte che altrove: i permessi vengono imposti dalla configurazione che il
/// router scrive, ma quella configurazione si fonde con quella dell'utente, e un agente definito da
/// lui puo' portarsi i propri permessi. Il controllo va attestato dall'operatore, non dedotto.
/// </summary>
public sealed class OpenCodeProbe(IProcessGuardFactory guards, IClock clock) : ITargetProbe
{
    private readonly IProcessGuardFactory guards = guards;
    private readonly IClock clock = clock;

    /// <inheritdoc />
    public ValueTask<ProbeResult> ProbeAsync(ServiceDescriptor service, CancellationToken cancellationToken) =>
        CliProbe.ObserveAsync(service, guards, clock, cancellationToken);
}
