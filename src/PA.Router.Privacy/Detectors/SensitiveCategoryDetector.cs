using System.Text.RegularExpressions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Privacy.Detectors;

/// <summary>
/// Categorie particolari dell'articolo 9 GDPR: salute, origine, convinzioni religiose o
/// filosofiche, opinioni politiche, appartenenza sindacale, vita e orientamento sessuale, dati
/// biometrici e genetici.
///
/// **Prima implementazione deterministica a termini, sostituibile.** Un elenco di parole non
/// capisce il contesto: «diabete» in una nota clinica e in un articolo di giornale pesano lo
/// stesso. La scelta e' voluta: qui un falso positivo alza la classe e limita la destinazione, un
/// falso negativo la lascia dov'era. Sbagliare per eccesso restringe, sbagliare per difetto
/// espone, e fra i due si sceglie il primo.
///
/// Un modello capirebbe il contesto, ma la misura dell'11 settembre 2026 su `rizzo-pii-0.3B` dice
/// 2,5 KB/s su CPU: per un payload di 4 KB sarebbero 1,6 secondi, gia' oltre il budget inline
/// dichiarato. Un rilevamento di categoria che costa piu' del turno non e' un miglioramento, ed e'
/// il motivo per cui la capacita' `sensitive-category-detection` nasce deterministica e il modello
/// resta sul percorso della memoria (03-specs/PRIVACY.md).
/// </summary>
public sealed partial class SensitiveCategoryDetector : RegexDetector
{
    /// <inheritdoc />
    public override string Name => "sensitive_category";

    /// <inheritdoc />
    public override string Version => "sensitive-category-v2";

    /// <inheritdoc />
    public override FindingType Type => FindingType.SensitiveCategory;

    /// <inheritdoc />
    public override DataClass ClassContribution => DataClass.Sensitive;

    /// <inheritdoc />
    protected override double Confidence => 0.6;

    /// <inheritdoc />
    protected override Regex Pattern => CategoryPattern();

    /// <summary>
    /// Termini in italiano e in inglese, con il confine di parola: «cardiopatia» non deve
    /// accendersi su «cardio» dentro un'altra parola, e un elenco che si accende su qualunque
    /// sottostringa produrrebbe piu' rumore che protezione.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:"
        + @"diagnos[ie]|diagnosis|cartella\s+clinica|referto|anamnesi|ricoverat[oaie]|patolog(?:ia|ie)|"
        + @"malatt(?:ia|ie)|terapia\s+farmacologica|ricovero|disabilit[àa]|invalidit[àa]|"
        + @"gravidanza|pregnancy|diabete|diabetes|tumore|oncolog\w*|hiv|sieroposit\w*|"
        + @"salute\s+mentale|mental\s+health|psichiatr\w*|dipendenz[ae]\s+da\s+sostanze|"
        // v2 (24 settembre 2026): «mio fratello Marco Bianchi e' in cura per depressione» restava personale, perche'
        // l'elenco non conosceva le malattie piu' comuni. «Cura» e «terapia» da sole no: «prendersi cura» non e' salute.
        + @"depression[ei]|depression|antidepressiv\w*|antidepressant\w*|ansiolitic\w*|psicoterap\w*|psicolog[oa]|"
        + @"disturb[oi]\s+(?:bipolar\w*|d'ansia|dell'umore|alimentar\w*|ossessiv\w*|di\s+personalit[àa])|attacch?i\s+di\s+panico|"
        + @"in\s+cura\s+(?:per|da)|in\s+terapia|schizofreni\w*|autism\w*|alzheimer|parkinson|demenza|epilessi\w*|"
        + @"cancro|chemioterapi\w*|radioterapi\w*|sclerosi|ictus|infarto|anoressi\w*|bulimi\w*|dialisi|epatite|cirrosi|"
        + @"anxiety\s+disorder|bipolar\s+disorder|eating\s+disorder|panic\s+attacks?|schizophrenia|dementia|epilepsy|"
        + @"cancer|chemotherapy|antidepressants?|"
        + @"origine\s+etnica|ethnic\s+origin|origine\s+razziale|"
        + @"convinzion[ei]\s+religios\w*|religious\s+belief\w*|confessione\s+religiosa|"
        + @"opinion[ei]\s+politic\w*|political\s+opinion\w*|iscrizione\s+a\s+un\s+partito|"
        + @"appartenenza\s+sindacale|trade\s+union\s+membership|iscritt\w*\s+al\s+sindacato|"
        + @"orientamento\s+sessuale|sexual\s+orientation|vita\s+sessuale|"
        + @"dat[oi]\s+biometric\w*|biometric\s+data|impronta\s+digitale|riconoscimento\s+facciale|"
        + @"dat[oi]\s+genetic\w*|genetic\s+data|test\s+del\s+dna"
        + @")\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        50)]
    private static partial Regex CategoryPattern();
}
