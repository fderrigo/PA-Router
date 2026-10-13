using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Adapters.Codex;

/// <summary>
/// Probe del programma ufficiale Codex CLI.
///
/// Come per Claude Code, osserva percorso, digest e versione e non dichiara alcun controllo: la
/// sandbox viene imposta a ogni invocazione dall'adapter, ma il suo effetto non e' stato osservato
/// e non viene dichiarato come ottenuto.
/// </summary>
public sealed class CodexProbe(IProcessGuardFactory guards, IClock clock) : ITargetProbe
{
    private readonly IProcessGuardFactory guards = guards;
    private readonly IClock clock = clock;

    /// <inheritdoc />
    public ValueTask<ProbeResult> ProbeAsync(ServiceDescriptor service, CancellationToken cancellationToken) =>
        CliProbe.ObserveAsync(service, guards, clock, cancellationToken);
}
