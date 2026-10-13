using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Adapters.ClaudeCode;

/// <summary>
/// Probe del programma ufficiale Claude Code.
///
/// Osserva percorso, digest e versione dichiarata. Non dichiara controlli: quelli richiesti dai
/// profili — confini di lettura e scrittura, restrizioni di shell, isolamento della configurazione —
/// non sono stati osservati con un turno reale, e la matrice di S13 li tiene come <c>unknown</c>.
/// </summary>
public sealed class ClaudeCodeProbe(IProcessGuardFactory guards, IClock clock) : ITargetProbe
{
    private readonly IProcessGuardFactory guards = guards;
    private readonly IClock clock = clock;

    /// <inheritdoc />
    public ValueTask<ProbeResult> ProbeAsync(ServiceDescriptor service, CancellationToken cancellationToken) =>
        CliProbe.ObserveAsync(service, guards, clock, cancellationToken);
}
