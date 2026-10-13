using System.Net;
using System.Net.Sockets;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Abstractions;

/// <summary>
/// Regola di rete di ciascuna collocazione (ADR-016). La regola vecchia non e' stata allentata:
/// e' stata sostituita da quattro regole, una per collocazione, tutte altrettanto strette.
///
/// | Collocazione | Regola |
/// |---|---|
/// | loopback | indirizzo numerico di loopback, http, nessun proxy, nessun redirect |
/// | lan | indirizzo privato numerico (RFC 1918 o fc00::/7), nessun nome, nessun redirect |
/// | private_remote | TLS con certificato verificato, nessun redirect |
/// | third_party | come sopra, piu' i gate di classe del dato e di elaborazione esterna |
///
/// Un nome al posto di un indirizzo viene rifiutato dove la collocazione promette una macchina
/// precisa: la risoluzione non e' sotto il controllo del router.
/// </summary>
public static class PlacementRules
{
    /// <summary>Motivo per cui l'endpoint non soddisfa la collocazione, null se la soddisfa.</summary>
    public static string? Violation(Placement placement, Uri? endpoint)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri)
        {
            return "endpoint_missing";
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            // Credenziali nell'indirizzo finirebbero nei log e nelle diagnostiche.
            return "endpoint_has_credentials";
        }

        if (!string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            return "endpoint_not_origin";
        }

        // Loopback e rete locale promettono una macchina precisa: niente percorso, che potrebbe nascondere un proxy verso un
        // altro host. Un servizio remoto puo' avere un prefisso (23 settembre 2026): Scaleway mette il progetto nel percorso,
        // Azure il deployment. I dati vanno comunque all'host dichiarato, con il certificato verificato; il prefisso deve
        // finire con «/», perche' l'adapter vi aggiunge il proprio percorso invece di sostituirlo.
        bool remote = placement is Placement.PrivateRemote or Placement.ThirdParty;

        if (!remote && endpoint.AbsolutePath is not ("/" or ""))
        {
            return "endpoint_not_origin";
        }

        if (remote && !endpoint.AbsolutePath.EndsWith('/'))
        {
            return "endpoint_path_not_directory";
        }

        return placement switch
        {
            Placement.Loopback => LocalEndpoint.IsAcceptable(endpoint) ? null : "endpoint_not_loopback",
            Placement.Lan => LanViolation(endpoint),
            Placement.PrivateRemote or Placement.ThirdParty => RemoteViolation(endpoint),
            _ => "placement_unknown",
        };
    }

    /// <summary>Vero se l'endpoint soddisfa la regola della collocazione.</summary>
    public static bool IsAcceptable(Placement placement, Uri? endpoint) => Violation(placement, endpoint) is null;

    /// <summary>Verifica l'endpoint o lancia un errore tipizzato.</summary>
    public static Uri Require(Placement placement, Uri? endpoint)
    {
        if (Violation(placement, endpoint) is not string reason)
        {
            return endpoint!;
        }

        throw new PaRouterException(
            ErrorCode.ControlUnavailable,
            "The endpoint does not satisfy the rule of its declared placement.")
            .WithMetadata("reason", reason);
    }

    /// <summary>
    /// Handler HTTP della collocazione: mai un proxy, mai un redirect seguito, mai un cookie.
    /// Per le collocazioni remote la verifica del certificato resta quella predefinita della
    /// piattaforma: non esiste un percorso che la disattivi.
    /// </summary>
    public static HttpMessageHandler CreateHandler(Placement placement) => new SocketsHttpHandler
    {
        UseProxy = false,
        Proxy = null,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = placement is Placement.Loopback ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(15),
    };

    /// <summary>
    /// Client per una generazione: l'handler della collocazione, senza scadenza propria. Il tempo di un turno lo fissa
    /// la deadline della policy; una scadenza del client la anticiperebbe a cento secondi, tagliando a meta' una
    /// generazione locale lunga e chiamandola timeout del fornitore.
    /// </summary>
    public static HttpClient CreateModelClient(Placement placement) =>
        new(CreateHandler(placement)) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private static string? LanViolation(Uri endpoint)
    {
        if (endpoint.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return "endpoint_not_numeric";
        }

        if (!IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out IPAddress? address))
        {
            return "endpoint_not_numeric";
        }

        return IsPrivate(address) ? null : "endpoint_not_private";
    }

    private static string? RemoteViolation(Uri endpoint) =>
        string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ? null : "endpoint_not_tls";

    /// <summary>
    /// Indirizzi che appartengono alla rete dell'operatore: RFC 1918 per IPv4, fc00::/7 per IPv6.
    /// Il loopback non vale come LAN: ha una collocazione propria, e confonderle renderebbe
    /// inutile la distinzione.
    /// </summary>
    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily is AddressFamily.InterNetwork)
        {
            byte[] octets = address.GetAddressBytes();

            return octets[0] switch
            {
                10 => true,
                172 => octets[1] is >= 16 and <= 31,
                192 => octets[1] is 168,
                _ => false,
            };
        }

        if (address.AddressFamily is AddressFamily.InterNetworkV6)
        {
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        return false;
    }
}
