using System.Text.Json;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Cli.Output;

/// <summary>
/// Involucro JSON dei risultati della CLI.
///
/// Con <c>--json</c> lo standard output contiene esattamente un documento e nient'altro:
/// niente markup Spectre, niente sequenze ANSI, niente righe di avanzamento. Progressi e
/// diagnostica vanno su standard error, cosi' uno script puo' leggere lo stdout senza filtrarlo
/// (03-specs/CLI-TUI.md).
///
/// Ogni documento porta <c>errorCode</c> quando c'e' un errore: uno script non deve mai dover
/// interpretare un messaggio in italiano per capire che cosa e' successo.
/// </summary>
internal static class CliJson
{
    /// <summary>Versione dello schema dei risultati della CLI.</summary>
    internal const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Documento di successo.</summary>
    internal static JsonObject Ok(string command, JsonObject result) => new()
    {
        ["schemaVersion"] = SchemaVersion,
        ["command"] = command,
        ["ok"] = true,
        ["result"] = result,
    };

    /// <summary>Documento di errore, con codice stabile.</summary>
    internal static JsonObject Error(string command, ErrorCode code, string message) => new()
    {
        ["schemaVersion"] = SchemaVersion,
        ["command"] = command,
        ["ok"] = false,
        ["errorCode"] = code.ToWire(),
        ["message"] = message,
    };

    /// <summary>Documento di errore di validazione, con l'elenco dei problemi.</summary>
    internal static JsonObject Invalid(string command, IReadOnlyList<ValidationIssue> issues)
    {
        JsonArray items = [];

        foreach (ValidationIssue issue in issues)
        {
            items.Add(new JsonObject
            {
                ["source"] = issue.Source,
                ["path"] = issue.Path,
                ["line"] = issue.Line,
                ["code"] = issue.Code,
                ["message"] = issue.Message,
            });
        }

        return new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["command"] = command,
            ["ok"] = false,
            ["errorCode"] = ErrorCode.PolicyInvalid.ToWire(),
            ["issues"] = items,
        };
    }

    /// <summary>Scrive il documento su standard output, senza colori e senza markup.</summary>
    internal static void Write(TextWriter output, JsonObject document) =>
        output.WriteLine(document.ToJsonString(Options));
}
