using System.Net;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Abstractions;

/// <summary>
/// Regole di rete delle sorgenti degli strumenti (ADR-020). Stesso spirito di <see cref="PlacementRules"/>, con
/// una differenza: un server MCP o un documento OpenAPI stanno su un percorso, quindi il percorso e' ammesso.
/// Credenziali nell'indirizzo, query e frammento no; solo https verificato o loopback numerico, perche' e' li'
/// che le credenziali di un'applicazione possono viaggiare.
/// </summary>
public static class ToolSourceRules
{
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "content-type", "accept", "connection", "transfer-encoding", "te", "trailer",
        "upgrade", "expect", "cookie", "proxy-authorization", "mcp-session-id", "mcp-protocol-version",
        "last-event-id",
    };

    /// <summary>Motivo per cui l'indirizzo non e' ammesso, null se lo e'.</summary>
    public static string? Violation(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri)
        {
            return "url_missing";
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            return "url_has_credentials";
        }

        if (!string.IsNullOrEmpty(url.Query))
        {
            return "url_has_query";
        }

        if (!string.IsNullOrEmpty(url.Fragment))
        {
            return "url_has_fragment";
        }

        if (string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            && url.HostNameType is (UriHostNameType.IPv4 or UriHostNameType.IPv6)
            && IPAddress.TryParse(url.Host.Trim('[', ']'), out IPAddress? address)
            && IPAddress.IsLoopback(address))
        {
            return null;
        }

        return "url_not_https_or_loopback";
    }

    /// <summary>
    /// Collocazione osservata di una sorgente HTTP: loopback se l'indirizzo lo e', altrimenti un terzo. Un server
    /// raggiunto in https puo' essere di chiunque, e da qui non si vede.
    /// </summary>
    public static Placement PlacementOf(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        return string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ? Placement.ThirdParty : Placement.Loopback;
    }

    /// <summary>Motivo per cui un'intestazione non puo' portare una credenziale, null se puo'.</summary>
    public static string? HeaderViolation(string? header)
    {
        if (string.IsNullOrEmpty(header) || header.Length > 64 || !header.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
        {
            return "header_malformed";
        }

        return ReservedHeaders.Contains(header) ? "header_reserved" : null;
    }
}
