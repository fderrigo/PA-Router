using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PA.Router.Abstractions;
using PA.Router.Privacy.Model;

namespace PA.Router.Detectors.Onnx;

/// <summary>
/// Rilevatore di dati personali con modello locale esportato in ONNX.
///
/// Gira **in processo**: nessun servizio da avviare, nessuna porta, nessun egress. Il modello e i
/// suoi file li installa l'operatore; se mancano, il rilevatore non parte e lo dichiara, come fa
/// <c>doctor</c> con un modello di chat non installato.
///
/// La classificazione e' per token con etichette BIO: ogni token riceve un'etichetta e le entita'
/// si ricostruiscono unendo i token contigui della stessa classe. Gli span tornano in caratteri
/// del testo originale, perche' un riconoscimento che non dice **dove** non serve a redigere ne' a
/// dichiarare.
/// </summary>
public sealed class OnnxPiiModel : IPiiModel
{
    /// <summary>Nome del file del modello esportato, atteso nella directory indicata.</summary>
    public const string ModelFileName = "model.onnx";

    /// <summary>Nome del file del tokenizer, come lo pubblica il fornitore del modello.</summary>
    public const string TokenizerFileName = "tokenizer.json";

    /// <summary>Nome del file di configurazione, da cui arrivano le etichette.</summary>
    public const string ConfigFileName = "config.json";

    private readonly InferenceSession session;
    private readonly GemmaBpeTokenizer tokenizer;
    private readonly IReadOnlyList<string> labels;
    private readonly string inputIdsName;
    private readonly string attentionMaskName;

    private OnnxPiiModel(
        InferenceSession session,
        GemmaBpeTokenizer tokenizer,
        IReadOnlyList<string> labels,
        int maxTokens,
        string name,
        string inputIdsName,
        string attentionMaskName)
    {
        this.session = session;
        this.tokenizer = tokenizer;
        this.labels = labels;
        this.inputIdsName = inputIdsName;
        this.attentionMaskName = attentionMaskName;

        MaxTokens = maxTokens;
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Version => "onnx-pii-v1";

    /// <inheritdoc />
    public int MaxTokens { get; }

    /// <summary>Vero se la directory contiene i tre file che servono.</summary>
    public static bool IsInstalled(string modelDirectory) =>
        !string.IsNullOrWhiteSpace(modelDirectory)
        && File.Exists(Path.Combine(modelDirectory, ModelFileName))
        && File.Exists(Path.Combine(modelDirectory, TokenizerFileName))
        && File.Exists(Path.Combine(modelDirectory, ConfigFileName));

    /// <summary>
    /// Apre il modello installato nella directory indicata. Un modello assente non viene scaricato:
    /// e' una decisione dell'operatore, non l'effetto collaterale di una scansione.
    /// </summary>
    public static OnnxPiiModel Load(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

        if (!IsInstalled(modelDirectory))
        {
            throw new PaRouterException(
                ErrorCode.TargetUnavailable,
                "The local PII model is not installed in the declared directory.")
                .WithMetadata("reason", "model_not_installed");
        }

        (IReadOnlyList<string> labels, int maxTokens) = ReadConfiguration(Path.Combine(modelDirectory, ConfigFileName));

        // Tokenizer e sessione si caricano insieme (24 settembre 2026): la sessione aspetta soprattutto il disco — 1,2 GB, o
        // 300 MB quantizzato — e il tokenizer il processore, quasi due secondi. In fila erano il primo turno di ogni processo.
        Task<GemmaBpeTokenizer> tokenizing = Task.Run(() => GemmaBpeTokenizer.Load(Path.Combine(modelDirectory, TokenizerFileName)));

        SessionOptions options = new()
        {
            // Il rilevamento gira accanto a un turno: occupare tutti i core renderebbe piu' lento
            // cio' che l'operatore sta aspettando.
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            InterOpNumThreads = 1,
        };

        InferenceSession session = new(Path.Combine(modelDirectory, ModelFileName), options);
        GemmaBpeTokenizer tokenizer = tokenizing.GetAwaiter().GetResult();

        string inputIds = session.InputMetadata.Keys.FirstOrDefault(name => name.Contains("input_ids", StringComparison.Ordinal))
            ?? throw new PaRouterException(ErrorCode.ProtocolError, "The exported model has no input_ids input.");

        string attentionMask = session.InputMetadata.Keys.FirstOrDefault(name => name.Contains("attention_mask", StringComparison.Ordinal))
            ?? throw new PaRouterException(ErrorCode.ProtocolError, "The exported model has no attention_mask input.");

        return new OnnxPiiModel(
            session,
            tokenizer,
            labels,
            maxTokens,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(modelDirectory)),
            inputIds,
            attentionMask);
    }

