using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PA.Router.Abstractions;

namespace PA.Router.Rerank.Onnx;

/// <summary>
/// Riordinatore delle candidate con un cross-encoder locale esportato in ONNX.
///
/// La differenza con un modello di embedding e' quella che conta: un embedding vede la domanda e il
/// documento **separatamente** e li confronta per vicinanza di due vettori, un cross-encoder li
/// legge **insieme** in una sola passata e risponde quanto il documento risponde a quella domanda.
/// E' piu' preciso ed e' molto piu' caro, e per questo uno recupera e l'altro riordina: il primo
/// guarda tutto l'indice una volta, il secondo poche decine di candidate.
///
/// Gira in processo come il rilevatore PII, con lo stesso patto: il prodotto contiene il caricatore
/// e il tokenizer, i pesi li installa l'operatore con <c>eng/export-reranker-model.ps1</c>. Nessuna
/// porta, nessun programma da avviare, nessun egress.
/// </summary>
public sealed class OnnxReranker : IReranker
{
    /// <summary>Nome del file del modello esportato, atteso nella directory indicata.</summary>
    public const string ModelFileName = "model.onnx";

    /// <summary>Nome del file di configurazione, da cui arriva la finestra del modello.</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>
    /// Finestra usata quando la configurazione ne dichiara una piu' grande.
    ///
    /// I cross-encoder recenti accettano migliaia di token, ma il costo cresce con il quadrato
    /// della lunghezza e una nota di memoria sta in poche centinaia. Allargare la finestra
    /// pagherebbe un prezzo alto per del riempimento.
    /// </summary>
    public const int WindowTokens = 512;

    /// <summary>Coppie valutate in una sola passata del grafo.</summary>
    private const int BatchSize = 8;

    private readonly InferenceSession session;
    private readonly XlmRobertaTokenizer tokenizer;
    private readonly string inputIdsName;
    private readonly string attentionMaskName;
    private readonly int window;

    private OnnxReranker(
        InferenceSession session,
        XlmRobertaTokenizer tokenizer,
        string name,
        string inputIdsName,
        string attentionMaskName,
        int window)
    {
        this.session = session;
        this.tokenizer = tokenizer;
        this.inputIdsName = inputIdsName;
        this.attentionMaskName = attentionMaskName;
        this.window = window;

        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Version => "onnx-rerank-v1";

    /// <summary>Vero se la directory contiene i file che servono per aprire il modello.</summary>
    public static bool IsInstalled(string modelDirectory) =>
        !string.IsNullOrWhiteSpace(modelDirectory)
        && File.Exists(Path.Combine(modelDirectory, ModelFileName))
        && File.Exists(Path.Combine(modelDirectory, XlmRobertaTokenizer.ModelFileName))
        && File.Exists(Path.Combine(modelDirectory, ConfigFileName));

    /// <summary>Apre il modello installato nella directory indicata.</summary>
    public static OnnxReranker Load(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

        if (!IsInstalled(modelDirectory))
        {
            throw new PaRouterException(
                ErrorCode.TargetUnavailable,
                "The local reranker model is not installed in the declared directory.")
                .WithMetadata("reason", "model_not_installed");
        }

        XlmRobertaTokenizer tokenizer = XlmRobertaTokenizer.Load(modelDirectory);

        SessionOptions options = new()
        {
            // Il riordino gira mentre l'operatore aspetta una risposta: occupare tutti i core
            // renderebbe piu' lento cio' che sta aspettando.
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            InterOpNumThreads = 1,
        };

        InferenceSession session = new(Path.Combine(modelDirectory, ModelFileName), options);

        string inputIds = session.InputMetadata.Keys.FirstOrDefault(name => name.Contains("input_ids", StringComparison.Ordinal))
            ?? throw new PaRouterException(ErrorCode.ProtocolError, "The exported model has no input_ids input.");

        string attentionMask = session.InputMetadata.Keys.FirstOrDefault(name => name.Contains("attention_mask", StringComparison.Ordinal))
            ?? throw new PaRouterException(ErrorCode.ProtocolError, "The exported model has no attention_mask input.");

        return new OnnxReranker(
            session,
            tokenizer,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(modelDirectory)),
            inputIds,
            attentionMask,
            ReadWindow(Path.Combine(modelDirectory, ConfigFileName)));
    }

