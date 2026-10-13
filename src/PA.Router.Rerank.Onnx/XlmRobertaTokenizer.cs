using Microsoft.ML.Tokenizers;
using PA.Router.Abstractions;

namespace PA.Router.Rerank.Onnx;

/// <summary>
/// Tokenizer dei cross-encoder della famiglia XLM-RoBERTa, che e' quella dei riordinatori
/// multilingue.
///
/// Il modello SentencePiece viene letto dal file del fornitore, non reimplementato: la
/// segmentazione Unigram, la normalizzazione precompilata e il marcatore di spazio stanno dentro
/// quel file, e riscriverli a mano sarebbe il modo piu' rapido di ottenere una segmentazione
/// leggermente diversa da quella con cui il modello e' stato addestrato — che non fallisce, peggiora
/// e basta.
///
/// Una cosa va aggiunta sopra, ed e' l'unica: XLM-RoBERTa **non** usa gli identificatori di
/// SentencePiece cosi' come sono. Il suo vocabolario mette in testa quattro token speciali e
/// sposta tutto il resto di uno. La verifica e' in <c>XlmRobertaTokenizerTests</c>, contro id
/// prodotti dal tokenizer del fornitore.
/// </summary>
public sealed class XlmRobertaTokenizer : IDisposable
{
    /// <summary>File del modello SentencePiece, come lo nomina il fornitore.</summary>
    public const string ModelFileName = "sentencepiece.bpe.model";

    /// <summary>Scostamento fra gli identificatori di SentencePiece e quelli di XLM-RoBERTa.</summary>
    private const int FairseqOffset = 1;

    private readonly SentencePieceTokenizer tokenizer;

    private XlmRobertaTokenizer(SentencePieceTokenizer tokenizer) => this.tokenizer = tokenizer;

    /// <summary>Inizio sequenza.</summary>
    public const int BeginOfSequence = 0;

    /// <summary>Riempimento. Le posizioni riempite hanno maschera di attenzione zero.</summary>
    public const int Padding = 1;

    /// <summary>Fine sequenza, e separatore fra le due parti di una coppia.</summary>
    public const int EndOfSequence = 2;

    /// <summary>Apre il tokenizer dal file del modello nella directory indicata.</summary>
    public static XlmRobertaTokenizer Load(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

        string path = Path.Combine(modelDirectory, ModelFileName);

        if (!File.Exists(path))
        {
            throw new PaRouterException(
                ErrorCode.TargetUnavailable,
                "The reranker model directory does not contain the tokenizer model file.")
                .WithMetadata("file", ModelFileName);
        }

        using FileStream stream = File.OpenRead(path);

        // I marcatori di inizio e fine li aggiunge la costruzione della coppia: una coppia
        // domanda/documento non e' due frasi indipendenti incollate, e lasciarli al tokenizer
        // darebbe una sequenza con i separatori nel posto sbagliato.
        return new XlmRobertaTokenizer(
            SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false));
    }

    /// <summary>Identificatori del testo, gia' riportati nello spazio di XLM-RoBERTa.</summary>
    public IReadOnlyList<int> Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return [.. tokenizer.EncodeToIds(text).Select(id => id + FairseqOffset)];
    }

    /// <summary>
    /// Sequenza di una coppia domanda/documento: <c>&lt;s&gt; domanda &lt;/s&gt;&lt;/s&gt;
    /// documento &lt;/s&gt;</c>.
    ///
    /// Il taglio, quando serve, cade sul **documento**: la domanda e' corta e toglierne un pezzo
    /// cambierebbe cio' che si sta chiedendo, mentre un documento troncato resta una versione
    /// parziale di se stesso. La domanda piu' lunga della finestra viene troncata per ultima,
    /// perche' altrimenti non resterebbe spazio per nulla.
    /// </summary>
    public int[] EncodePair(string query, string document, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 8);

        // Quattro posizioni per i marcatori: <s>, </s></s>, </s>.
        int available = maxTokens - 4;

        IReadOnlyList<int> queryIds = Encode(query);
        IReadOnlyList<int> documentIds = Encode(document);

        int queryLength = Math.Min(queryIds.Count, Math.Max(available / 2, available - documentIds.Count));
        int documentLength = Math.Min(documentIds.Count, available - queryLength);

        int[] sequence = new int[4 + queryLength + documentLength];
        int position = 0;

        sequence[position++] = BeginOfSequence;

        for (int index = 0; index < queryLength; index++)
        {
            sequence[position++] = queryIds[index];
        }

        sequence[position++] = EndOfSequence;
        sequence[position++] = EndOfSequence;

        for (int index = 0; index < documentLength; index++)
        {
            sequence[position++] = documentIds[index];
        }

        sequence[position] = EndOfSequence;

        return sequence;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // SentencePieceTokenizer non possiede risorse native: il metodo esiste perche' il tipo
        // faccia parte del ciclo di vita del riordinatore senza che il chiamante debba saperlo.
        GC.SuppressFinalize(this);
    }
}
