namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Zona di un servizio (ADR-029, ADR-032 fase 4): si ricava da dove avviene l'inferenza e dalle conferme **effettive**
/// (dichiarate e osservate o confermate dall'operatore), mai da un campo.
///
/// Dal 25 settembre 2026 una collocazione locale non basta: un servizio su questo computer o sulla rete di casa vale come
/// tale solo se l'elaborazione locale e' effettiva (<c>local_inference</c> osservata o confermata). Senza, di lui si sa
/// solo dove gira il processo, non dove finiscono i token, e vale come un terzo generico. Prima lo decideva
/// <c>requiredControls</c> del profilo, che ogni modalita' consegnata portava.
///
/// Unica implementazione: la usano il valutatore della policy, la conversione di <c>pa config migrate</c> e la resa.
/// </summary>
public static class ServiceZones
{
    private static readonly IReadOnlySet<ProcessingRegion> EuropeanUnion =
        Regions.Expand(new HashSet<ProcessingRegion> { Regions.Parse(Regions.EuropeanUnion) });

    /// <summary>
    /// Zona di un servizio. Una macchina propria fuori sede e' <c>private_eu</c> solo con tutti i luoghi dichiarati dentro
    /// l'Unione; un terzo e' <c>third_party_dpa</c> solo con l'accordo di trattamento effettivo.
    /// </summary>
    public static TrustZone Of(Placement placement, IReadOnlySet<Control> controls, IReadOnlySet<ProcessingRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(regions);

        return placement switch
        {
            Placement.Loopback => controls.Contains(Control.LocalInference) ? TrustZone.SameHost : TrustZone.ThirdParty,
            Placement.Lan => controls.Contains(Control.LocalInference) ? TrustZone.Lan : TrustZone.ThirdParty,
            Placement.PrivateRemote => InEuropeanUnion(regions) ? TrustZone.PrivateEu : TrustZone.ThirdParty,
            _ => controls.Contains(Control.ProcessingAgreement) ? TrustZone.ThirdPartyDpa : TrustZone.ThirdParty,
        };
    }

    /// <summary>
    /// Vero se il servizio gira su questo computer o sulla rete di casa ma l'elaborazione locale non e' confermata: la
    /// sua zona e' quella di un terzo, e la cosa da fare e' confermarla.
    /// </summary>
    public static bool LocalButUnconfirmed(Placement placement, IReadOnlySet<Control> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        return placement is Placement.Loopback or Placement.Lan && !controls.Contains(Control.LocalInference);
    }

    /// <summary>Vero se ogni luogo dichiarato sta nell'Unione europea; un elenco vuoto non ci sta.</summary>
    public static bool InEuropeanUnion(IReadOnlySet<ProcessingRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        IReadOnlySet<ProcessingRegion> expanded = Regions.Expand(regions);
        return expanded.Count > 0 && expanded.IsSubsetOf(EuropeanUnion);
    }
}

/// <summary>
/// Controlli che un servizio deve avere per rispondere a una richiesta (ADR-032, fase 4). Non si configurano: vengono
/// dal tipo di servizio e da cio' che il turno chiede.
///
/// <list type="bullet">
/// <item>Una chat riceve solo il contenuto autorizzato: <c>closed_payload</c>. Era in <c>requiredControls</c> di ogni
/// modalita' consegnata che ammetteva una chat.</item>
/// <item>Un agente deve saper tenere il recinto per cio' che il turno gli concede: leggere il codice
/// (<c>native_agent</c>), scrivere (<c>workspace_write</c>), eseguire comandi (<c>shell_control</c>). Prima la modalita'
/// li pretendeva tutti e tre per ogni turno, anche solo di lettura.</item>
/// <item>Dove va il dato non e' un controllo: lo decide la zona (<see cref="ServiceZones"/>).</item>
/// </list>
/// </summary>
public static class AdmissionControls
{
    /// <summary>Controlli richiesti a un servizio di questa capacita' per le capacita' agentiche del turno.</summary>
    public static IReadOnlySet<Control> For(ServiceCapability capability, IReadOnlySet<Capability> required)
    {
        ArgumentNullException.ThrowIfNull(required);

        HashSet<Control> controls = [];

        switch (capability)
        {
            case ServiceCapability.Chat:
                controls.Add(Control.ClosedPayload);
                break;

            case ServiceCapability.CodeAgent:
                foreach (Capability needed in required)
                {
                    if (ControlFor(needed) is Control control)
                    {
                        controls.Add(control);
                    }
                }

                break;
        }

        return controls;
    }

    /// <summary>Il controllo che rende un agente capace di tenere il recinto per una capacita'; null per la chat.</summary>
    public static Control? ControlFor(Capability capability) => capability switch
    {
        Capability.CodeRead => Control.NativeAgent,
        Capability.FileWrite => Control.WorkspaceWrite,
        Capability.Shell => Control.ShellControl,
        _ => null,
    };
}

/// <summary>
/// La regola delle zone (ADR-029, decisioni H e I): la classe decide la zona piu' lontana ammessa, il perimetro fisso
/// separa dentro e fuori, fuori dal perimetro gli allegati restano dentro e alcune classi aspettano la conferma
/// dell'operatore. Unica implementazione: la chiama il valutatore della policy e la usa la conversione della fase 4.
/// </summary>
public static class ZoneRule
{
    /// <summary>Motivo del rifiuto, null se la zona e' ammessa.</summary>
    /// <param name="zones">Zone del profilo.</param>
    /// <param name="dataClass">Classe effettiva del payload.</param>
    /// <param name="certainty">Completezza della scansione.</param>
    /// <param name="zone">Zona del servizio.</param>
    /// <param name="placement">Collocazione dell'inferenza, per scegliere il motivo.</param>
    /// <param name="hasAttachments">Il turno porta allegati.</param>
    /// <param name="externalConfirmed">L'operatore ha confermato l'uscita dal perimetro per questo turno.</param>
    public static PolicyReason? Evaluate(
        PolicyZones zones,
        DataClass dataClass,
        ScanCertainty certainty,
        TrustZone zone,
        Placement placement,
        bool hasAttachments,
        bool externalConfirmed)
    {
        ArgumentNullException.ThrowIfNull(zones);

        // Una scansione che non conclude nemmeno al secondo tentativo nega anche in locale (S80): il testo non si riesce
        // a classificare, e mandarlo a un modello significa consegnarlo senza sapere che cosa contenga.
        if (zone <= TrustZone.Lan && dataClass is DataClass.Unknown && certainty is ScanCertainty.Incomplete)
        {
            return PolicyReason.DataClassForbidden;
        }

        TrustZone allowed = zones.For(dataClass);

        if (zone > allowed)
        {
            return allowed <= TrustZone.Lan
                ? PolicyReason.DataClassForbidden
                : ServiceDescriptor.IsThirdParty(placement) && allowed < TrustZone.ThirdPartyDpa
                    ? PolicyReason.ExternalForbidden
                    : PolicyReason.ZoneNotAllowed;
        }

        if (zones.Perimeter.Contains(zone))
        {
            return null;
        }

        // Fuori dal perimetro. Gli allegati restano dentro, salvo eccezione del profilo (decisione I).
        if (hasAttachments && !zones.AttachmentsOutsidePerimeter)
        {
            return PolicyReason.ZoneNotAllowed;
        }

        // Un obbligo del profilo, mai una scelta della richiesta: la conferma arriva solo dal terminale.
        return zones.ConfirmOutside.Contains(dataClass) && !externalConfirmed
            ? PolicyReason.ExternalConfirmationRequired
            : null;
    }
}
