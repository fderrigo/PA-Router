using System.Net.Http.Json;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Adapters.Ollama;

/// <summary>
/// Se un modello di Ollama ragiona prima di rispondere, e se si puo' spegnere (23 settembre 2026). La scheda del modello
/// (<c>/api/show</c>) dice solo «thinking», sia per chi lo spegne sia per chi ragiona sempre; la distinzione la da' una
/// domanda minima con <c>think: false</c> e temperatura zero: un modello che obbedisce risponde in pochi token, uno che
/// ragiona comunque ne scrive decine. Si esegue solo quando lo chiede il setup, mai durante un turno.
/// </summary>
public static class OllamaThinkingProbe
{
    /// <summary>Oltre questi token di uscita, per rispondere «ok», il modello ha ragionato.</summary>
    private const int ObeyingTokens = 12;

    /// <summary>Osserva il modello indicato sull'Ollama indicato; solo loopback, come ogni altra lettura del setup.</summary>
    public static async Task<ThinkingSupport> ObserveAsync(Uri endpoint, string model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        if (!PlacementRules.IsAcceptable(Placement.Loopback, endpoint) || LocalModel.IsCloudAlias(model)) { return ThinkingSupport.Unknown; }

        using HttpClient client = PlacementRules.CreateModelClient(Placement.Loopback);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));

        try
        {
            using HttpRequestMessage showRequest = new(HttpMethod.Post, new Uri(endpoint, "api/show")) { Content = JsonContent.Create(new { model }) };
            using HttpResponseMessage show = await client.SendAsync(showRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!show.IsSuccessStatusCode) { return ThinkingSupport.Unknown; }

            using JsonDocument card = JsonDocument.Parse(await BoundedHttpContent.ReadUtf8Async(show.Content, BoundedHttpContent.MetadataBytes, timeout.Token).ConfigureAwait(false));
            bool thinks = card.RootElement.ValueKind == JsonValueKind.Object && card.RootElement.TryGetProperty("capabilities", out JsonElement capabilities)
                && capabilities.ValueKind is JsonValueKind.Array
                && capabilities.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "thinking");

            if (!thinks) { return ThinkingSupport.None; }

            var request = new
            {
                model,
                stream = false,
                think = false,
                keep_alive = "5m",
                messages = new[] { new { role = "user", content = "Rispondi solo con la parola: ok" } },
                options = new { temperature = 0, num_ctx = 2048, num_predict = 64 },
            };

            using HttpRequestMessage chatRequest = new(HttpMethod.Post, new Uri(endpoint, "api/chat")) { Content = JsonContent.Create(request) };
            using HttpResponseMessage answer = await client.SendAsync(chatRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!answer.IsSuccessStatusCode) { return ThinkingSupport.Unknown; }

            using JsonDocument reply = JsonDocument.Parse(await BoundedHttpContent.ReadUtf8Async(answer.Content, BoundedHttpContent.ErrorBytes, timeout.Token).ConfigureAwait(false));
            if (reply.RootElement.ValueKind is not JsonValueKind.Object) { return ThinkingSupport.Unknown; }
            long written = BoundedHttpContent.NonNegativeCount(reply.RootElement, "eval_count") ?? long.MaxValue;
            bool hidden = reply.RootElement.TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("thinking", out JsonElement thinking)
                && thinking.ValueKind == JsonValueKind.String
                && thinking.GetString() is { Length: > 0 };

            return written <= ObeyingTokens && !hidden ? ThinkingSupport.Switchable : ThinkingSupport.Always;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ThinkingSupport.Unknown;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or PaRouterException or IOException)
        {
            return ThinkingSupport.Unknown;
        }
    }
}
