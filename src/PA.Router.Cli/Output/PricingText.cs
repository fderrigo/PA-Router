using System.Globalization;
using System.Text.Json.Nodes;
using PA.Router.Abstractions.Contracts;
using PA.Router.Hosting.Runtime;

namespace PA.Router.Cli.Output;

/// <summary>Resa di prezzi e costi stimati per il terminale.</summary>
internal static class PricingText
{
    /// <summary>Prezzo di un servizio, oppure che cosa vale senza prezzo.</summary>
    internal static string Describe(ServiceDescriptor service) =>
        service.Pricing is ServicePricing pricing
            ? Number(pricing.InputPerMillionTokens) + "/" + Number(pricing.OutputPerMillionTokens)
                + " " + pricing.Currency + Ui.Text(" per milione di token")
            : service.MarginalPrice is null ? Ui.Text("non dichiarato") : Ui.Text("nessun costo a consumo");

    /// <summary>Prezzo di un servizio in forma JSON, null se non dichiarato.</summary>
    internal static JsonObject? Json(ServiceDescriptor service) =>
        service.Pricing is ServicePricing pricing
            ? new JsonObject
            {
                ["currency"] = pricing.Currency,
                ["inputPerMillionTokens"] = pricing.InputPerMillionTokens,
                ["outputPerMillionTokens"] = pricing.OutputPerMillionTokens,
            }
            : null;

    /// <summary>Costi stimati dei servizi ammessi.</summary>
    internal static string Describe(IReadOnlyList<ServiceCost> costs, string? currency) =>
        costs.Count == 0
            ? Ui.Text("nessun servizio ammesso")
            : string.Join(", ", costs.Select(cost => cost.ServiceId + " "
                + (cost.EstimatedCost is double value ? Number(value) + (currency is null ? string.Empty : " " + currency) : Ui.Text("sconosciuto"))));

    /// <summary>Costi stimati in forma JSON: un prezzo sconosciuto vale null.</summary>
    internal static JsonObject Json(IReadOnlyList<ServiceCost> costs) =>
        new(costs.Select(cost => new KeyValuePair<string, JsonNode?>(
            cost.ServiceId,
            cost.EstimatedCost is double value ? JsonValue.Create(value) : null)));

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
