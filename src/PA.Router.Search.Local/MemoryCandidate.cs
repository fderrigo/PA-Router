using System.Runtime.CompilerServices;
using PA.Router.Abstractions.Contracts;

[assembly: InternalsVisibleTo("PA.Router.MemoryTests")]

namespace PA.Router.Search.Local;

/// <summary>
/// Evidence retained before the similarity floor. This is an internal retrieval candidate,
/// not permission to fetch its body or a relevance decision. It carries no query or memory text.
/// Ranks start at one; a missing rank means that retrieval list did not select the chunk.
/// </summary>
internal sealed record MemoryCandidate(
    string ChunkId,
    string ObservationId,
    int ChunkOrdinal,
    double? Cosine,
    int? VectorRank,
    int? LexicalRank,
    double FusionScore,
    bool PassesSimilarityFloor)
{
    public bool RetrievedByVector => VectorRank.HasValue;
    public bool RetrievedByLexical => LexicalRank.HasValue;

    // Candidate identity is private metadata, not an invitation to put it in a log.
    public override string ToString() => nameof(MemoryCandidate);
}

/// <summary>Scoped, revalidated evidence for diagnostics and future selection experiments.</summary>
internal sealed record MemoryCandidateResult(
    IReadOnlyList<MemoryCandidate> Candidates,
    MemorySearchMode Mode,
    MemoryIndexStatus IndexStatus,
    string? ModelFingerprint)
{
    public override string ToString() => nameof(MemoryCandidateResult);
}

/// <summary>A cosine calculation retained independently of candidate filtering.</summary>
internal sealed record VectorChunkScore(string ChunkId, double Cosine);

/// <summary>RRF order and the contribution of each retrieval list, without document text.</summary>
internal sealed record ChunkFusionEvidence(string ChunkId, double Score, int? VectorRank, int? LexicalRank);
