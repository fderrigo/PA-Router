using PA.Router.Abstractions.Contracts;

namespace PA.Router.Extensions.Tools;

/// <summary>Strumento letto da una sorgente, con l'impronta di cio' che il modello vedra'.</summary>
/// <param name="Name">Nome nella sorgente.</param>
/// <param name="Description">Descrizione della sorgente.</param>
/// <param name="InputSchemaJson">Schema dei parametri.</param>
/// <param name="Fingerprint">Impronta di nome, descrizione, schema e, per OpenAPI, destinazione.</param>
public sealed record DiscoveredTool(string Name, string Description, string InputSchemaJson, string Fingerprint);

/// <summary>
/// Sorgente degli strumenti per un turno (ADR-020): server MCP in HTTP o stdio, oppure API OpenAPI. La sorgente
/// scopre e chiama; decidere se si puo' chiamare non e' compito suo.
/// </summary>
public interface IToolSource : IAsyncDisposable
{
    /// <summary>Identificatore nella richiesta o nella configurazione.</summary>
    string Id { get; }

    /// <summary>Chiave stabile per le attestazioni: l'indirizzo, oppure <c>stdio:</c> e l'identificatore.</summary>
    string Key { get; }

    /// <summary>Collocazione osservata.</summary>
    Placement Placement { get; }

    /// <summary>
    /// Legge gli strumenti. Un guasto e' un'eccezione tipizzata: <c>TOOL_SOURCE_UNAVAILABLE</c>,
    /// <c>TOOL_CREDENTIALS_MISSING</c> o <c>TOOL_CREDENTIALS_REJECTED</c>, con il motivo nei metadata.
    /// </summary>
    Task<IReadOnlyList<DiscoveredTool>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Chiama uno strumento gia' autorizzato; un guasto e' dichiarato nell'esito, non lanciato.</summary>
    Task<ToolInvocation> CallAsync(string tool, string argumentsJson, CancellationToken cancellationToken);
}
