using System.Net;
using System.Text;
using PA.Router.Abstractions;

namespace PA.Router.Extensions.Tools;

/// <summary>
/// Trasporto HTTP comune alle sorgenti: nessun redirect seguito, credenziali solo verso la sorgente per cui sono
/// state consegnate, corpo letto con un tetto, guasti tradotti in codici stabili senza propagare nulla del server.
/// </summary>
internal static class SourceHttp
{
    /// <summary>Invia e restituisce la risposta riuscita, oppure lancia con il codice del guasto.</summary>
    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        string sourceId,
        bool hasCredential,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw Unavailable(sourceId, "unreachable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable(sourceId, "timeout");
        }

        int status = (int)response.StatusCode;

        if (status is >= 300 and < 400)
        {
            response.Dispose();

            // Un redirect porterebbe la richiesta, e la credenziale, su un indirizzo che nessuno ha indicato.
            throw Unavailable(sourceId, "redirect");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();

            throw new PaRouterException(
                hasCredential ? ErrorCode.ToolCredentialsRejected : ErrorCode.ToolCredentialsMissing,
                "The tool source refused the request.")
                .WithMetadata("toolSource", sourceId);
        }

        return response;
    }

    /// <summary>Corpo della risposta entro il tetto, null se lo supera.</summary>
    internal static async Task<string?> ReadCappedAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();

        byte[] chunk = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    internal static PaRouterException Unavailable(string sourceId, string reason) =>
        new PaRouterException(ErrorCode.ToolSourceUnavailable, "The tool source is not usable.")
            .WithMetadata("toolSource", sourceId)
            .WithMetadata("reason", reason);

    /// <summary>Guasto di una chiamata, dal codice dell'eccezione.</summary>
    internal static Abstractions.Contracts.ToolInvocationFailure FailureOf(PaRouterException exception) =>
        exception.Code is ErrorCode.ToolCredentialsRejected or ErrorCode.ToolCredentialsMissing
            ? Abstractions.Contracts.ToolInvocationFailure.CredentialsRejected
            : Abstractions.Contracts.ToolInvocationFailure.SourceUnavailable;
}
