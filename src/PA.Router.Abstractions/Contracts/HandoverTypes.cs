namespace PA.Router.Abstractions.Contracts;

/// <summary>Chi ha prodotto una capsule.</summary>
public enum CapsuleProducer
{
    [WireValue("runtime")]
    Runtime,

    [WireValue("operator")]
    Operator,

    [WireValue("agent")]
    Agent,
}

/// <summary>Tipo di modifica osservata su un file.</summary>
public enum ChangeKind
{
    [WireValue("added")]
    Added,

    [WireValue("modified")]
    Modified,

    [WireValue("deleted")]
    Deleted,
}

/// <summary>
/// Fatto trasportato dalla capsule, con il riferimento alla prova e il proprio stato.
///
/// <c>verified</c> significa che qualcuno ha osservato la prova indicata; un fatto prodotto da un
/// agente resta <c>unverified</c> finche' non e' stato controllato (03-specs/SESSION-HANDOVER.md).
/// </summary>
/// <param name="Text">Enunciato.</param>
/// <param name="Evidence">Riferimento alla prova.</param>
/// <param name="Status">Stato di verifica.</param>
public sealed record VerifiedFact(string Text, string Evidence, Verification Status);

/// <summary>Artefatto modificato durante il lavoro.</summary>
/// <param name="Path">Percorso relativo al workspace.</param>
/// <param name="Digest">Digest del contenuto osservato.</param>
/// <param name="Change">Tipo di modifica.</param>
public sealed record ArtifactChange(string Path, string Digest, ChangeKind Change);

/// <summary>
/// Fotografia del workspace al momento della capsule.
///
/// Prova una differenza, non la assenza di modifiche: se fra la creazione e il dispatch qualcosa
/// cambia, il confronto se ne accorge e il turno riparte dalla preparazione. Il checkpoint non
/// esegue reset ne' clean, e i nomi dei file restano nel blob cifrato.
/// </summary>
/// <param name="RepoRootDigest">HMAC della radice del workspace.</param>
/// <param name="GitHead">Commit corrente.</param>
/// <param name="DirtyManifestDigest">HMAC del manifesto delle modifiche non committate.</param>
public sealed record WorkspaceCheckpoint(string RepoRootDigest, string GitHead, string DirtyManifestDigest);

/// <summary>Origine della capsule.</summary>
/// <param name="Producer">Chi l'ha prodotta.</param>
/// <param name="CreatedAt">Quando.</param>
public sealed record CapsuleProvenance(CapsuleProducer Producer, DateTimeOffset CreatedAt)
{
    /// <summary>
    /// Il contenuto della capsule non e' mai un'istruzione fidata: e' sempre falso, e lo schema lo
    /// impone come costante. Un testo che chiedesse di cambiare policy resta un dato.
    /// </summary>
    public bool TrustedInstructions => false;
}

/// <summary>
/// Capsule di passaggio fra agenti (06-contracts/handover.schema.json).
///
/// E' deterministica e costruita da dati strutturati: nessun riassunto prodotto da un modello, e
/// nessun transcript nativo dell'altro provider. Trasporta obiettivo, vincoli, fatti con le loro
/// prove, decisioni, lavoro aperto, artefatti e checkpoint.
/// </summary>
/// <param name="SessionId">Sessione di origine.</param>
/// <param name="SourceExecutionId">Esecuzione che l'ha prodotta.</param>
/// <param name="Objective">Obiettivo del lavoro.</param>
/// <param name="Constraints">Vincoli che restano validi.</param>
/// <param name="VerifiedFacts">Fatti con prova e stato.</param>
/// <param name="Decisions">Decisioni prese.</param>
/// <param name="OpenWork">Lavoro ancora aperto.</param>
/// <param name="Artifacts">File modificati.</param>
/// <param name="Checkpoint">Fotografia del workspace.</param>
/// <param name="Provenance">Origine.</param>
/// <param name="MemoryIds">Identificatori dei ricordi selezionati, non i loro contenuti.</param>
public sealed record HandoverCapsule(
    string SessionId,
    string SourceExecutionId,
    string Objective,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<VerifiedFact> VerifiedFacts,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> OpenWork,
    IReadOnlyList<ArtifactChange> Artifacts,
    WorkspaceCheckpoint Checkpoint,
    CapsuleProvenance Provenance,
    IReadOnlyList<string> MemoryIds)
{
    /// <summary>Versione dello schema della capsule.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Resa compatta: nessun contenuto nel log.</summary>
    public override string ToString() =>
        "HandoverCapsule { Session = " + SessionId + ", Source = " + SourceExecutionId
        + ", Facts = " + VerifiedFacts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " }";
}

/// <summary>Un turno conservato nella cronologia della sessione.</summary>
/// <param name="Ordinal">Posizione, da 1.</param>
/// <param name="Input">Messaggio dell'utente.</param>
/// <param name="Output">Risposta gia' filtrata dallo screening.</param>
/// <param name="ExecutionId">Esecuzione corrispondente.</param>
/// <param name="CreatedAt">Istante.</param>
/// <param name="DeclaredClass">
/// Classe della conversazione a fine turno, quando e' nota: rientrando nel contesto il turno la conserva, invece di valere
/// quanto una nuova scansione riesce a vedere.
/// </param>
public sealed record SessionTurn(
    int Ordinal,
    string Input,
    string? Output,
    string ExecutionId,
    DateTimeOffset CreatedAt,
    DataClass? DeclaredClass = null)
{
    /// <summary>Servizio che ha eseguito il turno. Assente nei turni scritti prima di S116.</summary>
    public string? ServiceId { get; init; }

    /// <summary>Sessione nativa dell'agente usata dal turno, quando ce n'era una (ADR-022).</summary>
    public string? NativeSessionId { get; init; }

    /// <summary>Nessun testo nella resa: la cronologia e' contenuto dell'utente.</summary>
    public override string ToString() =>
        "SessionTurn { Ordinal = " + Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ", Execution = " + ExecutionId + " }";
}

/// <summary>Limiti della costruzione del contesto, da 03-specs/SESSION-HANDOVER.md.</summary>
public static class ContextLimits
{
    /// <summary>Margine della finestra riservato all'overhead del provider.</summary>
    public const double WindowMargin = 0.10;

    /// <summary>
    /// Stima conservativa usata dove non esiste un tokenizer verificato: un token per byte UTF-8.
    /// Sovrastima, e va bene cosi': sbagliare per eccesso taglia contesto, sbagliare per difetto
    /// produce una richiesta rifiutata dal provider.
    /// </summary>
    public const int BytesPerToken = 1;
}
