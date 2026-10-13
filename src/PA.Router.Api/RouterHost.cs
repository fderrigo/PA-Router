using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Hosting.Runtime;
using PA.Router.Registry.Loading;
using PA.Router.Sdk;

namespace PA.Router.Api;

/// <summary>
/// Host HTTP di PA Router.
///
/// Ascolta solo su loopback: un'istanza raggiungibile dalla rete non fa parte della beta, e non e'
/// una configurazione che si attiva per distrazione. Il token di servizio viene generato a ogni
/// avvio se l'operatore non ne fornisce uno, e stampato una sola volta da chi avvia l'host.
/// </summary>
public sealed class RouterHost : IAsyncDisposable
{
    private readonly WebApplication application;

    private RouterHost(WebApplication application, PaRouterRuntime runtime, string serviceToken, Uri address)
    {
        this.application = application;

        Runtime = runtime;
        ServiceToken = serviceToken;
        Address = address;
    }

    /// <summary>Runtime servito da questo host.</summary>
    public PaRouterRuntime Runtime { get; }

    /// <summary>Token di servizio richiesto su ogni route.</summary>
    public string ServiceToken { get; }

    /// <summary>Indirizzo effettivo di ascolto.</summary>
    public Uri Address { get; }

    /// <summary>
    /// Avvia l'host sul loopback indicato dalla configurazione. La porta zero chiede al sistema una
    /// porta libera: serve ai test, e non cambia il fatto che l'ascolto resti locale.
    /// </summary>
    public static async Task<RouterHost> StartAsync(
        ConfigurationSnapshot snapshot,
        string? serviceToken = null,
        int? portOverride = null,
        CancellationToken cancellationToken = default,
        RouterStack? stack = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string host = snapshot.Config.Api.Host;

        if (host is not ("127.0.0.1" or "::1" or "[::1]"))
        {
            throw new PaRouterException(ErrorCode.PolicyInvalid, "The API host must be a numeric loopback address.")
                .WithMetadata("reason", "api_host_not_loopback");
        }

        string token = serviceToken ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        int port = portOverride ?? snapshot.Config.Api.Port;

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://" + host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Il corpo massimo e' quello del contratto, applicato alla porta: un corpo piu' grande si ferma mentre arriva.
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = RouterEndpoints.MaxBodyBytes);

        WebApplication application = builder.Build();

        // Con una composizione gia' aperta — il caso di `pa serve`, che e' anche il worker — lo
        // store ha un proprietario solo e l'host non ne apre un secondo.
        PaRouterRuntime? runtime = null;

        try
        {
            runtime = stack is null
                ? PaRouterRuntime.CreateChatbotRuntime(snapshot)
                : PaRouterRuntime.Over(stack, Channel.Chatbot, snapshot.Config.Api.ServiceProfileId);

            RouterEndpoints.Map(application, runtime, token);

            await application.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Un host che non parte — porta occupata, indirizzo rifiutato — non tiene la directory dati: senza questo il
            // proprietario successivo troverebbe STORE_IN_USE per un host che non esiste.
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }

            await application.DisposeAsync().ConfigureAwait(false);

            throw;
        }

        string address = application.Urls.FirstOrDefault()
            ?? "http://" + host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new RouterHost(application, runtime, token, new Uri(address));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await application.StopAsync().ConfigureAwait(false);
        await Runtime.DisposeAsync().ConfigureAwait(false);
        await application.DisposeAsync().ConfigureAwait(false);
    }
}
