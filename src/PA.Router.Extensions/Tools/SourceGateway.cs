using System.Text.Json;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Extensions.Tools;

/// <summary>Strumento offerto al modello: nome visto dal modello, sorgente, strumento e destinazione attestata.</summary>
public sealed record OfferedTool(string ModelName, IToolSource Source, DiscoveredTool Tool, ToolDestination Destination);

/// <summary>
/// Gateway degli strumenti di un turno sopra sorgenti vere. Offre al modello i soli strumenti preparati e
/// chiude le sorgenti a fine turno: le credenziali muoiono con loro.
/// </summary>
public sealed class SourceGateway : IToolGateway, IAsyncDisposable
{
    private readonly IReadOnlyList<IToolSource> sources;
    private readonly Dictionary<string, OfferedTool> offered;
    private readonly IReadOnlyList<string> secrets;

    /// <summary>Forme in cui una credenziale puo' ricomparire: intera, scritta in JSON, oppure il solo token.</summary>
    private static IEnumerable<string> Forms(string value)
    {
        yield return value;
        yield return JsonSerializer.Serialize(value)[1..^1];

        // "Bearer <token>": una sorgente che ripete solo il token lo espone lo stesso.
        int space = value.LastIndexOf(' ');

        if (space >= 0 && value.Length - space - 1 >= 8)
        {
            yield return value[(space + 1)..];
        }
    }

    /// <summary>Segnaposto al posto di una credenziale ritrovata in un risultato.</summary>
    public const string CredentialPlaceholder = "[credential]";

    /// <param name="sources">Sorgenti del turno.</param>
    /// <param name="tools">Strumenti offerti.</param>
    /// <param name="credentialValues">
    /// Valori delle credenziali del turno. Un risultato che ne riporta una — un'API che la ripete in un errore — la vede
    /// sostituita prima che il modello la legga; argomenti che ne contengono una non partono.
    /// </param>
    public SourceGateway(IReadOnlyList<IToolSource> sources, IReadOnlyList<OfferedTool> tools, IReadOnlyCollection<string>? credentialValues = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(tools);

        this.sources = sources;
        secrets = [.. (credentialValues ?? []).Where(value => value.Length > 0)
            .SelectMany(Forms)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)];
        offered = tools.ToDictionary(tool => tool.ModelName, StringComparer.Ordinal);
        Definitions = [.. tools.Select(tool => new ToolDefinition(tool.ModelName, tool.Tool.Description, tool.Tool.InputSchemaJson))];
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> Definitions { get; }

    /// <inheritdoc />
    public ToolDestination? DestinationOf(string toolName) =>
        offered.TryGetValue(toolName, out OfferedTool? tool) ? tool.Destination : null;

    /// <inheritdoc />
    public async Task<ToolInvocation> InvokeAsync(ToolCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!offered.TryGetValue(call.Name, out OfferedTool? tool))
        {
            return new ToolInvocation("This tool is not available.", IsError: true);
        }

        if (secrets.Any(secret => call.ArgumentsJson.Contains(secret, StringComparison.Ordinal)))
        {
            // Una credenziale non passa da una sorgente a un'altra attraverso gli argomenti del modello.
            return new ToolInvocation("The arguments contain a credential of a tool source: the call was not made.", IsError: true);
        }

        ToolInvocation result = await tool.Source.CallAsync(tool.Tool.Name, call.ArgumentsJson, cancellationToken).ConfigureAwait(false);

        string content = result.Content;

        foreach (string secret in secrets)
        {
            content = content.Replace(secret, CredentialPlaceholder, StringComparison.Ordinal);
        }

        return result with { Content = content };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (IToolSource source in sources)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }
}
