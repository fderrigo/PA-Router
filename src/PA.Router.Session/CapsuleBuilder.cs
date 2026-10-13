using System.Text.Json;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Session;

/// <summary>
/// Costruzione e serializzazione della capsule (06-contracts/handover.schema.json).
///
/// La capsule e' deterministica: nasce da dati strutturati — obiettivo, vincoli, fatti annotati con
/// la loro prova, decisioni, lavoro aperto, artefatti, checkpoint — e non da un riassunto prodotto
/// da un modello. Un riassunto sarebbe comodo e non verificabile, e la beta non lo usa.
///
/// Cio' che la capsule trasporta della memoria sono gli identificatori dei ricordi, non i loro
/// contenuti: la destinazione li richiede di nuovo e riceve solo cio' che e' ancora autorizzato
/// (03-specs/SESSION-HANDOVER.md, «Integrazione memoria beta»).
/// </summary>
public static class CapsuleBuilder
{
    /// <summary>Limiti dello schema, applicati prima di costruire invece che dopo.</summary>
    public const int MaxObjectiveBytes = 8192;

    /// <summary>Elementi massimi per ciascun elenco.</summary>
    public const int MaxItems = 64;

    /// <summary>Costruisce la capsule verificando i limiti dichiarati dal contratto.</summary>
    public static HandoverCapsule Create(
        string sessionId,
        string sourceExecutionId,
        string objective,
        IReadOnlyList<string> constraints,
        IReadOnlyList<VerifiedFact> facts,
        IReadOnlyList<string> decisions,
        IReadOnlyList<string> openWork,
        IReadOnlyList<ArtifactChange> artifacts,
        WorkspaceCheckpoint checkpoint,
        DateTimeOffset createdAt,
        IReadOnlyList<string>? memoryIds = null,
        CapsuleProducer producer = CapsuleProducer.Runtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceExecutionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);
        ArgumentNullException.ThrowIfNull(constraints);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(openWork);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (System.Text.Encoding.UTF8.GetByteCount(objective) > MaxObjectiveBytes)
        {
            throw Invalid("objective_too_long");
        }

        Limit(constraints.Count, "constraints_too_many");
        Limit(facts.Count, "facts_too_many");
        Limit(decisions.Count, "decisions_too_many");
        Limit(openWork.Count, "open_work_too_many");
        Limit(artifacts.Count, "artifacts_too_many");

        return new HandoverCapsule(
            sessionId,
            sourceExecutionId,
            objective,
            [.. constraints],
            [.. facts],
            [.. decisions],
            [.. openWork],
            [.. artifacts],
            checkpoint,
            new CapsuleProvenance(producer, createdAt),
            memoryIds is null ? [] : [.. memoryIds]);
    }

    /// <summary>Serializza la capsule nella forma del contratto.</summary>
    public static string ToJson(HandoverCapsule capsule)
    {
        ArgumentNullException.ThrowIfNull(capsule);

        JsonObject payload = new()
        {
            ["schemaVersion"] = HandoverCapsule.SchemaVersion,
            ["sessionId"] = capsule.SessionId,
            ["sourceExecutionId"] = capsule.SourceExecutionId,
            ["objective"] = capsule.Objective,
            ["constraints"] = Array(capsule.Constraints),
            ["verifiedFacts"] = new JsonArray([.. capsule.VerifiedFacts.Select(fact => (JsonNode)new JsonObject
            {
                ["text"] = fact.Text,
                ["evidence"] = fact.Evidence,
                ["status"] = fact.Status.ToWire(),
            })]),
            ["decisions"] = Array(capsule.Decisions),
            ["openWork"] = Array(capsule.OpenWork),
            ["artifacts"] = new JsonArray([.. capsule.Artifacts.Select(artifact => (JsonNode)new JsonObject
            {
                ["path"] = artifact.Path,
                ["digest"] = artifact.Digest,
                ["changeKind"] = artifact.Change.ToWire(),
            })]),
            ["checkpoint"] = new JsonObject
            {
                ["repoRootDigest"] = capsule.Checkpoint.RepoRootDigest,
                ["gitHead"] = capsule.Checkpoint.GitHead,
                ["dirtyManifestDigest"] = capsule.Checkpoint.DirtyManifestDigest,
            },
            ["provenance"] = new JsonObject
            {
                ["producer"] = capsule.Provenance.Producer.ToWire(),
                ["createdAt"] = capsule.Provenance.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                // Costante del contratto: il contenuto della capsule non e' mai istruzione fidata.
                ["trustedInstructions"] = false,
            },
        };

        return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Testo che entra nel payload della destinazione. Contiene la capsule e gli identificatori dei
    /// ricordi, mai i loro contenuti: quelli si riottengono con i controlli correnti.
    /// </summary>
    public static string ToContextText(HandoverCapsule capsule)
    {
        ArgumentNullException.ThrowIfNull(capsule);

        string json = ToJson(capsule);

        if (capsule.MemoryIds.Count == 0)
        {
            return json;
        }

        return json + "\n[ricordi citati: " + string.Join(", ", capsule.MemoryIds) + "]";
    }

    private static JsonArray Array(IReadOnlyList<string> values) =>
        new([.. values.Select(value => (JsonNode)value)]);

    private static void Limit(int count, string reason)
    {
        if (count > MaxItems)
        {
            throw Invalid(reason);
        }
    }

    private static PaRouterException Invalid(string reason) =>
        new PaRouterException(ErrorCode.InputInvalid, "The capsule does not respect the contract limits.")
            .WithMetadata("reason", reason);
}
