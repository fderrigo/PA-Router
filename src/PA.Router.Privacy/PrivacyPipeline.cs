using System.Text;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Privacy.Detectors;

namespace PA.Router.Privacy;

/// <summary>
/// Richiesta da analizzare prima di qualunque decisione di routing.
/// </summary>
/// <param name="Input">Testo della richiesta.</param>
/// <param name="Context">Elementi di contesto scelti dal chiamante.</param>
/// <param name="ProfileFloor">Classe minima imposta dal profilo.</param>
/// <param name="DeclaredClass">Classe dichiarata dal chiamante; puo' solo alzare il livello.</param>
/// <param name="RedactContacts">Obbligo di redazione dei contatti.</param>
/// <param name="Limits">Limiti di byte del profilo.</param>
/// <param name="PayloadBudgetBytes">Budget del payload; null usa il massimo consentito.</param>
/// <param name="Instructions">
/// Istruzioni dell'applicazione (ADR-020), null se assenti. Sono contenuto come il resto: si analizzano,
/// si minimizzano con gli stessi obblighi e alzano la classe, ma non si tagliano.
/// </param>
/// <param name="Tools">
/// Strumenti offerti al modello. Nome, descrizione e schema arrivano da una sorgente esterna e partono con il
/// payload: si esaminano e alzano la classe, ma non si trasformano.
/// </param>
public sealed record PrivacyRequest(
    string Input,
    IReadOnlyList<ContextItem> Context,
    DataClass ProfileFloor,
    DataClass? DeclaredClass,
    bool RedactContacts,
    PrivacyLimits Limits,
    int? PayloadBudgetBytes = null,
    string? Instructions = null,
    IReadOnlyList<ToolDefinition>? Tools = null,
    PersonalDataGrading Grading = PersonalDataGrading.Graded);

/// <summary>
/// Esito completo dell'analisi: il report della prima scansione, quello della scansione finale e
/// il payload autorizzabile. Entrambi i report restano disponibili perche' la classe effettiva e'
/// il massimo dei due: una trasformazione non puo' declassificare.
/// </summary>
/// <param name="InitialReport">Scansione del payload assemblato.</param>
/// <param name="FinalReport">Scansione del payload dopo redazione e trimming.</param>
/// <param name="Payload">Payload autorizzabile.</param>
/// <param name="Origins">
/// Da dove viene ogni dato trovato nella prima scansione — richiesta, cronologia, ricordi, allegati, istruzioni — con
/// tipo e rilevatore, mai valore ne' posizione. Serve a raccontare il turno (S117): la classe resta quella calcolata.
/// </param>
public sealed record PrivacyAnalysis(
    PrivacyReport InitialReport,
    PrivacyReport FinalReport,
    SafePayload Payload,
    IReadOnlyList<FindingOrigin>? Origins = null)
{
    /// <summary>
    /// Perche' il dato e' stato graduato stretto (ADR-029): <c>identifier</c>, <c>name-address</c> o <c>profile</c>;
    /// null se la gradazione non ha alzato nulla.
    /// </summary>
    public string? Grade { get; init; }

    /// <summary>Provenienza dei dati trovati; vuota se non calcolata.</summary>
    public IReadOnlyList<FindingOrigin> FindingOrigins => Origins ?? [];
}

/// <summary>
/// Pipeline privacy: assembla, analizza, minimizza secondo gli obblighi del profilo, riduce al
/// budget e rianalizza il risultato.
///
/// L'ordine e' quello della specifica e non e' negoziabile: scansione prima del trimming, cosi'
/// un segreto non sfugge finendo nella parte tagliata; nuova scansione dopo, cosi' il payload che
/// parte davvero e' quello che e' stato esaminato. La classe effettiva e' il massimo di tutti i
/// contributi, quindi nessun passaggio successivo puo' abbassarla.
/// </summary>
public sealed class PrivacyPipeline
{
    private readonly PrivacyScanner scanner;
    private readonly IMetadataDigest digest;

    public PrivacyPipeline(PrivacyScanner scanner, IMetadataDigest digest)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(digest);

