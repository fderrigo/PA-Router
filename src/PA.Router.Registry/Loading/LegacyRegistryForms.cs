using System.Globalization;
using System.Text.Json.Nodes;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Forme del registro sostituite da una conversione.
///
/// Fino a S38 un servizio portava un solo numero di qualita' in <c>calibration.quality</c>, uguale
/// per qualsiasi richiesta. E' stato sostituito dai voti per abilita' (<c>ratings</c>), e dal 24 settembre 2026 (ADR-032,
/// fase 3) i voti dall'ordine dei modelli (<c>rank</c>): la conversione li porta fino all'ordine in un passo. Lo schema
/// rifiuterebbe comunque la vecchia forma, ma con un generico «proprieta' non definita» che non dice
/// come uscirne: qui la si riconosce e si nomina il comando che la converte.
///
/// La conversione non avviene alla lettura: un file che decide instradamenti convertito in silenzio
/// e' una comodita' che si paga (<see cref="ConfigMigration"/>).
/// </summary>
public static class LegacyRegistryForms
{
    /// <summary>
    /// Aggiunge un problema per ogni servizio che usa ancora il numero unico di qualita'.
    /// </summary>
    /// <returns>Vero se il documento usa una forma sostituita.</returns>
    public static bool Report(string source, ParsedDocument document, List<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(issues);

        if (document.Root is not JsonObject root || root["services"] is not JsonArray services)
        {
            return false;
        }

        bool found = false;

        for (int index = 0; index < services.Count; index++)
        {
            if (services[index] is not JsonObject service || !HasSingleQuality(service))
            {
                continue;
            }

            string pointer = "/services/" + index.ToString(CultureInfo.InvariantCulture) + "/calibration/quality";

            issues.Add(new ValidationIssue(
                source,
                pointer,
                document.LineOf(pointer),
                ValidationCodes.MigrationRequired,
                "The single quality score was replaced by the model order (rank): run 'pa config migrate'."));

            found = true;
        }

        return found;
    }

    /// <summary>Vero se il servizio porta ancora <c>calibration.quality</c>.</summary>
    public static bool HasSingleQuality(JsonObject service)
    {
        ArgumentNullException.ThrowIfNull(service);

        return service["calibration"] is JsonObject calibration && calibration.ContainsKey("quality");
    }
}
