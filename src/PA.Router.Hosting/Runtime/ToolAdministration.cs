using System.Globalization;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Extensions.Tools;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Attestazioni e revoche dell'operatore sugli strumenti (ADR-020), scritte nella configurazione con
/// <see cref="RegistryRewrite"/>. `pa tools attest` e `pa tools revoke` le usano; il worker e l'host API le
/// vedono dalla richiesta successiva, perche' rileggono la configurazione.
/// </summary>
public static class ToolAdministration
{
    /// <summary>Suffisso della copia dell'originale.</summary>
    public const string BackupSuffix = ".pre-tools.bak";

    /// <summary>Verifica un'attestazione dell'operatore prima di scriverla.</summary>
    public static ToolAttestationConfig PlanAttestation(
        ConfigurationSnapshot snapshot,
        string source,
        string tool,
        string fingerprint,
        string accepts,
        string returns,
        IReadOnlyList<string> regions,
        string expiresAt,
        string? placement,
        string by,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(regions);

        RequireKnownSource(snapshot, source);

        if (string.IsNullOrEmpty(tool) || !ToolNames.IsWellFormedMcpName(tool))
        {
            throw Invalid("tool", "The tool name is not well formed.");
        }

        if (fingerprint is null
            || fingerprint.Length != ToolFingerprint.Prefix.Length + 64
            || !fingerprint.StartsWith(ToolFingerprint.Prefix, StringComparison.Ordinal)
            || !fingerprint[ToolFingerprint.Prefix.Length..].All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'))
        {
            throw Invalid("fingerprint", "The fingerprint must be sha256: followed by 64 lowercase hex digits.");
        }

        DataClass accepted = DeclarableClass("accepts", accepts);
        DataClass returned = DeclarableClass("returns", returns);
        HashSet<ProcessingRegion> parsed = [];

        foreach (string term in regions)
        {
            ProcessingRegion region = Regions.Parse(term);

            if (!Regions.IsWellFormed(region))
            {
                throw Invalid("regions", "A processing region must be a two-letter country code or a known group.");
            }

            parsed.Add(region);
        }

        if (!DateTimeOffset.TryParse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset expires)
            || expires <= now)
        {
            throw Invalid("expires", "The attestation needs an expiry in the future.");
        }

        Placement? declaredPlacement = null;

        if (placement is not null)
        {
            if (!WireEnum.TryParse(placement, out Placement value))
            {
                throw Invalid("placement", "The placement must be loopback, lan, private_remote or third_party.");
            }

            declaredPlacement = value;
        }

        if (string.IsNullOrWhiteSpace(by) || by.Length > 128)
        {
            throw Invalid("by", "The attestation names who attests.");
        }

        return new ToolAttestationConfig(source, tool, fingerprint, accepted, returned, parsed, declaredPlacement, by, expires);
    }

    /// <summary>Verifica una revoca prima di scriverla.</summary>
    public static void PlanRevocation(ConfigurationSnapshot snapshot, string source, string tool)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        RequireKnownSource(snapshot, source);

        if (string.IsNullOrEmpty(tool) || !ToolNames.IsWellFormedMcpName(tool))
        {
            throw Invalid("tool", "The tool name is not well formed.");
        }
    }

    /// <summary>Scrive l'attestazione, sostituendo quella dello stesso strumento e togliendone la revoca.</summary>
    public static bool ApplyAttestation(JsonObject config, ToolAttestationConfig attestation)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(attestation);

        JsonObject tools = ToolsSection(config);
        JsonArray list = tools["attestations"] as JsonArray ?? [];

        tools["attestations"] = list;
        Remove(list, attestation.Source, attestation.Tool);

        if (tools["revoked"] is JsonArray revoked)
        {
            Remove(revoked, attestation.Source, attestation.Tool);
        }

        JsonObject entry = new()
        {
            ["source"] = attestation.Source,
            ["tool"] = attestation.Tool,
            ["fingerprint"] = attestation.Fingerprint,
            ["accepts"] = attestation.Accepts.ToWire(),
            ["returns"] = attestation.Returns.ToWire(),
            ["regions"] = new JsonArray([.. attestation.Regions.Select(region => (JsonNode?)region.Code).OrderBy(code => code!.GetValue<string>(), StringComparer.Ordinal)]),
            ["by"] = attestation.By,
            ["expiresAt"] = attestation.ExpiresAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };

        if (attestation.Placement is Placement declared)
        {
            entry["placement"] = declared.ToWire();
        }

        list.Add(entry);

        return true;
    }

    /// <summary>Revoca uno strumento: toglie l'attestazione dell'operatore e lo mette fra i revocati.</summary>
    public static bool ApplyRevocation(JsonObject config, string source, string tool)
    {
        ArgumentNullException.ThrowIfNull(config);

        JsonObject tools = ToolsSection(config);
        bool changed = tools["attestations"] is JsonArray attestations && Remove(attestations, source, tool);
        JsonArray revoked = tools["revoked"] as JsonArray ?? [];

        tools["revoked"] = revoked;

        if (revoked.OfType<JsonObject>().Any(entry => Matches(entry, source, tool)))
        {
            return changed;
        }

        revoked.Add(new JsonObject { ["source"] = source, ["tool"] = tool });

        return true;
    }

    private static void RequireKnownSource(ConfigurationSnapshot snapshot, string source)
    {
        bool configured = snapshot.Config.Tools?.Sources.Any(entry => string.Equals(entry.Id, source, StringComparison.Ordinal)) == true;

        if (!configured && (!Uri.TryCreate(source, UriKind.Absolute, out Uri? url) || ToolSourceRules.Violation(url) is not null))
        {
            throw Invalid("source", "The source must be a configured source id or the https or loopback url of an application source.");
        }
    }

    private static DataClass DeclarableClass(string field, string value)
    {
        if (!WireEnum.TryParse(value, out DataClass parsed) || parsed is DataClass.Secret or DataClass.Unknown)
        {
            throw Invalid(field, "The class must be public, internal, personal or sensitive.");
        }

        return parsed;
    }

    private static JsonObject ToolsSection(JsonObject config)
    {
        if (config["tools"] is JsonObject tools)
        {
            return tools;
        }

        // La sezione nasce spenta: attestare uno strumento non accende gli strumenti.
        tools = new JsonObject
        {
            ["enabled"] = false,
            ["limits"] = new JsonObject
            {
                ["maxRounds"] = ToolLimits.Default.MaxRounds,
                ["maxCallsPerRound"] = ToolLimits.Default.MaxCallsPerRound,
                ["maxCalls"] = ToolLimits.Default.MaxCalls,
                ["callTimeoutSeconds"] = ToolLimits.Default.CallTimeoutSeconds,
                ["maxResultBytes"] = ToolLimits.Default.MaxResultBytes,
            },
        };

        config["tools"] = tools;

        return tools;
    }

    private static bool Remove(JsonArray list, string source, string tool)
    {
        bool removed = false;

        for (int index = list.Count - 1; index >= 0; index--)
        {
            if (list[index] is JsonObject entry && Matches(entry, source, tool))
            {
                list.RemoveAt(index);
                removed = true;
            }
        }

        return removed;
    }

    private static bool Matches(JsonObject entry, string source, string tool) =>
        string.Equals(entry["source"]?.GetValue<string>(), source, StringComparison.Ordinal)
        && string.Equals(entry["tool"]?.GetValue<string>(), tool, StringComparison.Ordinal);

    private static PaRouterException Invalid(string field, string message) =>
        new PaRouterException(ErrorCode.InputInvalid, message).WithMetadata("field", field);
}
