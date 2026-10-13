using System.Text.Json.Nodes;
using PA.Router.Abstractions.Contracts;
using PA.Router.Hosting.Runtime;

namespace PA.Router.Cli.Output;

/// <summary>Resa della classificazione e di cio' che il router concede a una richiesta.</summary>
internal static class PlanText
{
    /// <summary>Classificazione, capacita' concesse e cartella, in una riga.</summary>
    internal static string Describe(TurnPlan plan) =>
        plan.Classification.Intent.ToWire() + " (" + plan.Classification.Version + ")"
        + (plan.Intent != plan.Classification.Intent ? " -> " + plan.Intent.ToWire() : string.Empty)
        + Ui.Text(", capacita': ") + (plan.Capabilities.Count == 0
            ? "chat"
            : string.Join(", ", plan.Capabilities.Select(capability => capability.ToWire()).Order(StringComparer.Ordinal)))
        + (plan.Workspace is WorkspaceDescriptor workspace
            ? Ui.Text(", cartella ") + workspace.RootPath + (workspace.Writable ? Ui.Text(" in scrittura") : Ui.Text(" in lettura"))
            : string.Empty);

    /// <summary>La stessa informazione in JSON.</summary>
    internal static JsonObject Json(TurnPlan plan) => new()
    {
        ["intent"] = plan.Classification.Intent.ToWire(),
        ["effectiveIntent"] = plan.Intent.ToWire(),
        ["version"] = plan.Classification.Version,
        ["signals"] = new JsonArray([.. plan.Classification.Signals.Select(signal => JsonValue.Create(signal))]),
        ["capabilities"] = new JsonArray([.. plan.Capabilities.Select(capability => capability.ToWire()).Order(StringComparer.Ordinal).Select(value => JsonValue.Create(value))]),
        ["workspace"] = plan.Workspace?.RootPath,
        ["writable"] = plan.Workspace?.Writable ?? false,
        ["limits"] = new JsonArray([.. plan.Limits.Select(limit => JsonValue.Create(limit))]),
    };
}
