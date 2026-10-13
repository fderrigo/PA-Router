using PA.Router.Abstractions.Contracts;

namespace PA.Router.Policy;

/// <summary>
/// Candidato da valutare: dichiarazione del registro gia' intersecata con l'osservazione del
/// probe.
///
/// Capacita' e controlli qui dentro sono gia' quelli <em>effettivi</em>. La valutazione non
/// guarda mai le sole dichiarazioni: un controllo dichiarato ma non osservato non conta, ed e'
/// esattamente la differenza fra un'autorizzazione e una promessa (03-specs/REGISTRY.md).
/// </summary>
/// <param name="TargetId">Identificatore del servizio.</param>
/// <param name="Capability">Capacita' offerta; e' la dimensione su cui il router instrada.</param>
/// <param name="Adapter">Adapter che lo raggiunge.</param>
/// <param name="AuthMode">Autenticazione dichiarata.</param>
/// <param name="InferencePlacement">Dove avviene l'inferenza: e' cio' che i gate guardano.</param>
/// <param name="Enabled">Abilitazione dell'operatore.</param>
/// <param name="Capabilities">Capacita' effettive.</param>
/// <param name="Controls">Controlli effettivi, osservati dal probe.</param>
/// <param name="ProcessingRegions">Regioni dichiarate: decidono se una macchina propria fuori sede sta in UE.</param>
/// <param name="HasFreshProbe">Vero se esiste un'osservazione non scaduta.</param>
/// <param name="AuthStatus">Autenticazione osservata.</param>
/// <param name="QuotaState">Quota osservata; <c>Unknown</c> non equivale a quota residua.</param>
public sealed record PolicyCandidate(
    string TargetId,
    ServiceCapability Capability,
    AdapterKind Adapter,
    AuthMode AuthMode,
    Placement InferencePlacement,
    bool Enabled,
    IReadOnlySet<Capability> Capabilities,
    IReadOnlySet<Control> Controls,
    IReadOnlySet<ProcessingRegion> ProcessingRegions,
    bool HasFreshProbe,
    AuthStatus AuthStatus,
    QuotaState QuotaState)
{
    /// <summary>
    /// Calibrazione dichiarata per il target. Non partecipa ad alcun controllo: viene solo
    /// trasportata nell'ammissibilita', perche' il ranking non possa procurarsela altrove.
    /// </summary>
    public TargetCalibration Calibration { get; init; } = TargetCalibration.Unknown;

    /// <summary>
    /// Finestra di contesto effettiva, minimo fra dichiarata e osservata. Serve al requisito di
    /// banda, che e' un gate: una finestra insufficiente non si compensa con un punteggio.
    /// </summary>
    public int ContextWindowTokens { get; init; }

    /// <summary>
    /// Vero se il servizio sa usare strumenti: osservato dal modello installato per Ollama, dichiarato nel
    /// registro per le API remote (ADR-020). Una richiesta con strumenti va solo a chi sa usarli.
    /// </summary>
    public bool SupportsTools { get; init; }

    /// <summary>
    /// Tetto di uscita dichiarato dal servizio (<c>maxOutputTokens</c>), 0 se non dichiarato. Serve a dire quanto puo'
    /// costare al massimo una richiesta (<see cref="CostCeiling"/>): l'adapter lo fa rispettare.
    /// </summary>
    public int MaxOutputTokens { get; init; }

    /// <summary>Tipo derivato dalla capacita'; non e' un campo del registro.</summary>
    public TargetKind Kind => Capability is ServiceCapability.CodeAgent ? TargetKind.Agent : TargetKind.Llm;

    /// <summary>Confine derivato dalla collocazione dell'inferenza.</summary>
    public Boundary Boundary =>
        ServiceDescriptor.LeavesMachine(InferencePlacement) ? Boundary.Cloud : Boundary.Local;
}

/// <summary>
/// Fatti della richiesta che la policy deve considerare. Nessun contenuto: solo la classe
/// risultante dall'analisi privacy, i digest e le misure.
/// </summary>
/// <param name="Channel">Canale della sessione, fissato alla creazione.</param>
/// <param name="Intent">Intento dichiarato dal comando.</param>
/// <param name="RoutedCapability">
/// Capacita' che la richiesta sta instradando. Un servizio di un'altra capacita' non e' un
/// candidato peggiore: non e' un candidato (ADR-015).
/// </param>
/// <param name="RequiredCapabilities">Capacita' necessarie a soddisfare la richiesta.</param>
/// <param name="EffectiveClass">Classe effettiva del payload, gia' comprensiva del floor.</param>
/// <param name="Certainty">Completezza della scansione privacy.</param>
/// <param name="PayloadDigest">Digest del payload autorizzando.</param>
/// <param name="ContextManifestDigest">Digest del manifest del contesto.</param>
/// <param name="InputBytes">Byte dell'input.</param>
/// <param name="ContextBytes">Byte del contesto incluso.</param>
/// <param name="SatisfiedObligations">Obblighi gia' applicati al payload.</param>
/// <param name="SnapshotDigest">Digest della configurazione usata.</param>
/// <param name="RequestedTargetId">Servizio richiesto esplicitamente; e' un vincolo in piu'.</param>
/// <param name="Band">
/// Banda di difficolta' stimata, con i requisiti che ne derivano. Null finche' l'analisi
/// deterministica non esiste (S26): senza banda il gate dei requisiti non si applica.
/// </param>
public sealed record PolicyRequest(
    Channel Channel,
    Intent Intent,
    ServiceCapability RoutedCapability,
    IReadOnlySet<Capability> RequiredCapabilities,
    DataClass EffectiveClass,
    ScanCertainty Certainty,
    string PayloadDigest,
    string ContextManifestDigest,
    int InputBytes,
    int ContextBytes,
    IReadOnlySet<Obligation> SatisfiedObligations,
    string SnapshotDigest,
    string? RequestedTargetId = null,
    BandRequirements? Band = null)
{
    /// <summary>
    /// Vero se il turno porta allegati (ADR-029, decisione I): per default restano nel perimetro del profilo.
    /// </summary>
    public bool HasAttachments { get; init; }

    /// <summary>
    /// Vero se l'operatore ha confermato, nel terminale e per questo turno, l'uscita dal perimetro che il profilo
    /// sottopone a conferma. Il canale di servizio non la porta mai.
    /// </summary>
    public bool ExternalConfirmed { get; init; }

    /// <summary>
    /// Token stimati della richiesta con il suo contesto. Servono al tetto di spesa (ADR-032, fase 5): quanto puo' costare
    /// al massimo la richiesta su un servizio. Zero quando il chiamante non stima, e allora conta la sola uscita.
    /// </summary>
    public int EstimatedInputTokens { get; init; }

}