    /// <inheritdoc />
    public PiiScanResult Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        IReadOnlyList<EncodedSpan> spans = tokenizer.Encode(text);

        if (spans.Count == 0)
        {
            return new PiiScanResult([], Truncated: false);
        }

        // Due posizioni restano ai token di inizio e fine, che il modello si aspetta.
        int usable = Math.Min(spans.Count, MaxTokens - 2);
        bool truncated = usable < spans.Count;

        long[] ids = new long[usable + 2];
        long[] mask = new long[usable + 2];

        ids[0] = tokenizer.BeginOfSequence;
        mask[0] = 1;

        for (int index = 0; index < usable; index++)
        {
            ids[index + 1] = spans[index].Id;
            mask[index + 1] = 1;
        }

        ids[usable + 1] = tokenizer.EndOfSequence;
        mask[usable + 1] = 1;

        DenseTensor<long> idTensor = new(ids, [1, ids.Length]);
        DenseTensor<long> maskTensor = new(mask, [1, mask.Length]);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run(
        [
            NamedOnnxValue.CreateFromTensor(inputIdsName, idTensor),
            NamedOnnxValue.CreateFromTensor(attentionMaskName, maskTensor),
        ]);

        Tensor<float> logits = outputs.First().AsTensor<float>();

        return new PiiScanResult(Decode(spans, usable, logits), truncated);
    }

    /// <inheritdoc />
    public void Dispose() => session.Dispose();

    /// <summary>
    /// Ricostruisce le entita' dalle etichette BIO. Un token <c>I-</c> senza un <c>B-</c> della
    /// stessa classe davanti apre comunque un'entita': un'etichetta incoerente non deve far
    /// perdere un dato personale, e un frammento in piu' e' meno grave di un frammento in meno.
    /// </summary>
    private IReadOnlyList<PiiEntity> Decode(IReadOnlyList<EncodedSpan> spans, int usable, Tensor<float> logits)
    {
        List<PiiEntity> entities = [];

        string? current = null;
        int start = 0;
        int end = 0;
        double confidence = 0;
        int tokens = 0;

        for (int index = 0; index < usable; index++)
        {
            (string label, double score) = Best(logits, index + 1);

            string? entity = label is "O" ? null : label[2..];
            bool begins = label.StartsWith("B-", StringComparison.Ordinal);

            if (entity is null || (begins && current is not null) || entity != current)
            {
                Flush(entities, current, start, end, confidence, tokens);

                current = entity;
                start = spans[index].Start;
                end = spans[index].Start + spans[index].Length;
                confidence = score;
                tokens = 1;

                continue;
            }

            end = spans[index].Start + spans[index].Length;
            confidence += score;
            tokens++;
        }

        Flush(entities, current, start, end, confidence, tokens);

        return entities;
    }

    private static void Flush(List<PiiEntity> entities, string? label, int start, int end, double confidence, int tokens)
    {
        if (label is null || tokens == 0 || end <= start)
        {
            return;
        }

        entities.Add(new PiiEntity(label, start, end - start, confidence / tokens));
    }

    /// <summary>Etichetta piu' probabile per una posizione, con la sua probabilita' normalizzata.</summary>
    private (string Label, double Score) Best(Tensor<float> logits, int position)
    {
        int best = 0;
        float top = float.NegativeInfinity;
        double total = 0;

        for (int label = 0; label < labels.Count; label++)
        {
            float value = logits[0, position, label];

            if (value > top)
            {
                top = value;
                best = label;
            }
        }

        for (int label = 0; label < labels.Count; label++)
        {
            total += Math.Exp(logits[0, position, label] - top);
        }

        return (labels[best], 1.0 / total);
    }

    /// <summary>
    /// Etichette e finestra del modello, lette dalla sua configurazione. Nessun valore viene
    /// assunto: un modello con etichette diverse da quelle attese produce entita' che il prodotto
    /// non traduce, e le ignora invece di indovinarle.
    /// </summary>
    private static (IReadOnlyList<string> Labels, int MaxTokens) ReadConfiguration(string configPath)
    {
        using FileStream file = File.OpenRead(configPath);
        using JsonDocument document = JsonDocument.Parse(file);

        if (!document.RootElement.TryGetProperty("id2label", out JsonElement map))
        {
            throw new PaRouterException(ErrorCode.ProtocolError, "The model configuration declares no labels.");
        }

        SortedDictionary<int, string> labels = [];

        foreach (JsonProperty entry in map.EnumerateObject())
        {
            labels[int.Parse(entry.Name, System.Globalization.CultureInfo.InvariantCulture)] = entry.Value.GetString()!;
        }

        int maxTokens = document.RootElement.TryGetProperty("max_position_embeddings", out JsonElement window)
            ? window.GetInt32()
            : 512;

        return ([.. labels.Values], maxTokens);
    }
}
