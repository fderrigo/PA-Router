using PA.Router.Abstractions.Contracts;

namespace PA.Router.Policy;

/// <summary>
/// Regola degli argomenti di una chiamata a uno strumento (ADR-020). Uno strumento e' una destinazione dei
/// dati come un modello: gli argomenti ci vanno solo se la loro classe e' ammessa da cio' che lo strumento ha
/// dichiarato **e** dal profilo, con le stesse regole che valgono per l'inferenza.
/// </summary>
public static class ToolPolicy
{
    /// <summary>
    /// Motivo per cui gli argomenti non possono andare allo strumento, null se possono. I motivi sono stabili
    /// e finiscono nel riscontro all'applicazione.
    /// </summary>
    public static string? ArgumentsDenial(
        CompiledPolicy policy,
        ToolDestination destination,
        DataClass argumentClass,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(destination);

        if (argumentClass is DataClass.Secret or DataClass.Unknown)
        {
            // Un segreto o un testo non classificabile non vanno a uno strumento, nemmeno locale: nessuna
            // dichiarazione di una sorgente vale abbastanza.
            return "data_class_forbidden";
        }

        if ((int)argumentClass > (int)destination.MaxArgumentClass)
        {
            return "above_declared_class";
        }

        if (!ServiceDescriptor.LeavesMachine(destination.Placement))
        {
            return null;
        }

        // Zone (ADR-029): di una sorgente si conoscono collocazione, luoghi dichiarati e scadenza, non un accordo di
        // trattamento. Nessuna conferma nel giro degli strumenti: fuori dal perimetro con conferma vale come negato.
        TrustZone zone = TrustZones.OfDestination(destination.Placement, destination.Regions, destination.EvidenceExpiresAt, now);
        TrustZone allowed = policy.Zones.For(argumentClass);

        if (zone > allowed)
        {
            return allowed <= TrustZone.Lan
                ? "data_class_forbidden"
                : ServiceDescriptor.IsThirdParty(destination.Placement) && allowed < TrustZone.ThirdPartyDpa
                    ? "external_forbidden"
                    : "zone_not_allowed";
        }

        if (!policy.Zones.Perimeter.Contains(zone) && policy.Zones.ConfirmOutside.Contains(argumentClass))
        {
            return "confirmation_required";
        }

        // Le regioni richieste dal profilo non esistono piu' (ADR-032, fase 4): un proprio server in UE e' gia' la zona
        // private_eu, e un terzo lo decide la zona.
        return null;
    }
}