        this.scanner = scanner;
        this.digest = digest;
    }

    /// <summary>Analizza la richiesta e produce il payload autorizzabile.</summary>
    public PrivacyAnalysis Analyze(PrivacyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnforceLimits(request);

        DataClass floor = DataClasses.Max(request.ProfileFloor, request.DeclaredClass ?? DataClass.Public);
        floor = DataClasses.Max(floor, request.Context.Select(item => item.DeclaredClass ?? request.ProfileFloor));

        List<ContextItem> included = [.. request.Context];
        string assembled = Assemble(request.Input, included);

        // 1. Scansione del payload assemblato: un segreto puo' comparire solo dopo l'unione di
        //    due frammenti innocui presi separatamente. I deterministici guardano il testo intero; lo stadio a
        //    modello pezzo per pezzo, e un pezzo gia' esaminato in un turno precedente non lo riesamina (S117).
        PrivacyReport initial = scanner.Scan(assembled, floor, Segments(request.Input, included), Attachments(included), Classified(request.Input, included));

        // 2. Obblighi del profilo. La redazione non tocca le credenziali: trasformarle per far
        //    passare un target cloud sarebbe esattamente il contrario del controllo.
        int redacted = 0;
        string payloadText = assembled;

        if (request.RedactContacts)
        {
            RedactionResult redaction = ContactRedactor.Redact(assembled, initial.Findings);
            payloadText = redaction.Text;
            redacted = redaction.ReplacementCount;
        }

        // 3. Riduzione al budget: si tolgono elementi di contesto interi, mai meta' di un
        //    elemento, e solo dopo che sono stati analizzati.
        int budget = request.PayloadBudgetBytes ?? request.Limits.MaxPayloadBytes;
        int beforeTrim = Encoding.UTF8.GetByteCount(payloadText);

        payloadText = TrimToBudget(request, included, payloadText, budget, redacted > 0, initial);
        int trimmed = beforeTrim - Encoding.UTF8.GetByteCount(payloadText);

        // 4. Nuova scansione del payload che parte davvero. Se non e' cambiato niente e' lo stesso testo della prima:
        //    rifarla dava lo stesso esito e costava una seconda passata del modello su tutto (S117). Senza redazione
        //    il testo e' ancora fatto di pezzi interi, e quelli gia' visti non si riesaminano.
        PrivacyReport final = string.Equals(payloadText, assembled, StringComparison.Ordinal)
            ? initial
            : scanner.Scan(
                payloadText,
                floor,
                redacted == 0 ? Segments(request.Input, included) : null,
                redacted == 0 ? Attachments(included) : null,
                redacted == 0 ? Classified(request.Input, included) : null);

        // 5. Le istruzioni dell'applicazione sono una seconda superficie che parte con il payload:
        //    stessa scansione prima e dopo la minimizzazione, e la loro classe entra in quella
        //    effettiva. Non si concatenano all'input, perche' al modello arrivano in un ruolo diverso.
        string? instructions = request.Instructions;
        PrivacyReport? instructionsInitial = null;
        PrivacyReport? instructionsFinal = null;

        if (instructions is not null)
        {
            instructionsInitial = scanner.Scan(instructions, floor);

            if (request.RedactContacts)
            {
                RedactionResult redaction = ContactRedactor.Redact(instructions, instructionsInitial.Findings);
                instructions = redaction.Text;
                redacted += redaction.ReplacementCount;
            }

            instructionsFinal = scanner.Scan(instructions, floor);
        }

        DataClass effective = DataClasses.Max(
            DataClasses.Max(initial.AggregateClass, final.AggregateClass),
            DataClasses.Max(
                instructionsInitial?.AggregateClass ?? DataClass.Public,
                instructionsFinal?.AggregateClass ?? DataClass.Public));

        byte[] bytes = Encoding.UTF8.GetBytes(payloadText);
        byte[] instructionBytes = instructions is null ? [] : Encoding.UTF8.GetBytes(instructions);

        // 6. Gli strumenti offerti: una descrizione cambiata non sarebbe piu' quella della sorgente, quindi
        //    non si redige; si esamina e la sua classe entra in quella effettiva.
        byte[] toolBytes = request.Tools is { Count: > 0 } offered ? Encoding.UTF8.GetBytes(ToolSurface(offered)) : [];
        PrivacyReport? toolsReport = toolBytes.Length == 0 ? null : scanner.Scan(Encoding.UTF8.GetString(toolBytes), floor);

        if (toolsReport is not null)
        {
            effective = DataClasses.Max(effective, toolsReport.AggregateClass);
        }

        // 7. I due gradi del dato personale (ADR-029): i rilevatori danno i segnali, la regola del profilo la classe.
        HashSet<FindingType> seen = [.. initial.Findings.Concat(final.Findings)
            .Concat(instructionsInitial?.Findings ?? []).Concat(toolsReport?.Findings ?? [])
            .Select(finding => finding.Type)];
        string? grade = Grade(seen, request.Grading);

        if (grade is not null)
        {
            effective = DataClasses.Max(effective, DataClass.Sensitive);
        }

        IReadOnlyList<ContextManifestEntry> manifest = BuildManifest(included, floor);

        List<FindingOrigin> origins = [.. Origins(assembled, request.Input, request.Context, initial.Findings)];
        origins.AddRange((instructionsInitial?.Findings ?? []).Concat(toolsReport?.Findings ?? [])
            .Select(finding => new FindingOrigin(finding.Type, finding.DetectorVersion, FindingSource.Instructions, null)));

        return new PrivacyAnalysis(
            Merge(Merge(initial, instructionsInitial), toolsReport),
            Merge(Merge(final, instructionsFinal), toolsReport),
            new SafePayload(
                bytes,
                PayloadDigest(digest, bytes, instructionBytes, toolBytes),
                digest.Compute(Encoding.UTF8.GetBytes(ManifestText(manifest))),
                effective,
                manifest,
                redacted,
                trimmed,
                instructionBytes,
                instructions is null ? null : digest.Compute(instructionBytes),
                request.Tools is { Count: > 0 } ? request.Tools : null),
            origins)
        {
            Grade = grade,
        };
    }

    /// <summary>Tipi che identificano una persona da soli: il dato e' stretto anche senza un nome accanto.</summary>
    private static readonly FindingType[] DirectIdentifiers =
        [FindingType.ItalianFiscalCode, FindingType.IdentityDocument, FindingType.Iban, FindingType.PaymentCard];

    /// <summary>Tipi che non sono dato personale di una persona: non contano per la regola «strict».</summary>
    private static readonly FindingType[] NotPersonal =
        [FindingType.Organisation, FindingType.ApiKey, FindingType.PrivateKey, FindingType.AssignedPassword, FindingType.HighEntropy];

    /// <summary>
    /// La regola di gradazione (ADR-029, <c>grading-v1</c>). Un nome da solo resta ordinario — nessun elenco di persone
    /// famose: il grado non dipende da chi e' la persona. Stretto per un identificativo diretto o per un nome con un
    /// indirizzo; con la regola <c>strict</c> del profilo, per qualunque dato personale.
    /// </summary>
    private static string? Grade(IReadOnlySet<FindingType> seen, PersonalDataGrading grading)
    {
        if (grading is PersonalDataGrading.Strict && seen.Any(type => !NotPersonal.Contains(type)))
        {
            return "profile";
        }

        if (DirectIdentifiers.Any(seen.Contains))
        {
            return "identifier";
        }

        return seen.Contains(FindingType.FullName) && seen.Contains(FindingType.PostalAddress) ? "name-address" : null;
    }

    /// <summary>
    /// I pezzi del payload assemblato che il rilevatore a modello esamina, in caratteri: la richiesta e il testo di ogni
    /// elemento di contesto, senza le intestazioni che aggiunge il router. Segue la forma di <see cref="Assemble"/>.
    ///
    /// **I ricordi no** (23 settembre 2026, decisione dell'operatore): un ricordo e' gia' stato esaminato quando e' nato e
    /// la sua classe viaggia con lui (<see cref="ContextItem.DeclaredClass"/>), che alza la classe del turno come prima.
    /// Riesaminarlo costava tempo e trovava cose che non ci sono: gli identificativi di vecchie conversazioni nella sua
    /// etichetta diventavano «IBAN», e un «ciao» diventava un dato sensibile. I rilevatori deterministici continuano a
    /// guardare tutto il testo: sono esatti, e servono alla redazione dei contatti.
    /// </summary>
    private static List<(int Start, int Length)> Segments(string input, IReadOnlyList<ContextItem> context)
    {
        List<(int Start, int Length)> segments = [(0, input.Length)];
        int position = input.Length;

        foreach (ContextItem item in context)
        {
            position += Header(item).Length;
            if (!IsClassified(item)) { segments.Add((position, item.Text.Length)); }
            position += item.Text.Length;
        }

        return segments;
    }

    /// <summary>
    /// Quali pezzi sono allegati — ne' cronologia ne' ricordi — per il loro budget separato. L'indice 0 e' la richiesta.
    /// </summary>
    private static HashSet<int> Attachments(IReadOnlyList<ContextItem> context)
    {
        // Gli indici sono quelli di Segments, che non contiene i ricordi: si contano solo i pezzi che ci sono.
        HashSet<int> attachments = [];
        int segment = 0;

        foreach (ContextItem item in context)
        {
            if (IsClassified(item)) { continue; }
            segment++;
            if (!HistoryItems.Is(item.Id)) { attachments.Add(segment); }
        }

        return attachments;
    }

    private static string Header(ContextItem item) => "\n\n[context:" + item.Id + "]\n";

    /// <summary>
    /// Un pezzo che porta gia' la sua classe: un ricordo, o la risposta di un turno precedente con la classe del suo turno
    /// (S48). Il modello dei nomi e l'elenco delle categorie particolari non lo riesaminano; i rilevatori di forme precise —
    /// credenziali, codice fiscale, IBAN, contatti — si' (23 settembre 2026).
    /// </summary>
    private static bool IsClassified(ContextItem item) =>
        item.Provenance is Provenance.Memory
        || (item.Provenance is Provenance.AgentOutput && HistoryItems.Is(item.Id) && item.DeclaredClass is not null);

    /// <summary>Dove stanno, in caratteri, i pezzi gia' classificati.</summary>
    private static List<(int Start, int Length)> Classified(string input, IReadOnlyList<ContextItem> context)
    {
        List<(int Start, int Length)> spans = [];
        int position = input.Length;

        foreach (ContextItem item in context)
        {
            position += Header(item).Length;
            if (IsClassified(item)) { spans.Add((position, item.Text.Length)); }
            position += item.Text.Length;
        }

        return spans;
    }

    /// <summary>
    /// Da quale pezzo viene ciascun ritrovamento. Per il codice fiscale dice anche se il carattere di controllo torna:
    /// il rilevatore a modello trova anche quelli scritti male, e chi legge deve sapere perche' i deterministici no.
    /// </summary>
    private static IEnumerable<FindingOrigin> Origins(
        string assembled,
        string input,
        IReadOnlyList<ContextItem> context,
        IReadOnlyList<PrivacyFinding> findings)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(assembled);
        List<(int End, FindingSource Source)> ranges = [(Encoding.UTF8.GetByteCount(input), FindingSource.Request)];
        int position = ranges[0].End;

        foreach (ContextItem item in context)
        {
            position += Encoding.UTF8.GetByteCount(Header(item)) + Encoding.UTF8.GetByteCount(item.Text);
            ranges.Add((position, HistoryItems.Is(item.Id) ? FindingSource.History
                : item.Provenance is Provenance.Memory ? FindingSource.Memory
                : FindingSource.Attachment));
        }

        foreach (PrivacyFinding finding in findings)
        {
            FindingSource source = ranges.FirstOrDefault(range => finding.StartByte < range.End) is { End: > 0 } found
                ? found.Source
                : FindingSource.Request;

            bool? valid = null;

            if (finding.Type is FindingType.ItalianFiscalCode
                && finding.StartByte >= 0 && finding.StartByte + finding.ByteLength <= bytes.Length)
            {
                string code = new([.. Encoding.UTF8.GetString(bytes, finding.StartByte, finding.ByteLength).Where(character => !char.IsWhiteSpace(character))]);
                valid = ItalianFiscalCodeDetector.HasValidChecksum(code);
            }

            yield return new FindingOrigin(finding.Type, finding.DetectorVersion, source, valid);
        }
    }

    /// <summary>
    /// Digest del payload che parte. Senza istruzioni e' quello dei soli byte, come prima; con le
    /// istruzioni le comprende con una lunghezza davanti, cosi' spostare testo fra le due superfici
    /// cambia il digest invece di lasciarlo uguale. Il grant e' legato a questo valore: istruzioni
    /// diverse non passano con il grant emesso per altre.
    /// </summary>
    public static string PayloadDigest(
        IMetadataDigest digest,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> instructions,
        ReadOnlySpan<byte> tools = default)
    {
        ArgumentNullException.ThrowIfNull(digest);

        if (instructions.IsEmpty && tools.IsEmpty)
        {
            return digest.Compute(payload);
        }

        byte[] header = Encoding.UTF8.GetBytes(
            "instructions:" + instructions.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\ntools:" + tools.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");

        byte[] framed = new byte[header.Length + instructions.Length + tools.Length + payload.Length];
        header.CopyTo(framed, 0);
        instructions.CopyTo(framed.AsSpan(header.Length));
        tools.CopyTo(framed.AsSpan(header.Length + instructions.Length));
        payload.CopyTo(framed.AsSpan(header.Length + instructions.Length + tools.Length));

        return digest.Compute(framed);
    }

    /// <summary>
    /// Report di tutte le superfici che partono. Classe e completezza sono quelle piu' vincolanti;
    /// gli span dei finding valgono ciascuno per la propria superficie, e nel report unito servono
    /// solo ai conteggi per tipo.
    /// </summary>
    private static PrivacyReport Merge(PrivacyReport payload, PrivacyReport? instructions) =>
        instructions is null
            ? payload
            : new PrivacyReport(
                payload.DetectorVersion,
                [.. payload.Findings, .. instructions.Findings],
                DataClasses.Max(payload.AggregateClass, instructions.AggregateClass),
                payload.Certainty is ScanCertainty.Incomplete || instructions.Certainty is ScanCertainty.Incomplete
                    ? ScanCertainty.Incomplete
                    : ScanCertainty.Complete,
                payload.ScannedBytes + instructions.ScannedBytes,
                [.. payload.IncompleteDetectors.Concat(instructions.IncompleteDetectors).Distinct(StringComparer.Ordinal)]);

    /// <summary>
    /// Esamina una superficie che nasce durante il turno: gli argomenti di una chiamata a uno strumento, o il
    /// suo risultato (ADR-020). Stessa scansione del payload, con la redazione dei contatti quando richiesta.
    /// </summary>
    public SurfaceAnalysis AnalyzeSurface(string text, DataClass floor, bool redactContacts)
    {
        ArgumentNullException.ThrowIfNull(text);

        PrivacyReport initial = scanner.Scan(text, floor);

        if (!redactContacts)
        {
            return new SurfaceAnalysis(text, initial, 0);
        }

        RedactionResult redaction = ContactRedactor.Redact(text, initial.Findings);

        return new SurfaceAnalysis(redaction.Text, Merge(initial, scanner.Scan(redaction.Text, floor)), redaction.ReplacementCount);
    }

    /// <summary>
    /// Forma canonica degli strumenti offerti, per la scansione e per il digest: nome, descrizione e schema
    /// nell'ordine in cui la richiesta li offre.
    /// </summary>
    public static string ToolSurface(IReadOnlyList<ToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        using MemoryStream buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartArray();

            foreach (ToolDefinition tool in tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                writer.WriteString("inputSchema", tool.InputSchemaJson);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void EnforceLimits(PrivacyRequest request)
    {
        PrivacyScanner.EnsureWithinLimit(request.Input, request.Limits.MaxInputBytes, "input");

        if (request.Instructions is not null)
        {
            PrivacyScanner.EnsureWithinLimit(request.Instructions, request.Limits.MaxInstructionsBytes, "instructions");
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            PrivacyScanner.EnsureWithinLimit(ToolSurface(tools), request.Limits.MaxToolDefinitionsBytes, "tools");
        }

        int total = 0;

        foreach (ContextItem item in request.Context)
        {
            PrivacyScanner.EnsureWithinLimit(item.Text, request.Limits.MaxContextItemBytes, "context item");
            total += Encoding.UTF8.GetByteCount(item.Text);
        }

        if (total > request.Limits.MaxContextBytes)
        {
            throw new PaRouterException(
                ErrorCode.InputInvalid,
                "The aggregated context exceeds the byte limit of the profile.")
                .WithMetadata("surface", "context")
                .WithMetadata("bytes", total.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithMetadata("maxBytes", request.Limits.MaxContextBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        HashSet<string> identifiers = new(StringComparer.Ordinal);

        foreach (ContextItem item in request.Context)
        {
            if (!identifiers.Add(item.Id))
            {
                throw new PaRouterException(
                    ErrorCode.InputInvalid,
                    "Two context items share the same identifier.")
                    .WithMetadata("itemId", item.Id);
            }
        }
    }

    /// <summary>
    /// Composizione deterministica del payload: stesso input e stesso contesto producono sempre
    /// gli stessi byte, quindi lo stesso digest.
    /// </summary>
    private static string Assemble(string input, IReadOnlyList<ContextItem> context)
    {
        StringBuilder builder = new();
        builder.Append(input);

        foreach (ContextItem item in context)
        {
            builder.Append(Header(item)).Append(item.Text);
        }

        return builder.ToString();
    }

    private string TrimToBudget(
        PrivacyRequest request,
        List<ContextItem> included,
        string payloadText,
        int budget,
        bool redacted,
        PrivacyReport initial)
    {
        if (Encoding.UTF8.GetByteCount(payloadText) <= budget)
        {
            return payloadText;
        }

        // Si eliminano gli elementi dal fondo: l'ordine di arrivo esprime la priorita' scelta da
        // chi ha costruito la richiesta, e l'input non viene mai tagliato.
        while (included.Count > 0 && Encoding.UTF8.GetByteCount(payloadText) > budget)
        {
            included.RemoveAt(included.Count - 1);
            payloadText = Assemble(request.Input, included);

            if (redacted)
            {
                payloadText = ContactRedactor.Redact(payloadText, scanner.Scan(payloadText, initial.AggregateClass).Findings).Text;
            }
        }

        if (Encoding.UTF8.GetByteCount(payloadText) > budget)
        {
            throw new PaRouterException(
                ErrorCode.ContextLimit,
                "The request does not fit the payload budget even without any context.")
                .WithMetadata("budgetBytes", budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return payloadText;
    }

    private IReadOnlyList<ContextManifestEntry> BuildManifest(IReadOnlyList<ContextItem> included, DataClass floor) =>
        [.. included.Select(item => new ContextManifestEntry(
            item.Id,
            item.Provenance,
            DataClasses.Max(floor, item.DeclaredClass ?? floor),
            Encoding.UTF8.GetByteCount(item.Text),
            digest.Compute(Encoding.UTF8.GetBytes(item.Text))))];

    /// <summary>
    /// Forma canonica del manifest per il digest: solo identificatori, provenienza, classe e
    /// digest degli elementi. Nessun testo entra nel calcolo.
    /// </summary>
    private static string ManifestText(IReadOnlyList<ContextManifestEntry> manifest) =>
        string.Join(
            '\n',
            manifest.Select(entry =>
                entry.ItemId + "|" + entry.Provenance.ToWire() + "|" + entry.EffectiveClass.ToWire()
                + "|" + entry.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "|" + entry.Digest));
}

/// <summary>Esito dell'esame di una superficie nata durante il turno.</summary>
/// <param name="Text">Testo dopo la redazione, se richiesta.</param>
/// <param name="Report">Report delle scansioni.</param>
/// <param name="RedactedCount">Sostituzioni applicate.</param>
public sealed record SurfaceAnalysis(string Text, PrivacyReport Report, int RedactedCount)
{
    /// <summary>Classe del testo: una scansione incompleta vale <c>unknown</c>.</summary>
    public DataClass EffectiveClass =>
        Report.Certainty is ScanCertainty.Incomplete ? DataClass.Unknown : Report.AggregateClass;

    /// <summary>Nessun contenuto nella resa testuale.</summary>
    public override string ToString() => "SurfaceAnalysis { Class = " + EffectiveClass.ToWire() + " }";
}