    /// <inheritdoc />
    public IReadOnlyList<double> Score(string query, IReadOnlyList<string> documents, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(documents);

        if (documents.Count == 0)
        {
            return [];
        }

        // Il valore neutro non e' zero: un logit zero e' un punteggio come un altro, e mescolarlo
        // ai punteggi veri sposterebbe le candidate non valutate in mezzo alla classifica. Chi non
        // viene valutato resta dove il recupero lo aveva messo, e il chiamante lo distingue.
        double[] scores = new double[documents.Count];
        Array.Fill(scores, double.NegativeInfinity);

        Stopwatch elapsed = Stopwatch.StartNew();
        bool first = true;

        for (int offset = 0; offset < documents.Count; offset += BatchSize)
        {
            // La prima passata si fa sempre: un budget cosi' stretto da non valutarne nessuna
            // renderebbe il riordino un interruttore nascosto invece di un limite di costo.
            if (!first && budget > TimeSpan.Zero && elapsed.Elapsed >= budget)
            {
                break;
            }

            first = false;

            int count = Math.Min(BatchSize, documents.Count - offset);

            ScoreBatch(query, documents, offset, count, scores);
        }

        return scores;
    }

    private void ScoreBatch(
        string query,
        IReadOnlyList<string> documents,
        int offset,
        int count,
        double[] scores)
    {
        int[][] sequences = new int[count][];
        int longest = 0;

        for (int index = 0; index < count; index++)
        {
            sequences[index] = tokenizer.EncodePair(query, documents[offset + index], window);
            longest = Math.Max(longest, sequences[index].Length);
        }

        long[] ids = new long[count * longest];
        long[] mask = new long[count * longest];

        for (int row = 0; row < count; row++)
        {
            int[] sequence = sequences[row];

            for (int column = 0; column < longest; column++)
            {
                int position = (row * longest) + column;

                // Oltre la fine della sequenza si riempie, e la maschera a zero dice al modello di
                // ignorare quelle posizioni: senza, il riempimento parteciperebbe al punteggio.
                ids[position] = column < sequence.Length ? sequence[column] : XlmRobertaTokenizer.Padding;
                mask[position] = column < sequence.Length ? 1 : 0;
            }
        }

        DenseTensor<long> idTensor = new(ids, [count, longest]);
        DenseTensor<long> maskTensor = new(mask, [count, longest]);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run(
        [
            NamedOnnxValue.CreateFromTensor(inputIdsName, idTensor),
            NamedOnnxValue.CreateFromTensor(attentionMaskName, maskTensor),
        ]);

        Tensor<float> logits = outputs.First().AsTensor<float>();

        for (int row = 0; row < count; row++)
        {
            // Un cross-encoder di riordino ha una sola uscita per coppia: quel logit e' il
            // punteggio. Non viene passato per una sigmoide perche' serve a ordinare, e una
            // funzione monotona non cambia un ordinamento - cambierebbe solo la scala, dando
            // l'impressione di una probabilita' che nessuno ha calibrato.
            scores[offset + row] = logits[row, 0];
        }
    }

    /// <summary>
    /// Finestra del modello, dalla configurazione del fornitore, limitata a quella che conviene
    /// davvero pagare.
    /// </summary>
    private static int ReadWindow(string configurationPath)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configurationPath));

            if (document.RootElement.TryGetProperty("max_position_embeddings", out JsonElement declared)
                && declared.TryGetInt32(out int value)
                && value >= 64)
            {
                return Math.Min(value, WindowTokens);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // Configurazione illeggibile: la finestra predefinita e' un valore prudente, non un
            // ripiego che nasconde un file rotto - il caricamento del grafo fallirebbe subito dopo.
        }

        return WindowTokens;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        session.Dispose();
        tokenizer.Dispose();
    }
}
