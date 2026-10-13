using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace PA.Router.Detectors.Onnx;

/// <summary>Un token con il suo identificatore e lo span in caratteri del testo originale.</summary>
/// <param name="Id">Identificatore nel vocabolario.</param>
/// <param name="Start">Primo carattere del testo originale coperto dal token.</param>
/// <param name="Length">Caratteri coperti.</param>
public readonly record struct EncodedSpan(int Id, int Start, int Length);

/// <summary>
/// Tokenizer del modello, letto dal suo <c>tokenizer.json</c>.
///
/// Il modello usa il tokenizer di Gemma: BPE su 256.000 voci, normalizzatore che sostituisce lo
/// spazio con <c>U+2581</c> e pre-tokenizzatore Metaspace con prefisso sempre presente. Non viene
/// reimplementato l'algoritmo BPE — lo fornisce <see cref="BpeTokenizer"/> — ma la
/// pre-tokenizzazione si', perche' serve una cosa che la libreria non restituisce: lo **span in
/// caratteri del testo originale** per ciascun token.
///
/// Senza quello span un riconoscimento non e' utilizzabile: sapere che i token 7..9 sono un nome
/// non dice quale parte del testo redigere o dichiarare. Per questo il testo viene diviso in
/// parole tenendo la posizione di ognuna, e ogni parola viene codificata da sola.
/// </summary>
public sealed class GemmaBpeTokenizer
{
    /// <summary>Carattere che sostituisce lo spazio, come dichiara il tokenizer del modello.</summary>
    public const char Metaspace = '▁';

    private readonly BpeTokenizer bpe;

    private GemmaBpeTokenizer(BpeTokenizer bpe, int beginOfSequence, int endOfSequence, int unknown)
    {
        this.bpe = bpe;
        BeginOfSequence = beginOfSequence;
        EndOfSequence = endOfSequence;
        Unknown = unknown;
    }

    /// <summary>Identificatore del token iniziale.</summary>
    public int BeginOfSequence { get; }

    /// <summary>Identificatore del token finale.</summary>
    public int EndOfSequence { get; }

    /// <summary>Identificatore del token sconosciuto.</summary>
    public int Unknown { get; }

    /// <summary>
    /// Carica il tokenizer dal file del modello. Il file dichiara vocabolario e fusioni: vengono
    /// estratti e passati alla libreria, invece di duplicarli in un formato proprio.
    /// </summary>
    public static GemmaBpeTokenizer Load(string tokenizerJsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerJsonPath);

        using FileStream file = File.OpenRead(tokenizerJsonPath);
        using JsonDocument document = JsonDocument.Parse(file);

        JsonElement model = document.RootElement.GetProperty("model");

        using MemoryStream vocabStream = new();
        using (Utf8JsonWriter writer = new(vocabStream))
        {
            model.GetProperty("vocab").WriteTo(writer);
        }

        vocabStream.Position = 0;

        using MemoryStream mergesStream = new();

        using (StreamWriter writer = new(mergesStream, leaveOpen: true))
        {
            HashSet<string> written = new(StringComparer.Ordinal);

            foreach (JsonElement merge in model.GetProperty("merges").EnumerateArray())
            {
                // Il formato recente elenca le fusioni come coppie; quello vecchio come stringhe
                // gia' separate da uno spazio. Entrambi vanno letti, perche' il file lo scrive il
                // fornitore del modello e non noi.
                string left;
                string right;

                if (merge.ValueKind is JsonValueKind.Array)
                {
                    left = merge[0].GetString()!;
                    right = merge[1].GetString()!;
                }
                else
                {
                    string line = merge.GetString()!;
                    int separator = line.IndexOf(' ', StringComparison.Ordinal);

                    if (separator < 0)
                    {
                        continue;
                    }

                    left = line[..separator];
                    right = line[(separator + 1)..];
                }

                // Il formato a righe non puo' rappresentare una fusione che contiene spazi o
                // andate a capo, e non serve: la pre-tokenizzazione divide il testo sugli spazi,
                // quindi una parola non contiene mai quei caratteri e quelle fusioni non
                // potrebbero comunque applicarsi. Il carattere Metaspace non e' uno spazio e
                // resta.
                if (ContainsWhitespace(left) || ContainsWhitespace(right))
                {
                    continue;
                }

                // Una fusione ripetuta e' un errore per la libreria: la prima vale, le successive
                // non aggiungono nulla perche' il rango e' gia' fissato.
                if (written.Add(left + " " + right))
                {
                    writer.WriteLine(left + " " + right);
                }
            }
        }

        mergesStream.Position = 0;

        Dictionary<string, int> special = new(StringComparer.Ordinal);

        foreach (JsonElement added in document.RootElement.GetProperty("added_tokens").EnumerateArray())
        {
            if (added.TryGetProperty("special", out JsonElement flag) && flag.GetBoolean())
            {
                special[added.GetProperty("content").GetString()!] = added.GetProperty("id").GetInt32();
            }
        }

        string unknownToken = model.TryGetProperty("unk_token", out JsonElement unk)
            ? unk.GetString() ?? "<unk>"
            : "<unk>";

        // La pre-tokenizzazione la facciamo noi, parola per parola, per conservare gli span: alla
        // libreria si chiede il solo algoritmo BPE.
        BpeTokenizer bpe = BpeTokenizer.Create(
            vocabStream,
            mergesStream,
            preTokenizer: null,
            normalizer: null,
            specialTokens: special,
            unknownToken: unknownToken);

        return new GemmaBpeTokenizer(
            bpe,
            Lookup(special, "<bos>"),
            Lookup(special, "<eos>"),
            Lookup(special, unknownToken));
    }

    /// <summary>
    /// Codifica il testo conservando, per ogni token, la porzione di testo originale che copre.
    /// I token speciali di inizio e fine non compaiono: li aggiunge chi costruisce l'ingresso del
    /// modello, e non corrispondono ad alcun carattere.
    /// </summary>
    public IReadOnlyList<EncodedSpan> Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<EncodedSpan> spans = [];
        int index = 0;

        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            int start = index;

            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            EncodeWord(text.AsSpan(start, index - start).ToString(), start, spans);
        }

        return spans;
    }

    /// <summary>
    /// Codifica una parola con il prefisso Metaspace, come fa il pre-tokenizzatore del modello, e
    /// riporta gli span sul testo originale togliendo il prefisso.
    /// </summary>
    private void EncodeWord(string word, int offset, List<EncodedSpan> spans)
    {
        string prefixed = Metaspace + word;

        IReadOnlyList<EncodedToken> tokens = bpe.EncodeToTokens(
            prefixed,
            out _,
            considerPreTokenization: false,
            considerNormalization: false);

        foreach (EncodedToken token in tokens)
        {
            int tokenStart = token.Offset.Start.Value;
            int tokenLength = token.Offset.End.Value - tokenStart;

            // Il primo carattere della forma con prefisso non esiste nel testo originale: lo span
            // parte dopo di esso, e un token fatto del solo prefisso non copre nulla.
            int start = Math.Max(0, tokenStart - 1);
            int length = tokenStart == 0 ? Math.Max(0, tokenLength - 1) : tokenLength;

            if (length == 0)
            {
                continue;
            }

            spans.Add(new EncodedSpan(token.Id, offset + start, Math.Min(length, word.Length - start)));
        }
    }

    private static bool ContainsWhitespace(string value)
    {
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }

    private static int Lookup(IReadOnlyDictionary<string, int> special, string token) =>
        special.TryGetValue(token, out int id) ? id : -1;
}
