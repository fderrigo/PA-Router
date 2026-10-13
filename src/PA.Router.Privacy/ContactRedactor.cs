using System.Text;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Privacy;

/// <summary>Esito di una redazione.</summary>
/// <param name="Text">Testo con i contatti sostituiti.</param>
/// <param name="ReplacementCount">Numero di sostituzioni applicate.</param>
public readonly record struct RedactionResult(string Text, int ReplacementCount);

/// <summary>
/// Sostituzione dei contatti con segnaposto stabili nel singolo payload.
///
/// La mappa fra segnaposto e valore originale non viene conservata da nessuna parte e non esiste
/// alcun reinserimento automatico nella risposta: sarebbe una via per far rientrare il dato che
/// si e' appena tolto. La sostituzione non e' anonimizzazione e non abbassa la classe: chi legge
/// <c>[EMAIL_001]</c> sa che li' c'era un indirizzo (03-specs/PRIVACY.md).
/// </summary>
public static class ContactRedactor
{
    /// <summary>Tipi di finding che la redazione dei contatti sostituisce.</summary>
    public static IReadOnlySet<FindingType> RedactableTypes { get; } =
        new HashSet<FindingType> { FindingType.Email, FindingType.Phone };

    /// <summary>
    /// Sostituisce i contatti trovati. I finding sono in byte: la sostituzione lavora sui byte
    /// UTF-8 e ricompone il testo, cosi' gli offset restano quelli osservati dai detector.
    /// </summary>
    public static RedactionResult Redact(string text, IReadOnlyList<PrivacyFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(findings);

        List<PrivacyFinding> targets = [.. findings
            .Where(finding => RedactableTypes.Contains(finding.Type))
            .OrderBy(finding => finding.StartByte)];

        if (targets.Count == 0)
        {
            return new RedactionResult(text, 0);
        }

        byte[] source = Encoding.UTF8.GetBytes(text);
        Dictionary<FindingType, int> counters = [];
        Dictionary<string, string> assigned = new(StringComparer.Ordinal);

        using MemoryStream output = new(source.Length);
        int cursor = 0;
        int replacements = 0;

        foreach (PrivacyFinding finding in targets)
        {
            if (finding.StartByte < cursor)
            {
                // Match sovrapposti: il primo ha gia' coperto questa porzione.
                continue;
            }

            output.Write(source, cursor, finding.StartByte - cursor);

            string original = Encoding.UTF8.GetString(source, finding.StartByte, finding.ByteLength);
            string placeholder = Placeholder(finding.Type, original, counters, assigned);

            byte[] replacement = Encoding.UTF8.GetBytes(placeholder);
            output.Write(replacement, 0, replacement.Length);

            cursor = finding.StartByte + finding.ByteLength;
            replacements++;
        }

        output.Write(source, cursor, source.Length - cursor);

        return new RedactionResult(Encoding.UTF8.GetString(output.ToArray()), replacements);
    }

    /// <summary>
    /// Segnaposto stabile dentro il payload: lo stesso indirizzo ripetuto riceve lo stesso
    /// numero, cosi' il modello puo' ancora capire che si tratta della stessa persona senza
    /// vedere il dato.
    /// </summary>
    private static string Placeholder(
        FindingType type,
        string original,
        Dictionary<FindingType, int> counters,
        Dictionary<string, string> assigned)
    {
        string key = type.ToWire() + "|" + original;

        if (assigned.TryGetValue(key, out string? existing))
        {
            return existing;
        }

        int next = counters.TryGetValue(type, out int current) ? current + 1 : 1;
        counters[type] = next;

        string placeholder = "[" + Label(type) + "_" + next.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + "]";
        assigned[key] = placeholder;

        return placeholder;
    }

    private static string Label(FindingType type) => type switch
    {
        FindingType.Email => "EMAIL",
        FindingType.Phone => "PHONE",
        _ => throw new ArgumentOutOfRangeException(nameof(type), "This finding type is not redactable."),
    };
}
