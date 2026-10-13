using System.Text.RegularExpressions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Privacy.Detectors;

/// <summary>
/// Base dei detector a espressione regolare. Il timeout e' impostato sulla regex stessa: e' il
/// meccanismo che il runtime offre per fermare un backtracking patologico, e scade anche se il
/// pattern e' scritto male o l'input e' costruito apposta.
/// </summary>
public abstract class RegexDetector : IPrivacyDetector
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Version { get; }

    /// <inheritdoc />
    public abstract FindingType Type { get; }

    /// <inheritdoc />
    public abstract DataClass ClassContribution { get; }

    /// <summary>Confidenza dichiarata per un match di questo detector.</summary>
    protected abstract double Confidence { get; }

    /// <summary>Espressione applicata alla superficie.</summary>
    protected abstract Regex Pattern { get; }

    /// <inheritdoc />
    public void Scan(string text, ByteOffsetMap offsets, List<PrivacyFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(offsets);
        ArgumentNullException.ThrowIfNull(findings);

        foreach (Match match in Pattern.Matches(text))
        {
            if (!Accept(match))
            {
                continue;
            }

            findings.Add(new PrivacyFinding(
                Type,
                offsets.ByteOffset(match.Index),
                offsets.ByteLength(match.Index, match.Length),
                Confidence,
                Version));
        }
    }

    /// <summary>
    /// Filtro aggiuntivo su un match, per i formati che hanno una verifica strutturale oltre alla
    /// forma. Il default accetta ogni match.
    /// </summary>
    protected virtual bool Accept(Match match) => true;
}
