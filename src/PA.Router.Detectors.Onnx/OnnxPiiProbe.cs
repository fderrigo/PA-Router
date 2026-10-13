using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Detectors.Onnx;

/// <summary>
/// Osservazione del rilevatore PII locale.
///
/// Non carica i pesi e non esegue un'inferenza: guarda se i tre file del modello sono al loro
/// posto nella directory dei modelli. E' quanto basta a <c>doctor</c> per dire se il servizio e'
/// utilizzabile, e non costa il tempo di aprire una sessione ONNX prima di ogni turno.
///
/// Un modello assente viene dichiarato, mai scaricato: un download durante una diagnosi sarebbe
/// un egress che nessuno ha autorizzato, esattamente come per il modello di chat.
/// </summary>
public sealed class OnnxPiiProbe : ITargetProbe
{
    /// <summary>Durata di una osservazione, la stessa degli altri servizi locali.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);

    private readonly string modelsRoot;
    private readonly IClock clock;

    public OnnxPiiProbe(string modelsRoot, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        ArgumentNullException.ThrowIfNull(clock);

        this.modelsRoot = modelsRoot;
        this.clock = clock;
    }

    /// <inheritdoc />
    public ValueTask<ProbeResult> ProbeAsync(ServiceDescriptor service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        cancellationToken.ThrowIfCancellationRequested();

        if (service.Endpoint is not null)
        {
            // Il rilevatore gira in processo: un indirizzo significa che l'operatore si aspetta
            // un servizio che questo adapter non contatta, e crederlo locale sarebbe sbagliato.
            return Result(Unavailable(service, "endpoint_not_supported"));
        }

        if (service.InferencePlacement is not Placement.Loopback)
        {
            return Result(Unavailable(service, "placement_not_loopback"));
        }

        if (service.Model is not string name || name.Length == 0 || service.HasPlaceholders)
        {
            return Result(Unavailable(service, "model_not_configured"));
        }

        if (LocalModelStore.Resolve(modelsRoot, name) is not string directory)
        {
            return Result(Unavailable(service, "model_name_invalid"));
        }

        if (!OnnxPiiModel.IsInstalled(directory))
        {
            // Configurazione da completare, non un guasto: i file li mette l'operatore.
            return Result(Unavailable(service, "model_not_installed"));
        }

        DateTimeOffset now = clock.UtcNow;

        // Il payload non lascia il processo: non c'e' socket, non c'e' programma figlio, e i pesi
        // sono un file locale. `local_inference` resta pero' un'attestazione dell'operatore, come
        // per ogni altro servizio: il processo osserva il proprio codice, non l'ambiente in cui
        // gira (ADR-016).
        // Come per il modello locale: la probe riporta cio' che osserva, e una dichiarazione del registro non e'
        // un'osservazione. `local_inference` lo rende effettivo l'attestazione dell'operatore (S30).
        HashSet<Control> controls = [Control.ClosedPayload];

        return Result(new ProbeResult(
            service.Id,
            ProbeHealth.Healthy,
            AuthStatus.Active,
            QuotaState.Unknown,
            ExecutablePath: null,
            ExecutableHash: LocalModelStore.ReadDeclaredDigest(directory),
            Version: null,
            ProtocolVersion: null,
            Model: name,
            Capabilities: new HashSet<Capability>(),
            Controls: controls,
            ContextWindowTokens: null,
            CheckedAt: now,
            ExpiresAt: now + Freshness,
            FailureCode: null));
    }

    private static ValueTask<ProbeResult> Result(ProbeResult result) => ValueTask.FromResult(result);

    private ProbeResult Unavailable(ServiceDescriptor service, string failure)
    {
        DateTimeOffset now = clock.UtcNow;

        return new ProbeResult(
            service.Id,
            ProbeHealth.Unavailable,
            AuthStatus.Unknown,
            QuotaState.Unknown,
            ExecutablePath: null,
            ExecutableHash: null,
            Version: null,
            ProtocolVersion: null,
            Model: service.Model,
            Capabilities: new HashSet<Capability>(),
            Controls: new HashSet<Control>(),
            ContextWindowTokens: null,
            CheckedAt: now,
            ExpiresAt: now + Freshness,
            FailureCode: failure);
    }
}
