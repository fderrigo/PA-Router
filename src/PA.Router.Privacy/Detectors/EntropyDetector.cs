using PA.Router.Abstractions.Contracts;

namespace PA.Router.Privacy.Detectors;

/// <summary>
/// Euristica di entropia: segnala sequenze che assomigliano a materiale casuale.
///
/// Non alza la classe. La specifica la vuole «solo come finding sospetto» e con ragione: un hash
/// di commit, un identificatore o un blob base64 legittimo hanno la stessa entropia di una
/// chiave. Alzare la classe qui renderebbe segreto mezzo repository e spingerebbe l'operatore a
/// disattivare i controlli. Il finding resta, e chi legge l'audit sa che c'era qualcosa da
/// guardare.
/// </summary>
public sealed class EntropyDetector : IPrivacyDetector
{
    /// <summary>Lunghezza minima di una sequenza per essere valutata.</summary>
    public const int MinimumRunLength = 32;

    /// <summary>Entropia minima, in bit per carattere, perche' la sequenza sia sospetta.</summary>
    public const double MinimumEntropy = 4.0;

    /// <inheritdoc />
    public string Name => "entropy";

    /// <inheritdoc />
    public string Version => "entropy-v1";

    /// <inheritdoc />
    public FindingType Type => FindingType.HighEntropy;

    /// <inheritdoc />
    public DataClass ClassContribution => DataClass.Public;

    /// <inheritdoc />
    public void Scan(string text, ByteOffsetMap offsets, List<PrivacyFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(offsets);
        ArgumentNullException.ThrowIfNull(findings);

        int start = -1;

        for (int index = 0; index <= text.Length; index++)
        {
            bool isTokenCharacter = index < text.Length && IsTokenCharacter(text[index]);

            if (isTokenCharacter)
            {
                if (start < 0)
                {
                    start = index;
                }

                continue;
            }

            if (start >= 0)
            {
                Evaluate(text, offsets, findings, start, index - start);
                start = -1;
            }
        }
    }

    /// <summary>Entropia di Shannon in bit per carattere.</summary>
    public static double ShannonEntropy(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        Dictionary<char, int> counts = [];

        foreach (char character in value)
        {
            counts[character] = counts.TryGetValue(character, out int count) ? count + 1 : 1;
        }

        double entropy = 0;

        foreach (int count in counts.Values)
        {
            double probability = (double)count / value.Length;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }

    private void Evaluate(string text, ByteOffsetMap offsets, List<PrivacyFinding> findings, int start, int length)
    {
        if (length < MinimumRunLength)
        {
            return;
        }

        if (ShannonEntropy(text.AsSpan(start, length)) < MinimumEntropy)
        {
            return;
        }

        findings.Add(new PrivacyFinding(
            Type,
            offsets.ByteOffset(start),
            offsets.ByteLength(start, length),
            Confidence: 0.3,
            Version));
    }

    private static bool IsTokenCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '=' or '_' or '-';
}
