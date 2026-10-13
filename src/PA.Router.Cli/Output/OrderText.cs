using System.Text.Json.Nodes;
using PA.Router.Cli.Localization;
using PA.Router.Hosting.Runtime;

namespace PA.Router.Cli.Output;

/// <summary>
/// Il posto di un servizio nell'ordine dei modelli, scritto per una persona (ADR-032, fase 3): l'ordine e' l'unica scala
/// della qualita', e ogni elenco che prima mostrava «voto 8 su 10» mostra il posto. Una resa sola per `pa registry list`,
/// `pa explain` e il REPL; il posto lo calcola <see cref="ServiceOrder"/>.
/// </summary>
internal static class OrderText
{
    /// <summary>«2° su 3 nel tuo ordine», oppure «fuori dall'ordine».</summary>
    internal static string Describe(OrderPosition? position) =>
        position is OrderPosition place
            ? Ui.Format("list.order {0} {1}", (place.Position + 1).ToString(Formats.Invariant), place.Count.ToString(Formats.Invariant))
            : Ui.Text("list.unordered");

    /// <summary>Il posto in forma JSON: 1 il primo, null se il servizio non ha posto.</summary>
    internal static JsonObject Json(OrderPosition? position) =>
        position is OrderPosition place
            ? new JsonObject { ["position"] = place.Position + 1, ["of"] = place.Count }
            : new JsonObject { ["position"] = null, ["of"] = null };
}
