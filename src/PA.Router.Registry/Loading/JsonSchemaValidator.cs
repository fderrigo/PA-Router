using System.Text.Json;
using Json.Schema;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Validazione Draft 2020-12 dei documenti gia' letti. I formati (uuid, date-time, uri) sono
/// asseriti esplicitamente: molte librerie li trattano come annotazioni e li ignorerebbero
/// (06-contracts/README.md).
/// </summary>
public static class JsonSchemaValidator
{
    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true,
    };

    /// <summary>
    /// Valida il documento. Ogni violazione diventa un <see cref="ValidationIssue"/> con il
    /// percorso della proprieta' e la riga, senza il valore che l'ha causata.
    /// </summary>
    public static bool Validate(
        ContractSchema schema,
        string source,
        ParsedDocument document,
        List<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(issues);

        JsonElement instance = JsonSerializer.SerializeToElement(document.Root);
        EvaluationResults results = SchemaCatalog.Get(schema).Evaluate(instance, Options);

        if (results.IsValid)
        {
            return true;
        }

        int before = issues.Count;
        List<ValidationIssue> containers = [];

        foreach (EvaluationResults detail in Flatten(results))
        {
            if (detail.Errors is null || IsConditionalProbe(detail))
            {
                continue;
            }

            string pointer = detail.InstanceLocation.ToString();

            foreach (KeyValuePair<string, string> error in detail.Errors)
            {
                ValidationIssue issue = new(
                    source,
                    pointer,
                    document.LineOf(pointer),
                    ValidationCodes.SchemaViolation,
                    Describe(error.Key));

                // Un vincolo che raccoglie altri vincoli (allOf, properties, items, then…) non dice che cosa manca: e' il
                // genitore dell'errore vero. Si mostra solo se non c'e' altro (23 settembre 2026).
                if (Containers.Contains(error.Key)) { containers.Add(issue); } else { issues.Add(issue); }
            }
        }

        if (issues.Count == before)
        {
            issues.AddRange(containers);
        }

        if (issues.Count == before)
        {
            // Lo schema ha rifiutato il documento senza dettagli utilizzabili: meglio un errore
            // generico che un silenzioso successo.
            issues.Add(new ValidationIssue(
                source,
                "",
                null,
                ValidationCodes.SchemaViolation,
                "The document does not satisfy " + SchemaCatalog.FileNameOf(schema) + "."));
        }

        return false;
    }

    /// <summary>
    /// Un ramo <c>if</c> che non si applica non e' una violazione: e' la domanda «questa regola
    /// riguarda questo documento?». Riportarlo come errore farebbe comparire, su ogni servizio,
    /// un elenco di vincoli che non lo riguardano.
    /// </summary>
    private static bool IsConditionalProbe(EvaluationResults detail) =>
        detail.EvaluationPath.ToString().Contains("/if", StringComparison.Ordinal);

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;

        foreach (EvaluationResults child in results.Details ?? [])
        {
            foreach (EvaluationResults nested in Flatten(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Parole chiave che raccolgono altri vincoli: la loro violazione e' quella dei figli.</summary>
    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    {
        "allOf", "anyOf", "oneOf", "not", "then", "else", "properties", "items", "prefixItems", "additionalItems",
        "patternProperties", "dependentSchemas", "$ref",
    };

    /// <summary>
    /// Messaggio safe: riporta il vincolo violato, mai il valore. Un file di configurazione puo' contenere riferimenti
    /// sensibili e l'errore finisce nei log. Fino al 25 settembre 2026 alcuni campi dei servizi abilitati (`unverified`,
    /// `processingRegions`, `evidenceExpiresAt`) avevano un messaggio proprio: le loro regole sono state tolte da ADR-032,
    /// fase 4.
    /// </summary>
    private static string Describe(string keyword) =>
        keyword switch
        {
            "additionalProperties" => "The object contains a property the schema does not define.",
            "required" => "A required property is missing.",
            "const" => "The value is not the constant the schema requires.",
            "enum" => "The value is outside the allowed set.",
            "type" => "The value has the wrong type.",
            "pattern" => "The value does not match the required pattern.",
            "format" => "The value does not match the required format.",
            "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" => "The number is outside the allowed range.",
            "minLength" or "maxLength" => "The string length is outside the allowed range.",
            "minItems" or "maxItems" => "The array length is outside the allowed range.",
            "uniqueItems" => "The array contains duplicate items.",
            _ => "The value violates the '" + keyword + "' constraint.",
        };
}
