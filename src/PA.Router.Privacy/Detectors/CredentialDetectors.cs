using System.Text.RegularExpressions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Privacy.Detectors;

/// <summary>
/// Chiavi note riconoscibili dal prefisso. Un match porta la classe a
/// <see cref="DataClass.Secret"/>: per la beta un segreto verso il cloud e' sempre deny, anche
/// dopo una trasformazione (03-specs/PRIVACY.md).
/// </summary>
public sealed partial class ApiKeyDetector : RegexDetector
{
    /// <inheritdoc />
    public override string Name => "api-key";

    /// <inheritdoc />
    public override string Version => "api-key-v1";

    /// <inheritdoc />
    public override FindingType Type => FindingType.ApiKey;

    /// <inheritdoc />
    public override DataClass ClassContribution => DataClass.Secret;

    /// <inheritdoc />
    protected override double Confidence => 0.95;

    /// <inheritdoc />
    protected override Regex Pattern => ApiKeyPattern();

    [GeneratedRegex(
        @"(?:sk-[A-Za-z0-9_\-]{16,}|ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|xox[abprs]-[A-Za-z0-9\-]{10,}|AIza[A-Za-z0-9_\-]{20,})",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        50)]
    private static partial Regex ApiKeyPattern();
}

/// <summary>
/// Blocchi di chiave privata. Basta l'intestazione: il corpo puo' essere spezzato su piu' righe
/// o troncato, ma la presenza dell'intestazione e' gia' sufficiente a bloccare.
/// </summary>
public sealed partial class PrivateKeyDetector : RegexDetector
{
    /// <inheritdoc />
    public override string Name => "private-key";

    /// <inheritdoc />
    public override string Version => "private-key-v1";

    /// <inheritdoc />
    public override FindingType Type => FindingType.PrivateKey;

    /// <inheritdoc />
    public override DataClass ClassContribution => DataClass.Secret;

    /// <inheritdoc />
    protected override double Confidence => 0.99;

    /// <inheritdoc />
    protected override Regex Pattern => PrivateKeyPattern();

    [GeneratedRegex(
        @"-{5}BEGIN(?: [A-Z0-9]{1,12}){0,4} PRIVATE KEY-{5}",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        50)]
    private static partial Regex PrivateKeyPattern();
}

/// <summary>
/// Password assegnata in linea, nella forma <c>password: valore</c>. E' il caso ricorrente di un
/// segreto incollato dentro un prompt o dentro un file di configurazione.
///
/// Dal 23 settembre 2026 anche detta a parole: «la mia password della posta e' Estate2025!», «my password is …». Prima
/// passava come testo qualunque: il segreto finiva nella cronologia e poteva uscire dalla rete locale. Perche' non basti
/// «la password e' troppo corta», il valore deve contenere una cifra o un simbolo che non sia punteggiatura.
/// </summary>
public sealed partial class AssignedPasswordDetector : RegexDetector
{
    /// <inheritdoc />
    public override string Name => "assigned-password";

    /// <inheritdoc />
    public override string Version => "assigned-password-v2";

    /// <inheritdoc />
    public override FindingType Type => FindingType.AssignedPassword;

    /// <inheritdoc />
    public override DataClass ClassContribution => DataClass.Secret;

    /// <inheritdoc />
    protected override double Confidence => 0.7;

    /// <inheritdoc />
    protected override Regex Pattern => AssignedPasswordPattern();

    [GeneratedRegex(
        @"(?i)(?:password|passwd|pwd|secret|token)[ \t]{0,8}[:=][ \t]{0,8}[^\s""']{6,128}"
        + @"|\b(?:password|passwd|pwd|passcode|pin)\b[^\n.:=]{0,40}?\b(?:è|e'|is|es|est|é|era|was)[ \t]+[""'«]?"
        + @"(?=[^\s""'»]*[0-9@#$%^&*_+=~|\\/<>{}\[\]])[^\s""'»]{6,128}",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        50)]
    private static partial Regex AssignedPasswordPattern();
}
