namespace PA.Router.Abstractions;

/// <summary>
/// Tipi di blob ammessi da <c>content_blobs.kind</c> in 001_initial.sql.
/// </summary>
public enum BlobKind
{
    Request,
    Transcript,
    Output,
    Capsule,
    Checkpoint,
    Observation,
    Embedding,
}

/// <summary>Conversioni wire per <see cref="BlobKind"/>.</summary>
public static class BlobKinds
{
    /// <summary>Valore usato dallo schema SQL.</summary>
    public static string ToWire(this BlobKind kind) => kind switch
    {
        BlobKind.Request => "request",
        BlobKind.Transcript => "transcript",
        BlobKind.Output => "output",
        BlobKind.Capsule => "capsule",
        BlobKind.Checkpoint => "checkpoint",
        BlobKind.Observation => "observation",
        BlobKind.Embedding => "embedding",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
