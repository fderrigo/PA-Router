using System.Globalization;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>Un modello che Ollama tiene in memoria in questo momento.</summary>
/// <param name="Endpoint">Ollama che lo tiene.</param>
/// <param name="Model">Nome del modello.</param>
/// <param name="Bytes">Memoria occupata.</param>
/// <param name="ContextTokens">Finestra con cui e' stato caricato: un'altra finestra lo fa ricaricare.</param>
/// <param name="ExpiresAt">Quando Ollama lo scarichera' se nessuno lo usa.</param>
public sealed record ResidentModel(Uri Endpoint, string Model, long Bytes, int? ContextTokens, DateTimeOffset? ExpiresAt);

/// <summary>
/// Quali modelli Ollama tiene in memoria, per il livello di diagnosi del REPL (23 settembre 2026). Un analizzatore
/// scaricato fra due turni costa una ventina di secondi a freddo senza GPU: senza vedere che cosa c'era in memoria prima e
/// dopo un turno, quei secondi non si spiegano. Legge soltanto: <c>GET /api/ps</c>, nessuna generazione, e solo gli
/// Ollama del registro su loopback.
/// </summary>
public static class OllamaResidency
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>I modelli in memoria su ogni Ollama di questa macchina che il registro usa; un Ollama che non risponde si salta.</summary>
    public static async Task<IReadOnlyList<ResidentModel>> ObserveAsync(ConfigurationSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Uri[] endpoints = [.. snapshot.Registry.Services
            .Where(service => service.Adapter is AdapterKind.Ollama && service.InferencePlacement is Placement.Loopback && service.Endpoint is not null)
            .Select(service => new Uri(service.Endpoint!.GetLeftPart(UriPartial.Authority)))
            .Distinct()];

        List<ResidentModel> resident = [];
        using HttpClient client = PlacementRules.CreateModelClient(Placement.Loopback);

        foreach (Uri endpoint in endpoints)
        {
            if (!PlacementRules.IsAcceptable(Placement.Loopback, endpoint)) { continue; }
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                using HttpResponseMessage response = await client.GetAsync(new Uri(endpoint, "api/ps"), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) { continue; }

                using JsonDocument document = JsonDocument.Parse(await BoundedHttpContent.ReadUtf8Async(response.Content, BoundedHttpContent.MetadataBytes, timeout.Token).ConfigureAwait(false));
                if (document.RootElement.ValueKind is not JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind is not JsonValueKind.Array) { continue; }

                foreach (JsonElement model in models.EnumerateArray())
                {
                    if (model.ValueKind is not JsonValueKind.Object) { continue; }
                    resident.Add(new ResidentModel(
                        endpoint,
                        model.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "?" : "?",
                        BoundedHttpContent.NonNegativeCount(model, "size") ?? 0,
                        model.TryGetProperty("context_length", out JsonElement context) && context.ValueKind == JsonValueKind.Number && context.TryGetInt32(out int tokens) && tokens >= 0 ? tokens : null,
                        model.TryGetProperty("expires_at", out JsonElement expires)
                            && expires.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(expires.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at) ? at : null));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A stalled body has the same bounded deadline as the request headers.
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or PaRouterException or IOException)
            {
                // Una diagnosi non ferma un turno: un Ollama che non risponde semplicemente non compare.
            }
        }

        return resident;
    }
}
