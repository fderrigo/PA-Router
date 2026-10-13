using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Routing.Demand;

/// <summary>
/// Le misure certe e le indicazioni che il pre-check legge (ADR-025). Separate anche nel registro: le prime sono
/// misure, le seconde deduzioni linguistiche, e l'audit le deve distinguere.
/// </summary>
/// <param name="InputBytes">Byte UTF-8 della richiesta.</param>
/// <param name="ContextBytes">Byte del contesto che parte con la richiesta.</param>
/// <param name="Attachments">Allegati o contenuti consegnati con la richiesta.</param>
/// <param name="CodeLines">Righe dentro blocchi di codice della richiesta.</param>
/// <param name="Words">Parole della richiesta (indicazione).</param>
/// <param name="Questions">Punti interrogativi (indicazione).</param>
public sealed record DemandFeatures(int InputBytes, int ContextBytes, int Attachments, int CodeLines, int Words, int Questions);

/// <summary>Esito del pre-check, con il livello imposto dalle misure quando ce n'e' uno.</summary>
/// <param name="Outcome">Banale, imposto dalle misure o da completare.</param>
/// <param name="ForcedTier">Livello minimo imposto dalle misure; 0 se nessuno.</param>
/// <param name="Features">I segnali letti.</param>
public sealed record PreCheckResult(PreCheckOutcome Outcome, int ForcedTier, DemandFeatures Features);

/// <summary>
/// Il profilo di domanda (ADR-025): pre-check deterministico e trasformazione versionata dai segnali al profilo.
///
/// **Non chiama modelli.** I segnali dell'analizzatore arrivano da fuori (ADR-026); qui si decide soltanto, in modo
/// riproducibile, che cosa ne segue. Soglie, elenco dei casi banali e regola di confidenza stanno in una risorsa
/// versionata (<c>demand-v3.json</c>), non nel codice: cambiarli cambia la versione registrata con ogni profilo.
///
/// **La brevita' non e' mai prova di semplicita'.** Non esiste un esito «semplice» dato da una soglia di lunghezza:
/// fuori dall'elenco minimo dei casi banali, o le misure impongono un minimo, o decide l'analizzatore. Senza
/// analizzatore — spento, non utilizzabile, testo con un segreto — la richiesta vale **difficile**: sotto-stimare e'
/// l'errore piu' grave, e a bassa confidenza le dimensioni salgono, mai scendono.
/// </summary>
public static class DemandProfiler
{
    private static readonly Lazy<Settings> Current = new(Load);

    /// <summary>Versione di soglie, elenco, prompt e trasformazione.</summary>
    public static string Version => Current.Value.Version;

    /// <summary>Probabilita' sotto la quale il verdetto dell'analizzatore vale come incerto.</summary>
    public static double LowConfidence => Current.Value.LowConfidence;

    /// <summary>Il pre-check: legge solo misure e forma, mai il significato.</summary>
    /// <param name="input">Testo scritto dall'operatore.</param>
    /// <param name="contextBytes">Byte del contesto che parte con la richiesta.</param>
    /// <param name="attachments">Numero di allegati o contenuti consegnati.</param>
    public static PreCheckResult PreCheck(string input, int contextBytes, int attachments)
    {
        ArgumentNullException.ThrowIfNull(input);

        Settings settings = Current.Value;
        string[] words = Words(input);
        DemandFeatures features = new(
            Encoding.UTF8.GetByteCount(input),
            contextBytes,
            attachments,
            CodeLines(input),
            words.Length,
            input.Count(character => character == '?'));

        int forced =
            features.ContextBytes >= settings.Tier3.ContextBytes || features.Attachments >= settings.Tier3.Attachments
                || features.CodeLines >= settings.Tier3.CodeLines ? 3
            : features.ContextBytes >= settings.Tier2.ContextBytes || features.Attachments >= settings.Tier2.Attachments
                || features.CodeLines >= settings.Tier2.CodeLines ? 2
            : 0;

        if (forced > 0)
        {
            return new PreCheckResult(PreCheckOutcome.ForcedMinimumTier, forced, features);
        }

        return IsTrivial(words, settings)
            ? new PreCheckResult(PreCheckOutcome.Trivial, 0, features)
            : new PreCheckResult(PreCheckOutcome.Unknown, 0, features);
    }

    /// <summary>
    /// Dai segnali al profilo, in modo deterministico. <paramref name="signals"/> e' null quando l'analizzatore non ha
    /// risposto o non e' stato interpellato.
    /// </summary>
    public static DemandProfile Transform(PreCheckResult check, AnalyzerSignals? signals)
    {
        ArgumentNullException.ThrowIfNull(check);

        Settings settings = Current.Value;

        if (check.Outcome is PreCheckOutcome.Trivial && signals is null)
        {
            return new DemandProfile(settings.Version, check.Outcome, null, 1, 1, 1, 1, "trivial", false, null);
        }

        if (signals is null)
        {
            // Nessun giudizio sul contenuto. Fino a demand-v2 si saliva al livello 3, cioe' al cloud; da demand-v3, per
            // decisione dell'operatore, nel dubbio si resta al minimo che le misure impongono: la privacy la decidono i
            // suoi controlli, non questo livello, e il servizio piu' capace ammesso resta la rete sotto (BelowDemand).
            int floor = Math.Max(settings.TierWithoutSignals, check.ForcedTier);
            return new DemandProfile(settings.Version, check.Outcome, null, floor, floor, 1, check.ForcedTier > 0 ? check.ForcedTier : 1,
                "no-signals", false, null);
        }

        int reasoning = Level(signals.Reasoning);
        int tier = Math.Max(reasoning, Math.Max(Level(signals.Knowledge), Level(signals.Synthesis)));
        int coding = signals.Task is TaskType.Coding ? tier : 1;
        int context = Math.Max(Level(signals.Context), check.ForcedTier);
        tier = Math.Max(tier, check.ForcedTier);

        // Da demand-v3 una confidenza bassa non alza il livello (nel dubbio locale); il valore si registra comunque.
        bool raised = settings.RaiseOnLowConfidence && signals.Confidence is double confidence && confidence < settings.LowConfidence;

        if (raised)
        {
            tier = Math.Min(3, tier + 1);
            reasoning = Math.Min(3, reasoning + 1);
            coding = signals.Task is TaskType.Coding ? Math.Min(3, coding + 1) : coding;
        }

        return new DemandProfile(settings.Version, check.Outcome, signals.Task, tier, reasoning, coding, context,
            check.Outcome is PreCheckOutcome.ForcedMinimumTier ? "forced" : "analyzer", raised, signals);
    }

    /// <summary>Il profilo come coppie <c>chiave=valore</c>, senza contenuti, per gli eventi del turno.</summary>
    public static string Describe(DemandProfile profile, PreCheckResult check, string? bandToday, string? unavailable)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(check);

        StringBuilder text = new();
        text.Append("version=").Append(profile.Version)
            .Append(";precheck=").Append(profile.PreCheck.ToWire())
            .Append(";source=").Append(profile.Source)
            .Append(";tier=").Append(profile.BaseTier.ToString(CultureInfo.InvariantCulture))
            .Append(";reasoning=").Append(profile.Reasoning.ToString(CultureInfo.InvariantCulture))
            .Append(";coding=").Append(profile.Coding.ToString(CultureInfo.InvariantCulture))
            .Append(";context=").Append(profile.Context.ToString(CultureInfo.InvariantCulture))
            .Append(";hard=bytes:").Append(check.Features.InputBytes.ToString(CultureInfo.InvariantCulture))
            .Append('+').Append(check.Features.ContextBytes.ToString(CultureInfo.InvariantCulture))
            .Append(",attachments:").Append(check.Features.Attachments.ToString(CultureInfo.InvariantCulture))
            .Append(",code:").Append(check.Features.CodeLines.ToString(CultureInfo.InvariantCulture))
            .Append(";heuristic=words:").Append(check.Features.Words.ToString(CultureInfo.InvariantCulture))
            .Append(",questions:").Append(check.Features.Questions.ToString(CultureInfo.InvariantCulture));

        if (profile.Task is TaskType task) { text.Append(";task=").Append(task.ToWire()); }
        if (profile.Raised) { text.Append(";raised=1"); }

        if (profile.Signals is AnalyzerSignals signals)
        {
            text.Append(";signals=").Append(signals.Reasoning).Append(signals.Knowledge).Append(signals.Synthesis)
                .Append(signals.Context).Append(signals.Sensitive);

            if (signals.Confidence is double confidence)
            {
                text.Append(";confidence=").Append(confidence.ToString("0.00", CultureInfo.InvariantCulture));
            }
        }

        if (bandToday is not null) { text.Append(";bandToday=").Append(bandToday); }
        if (unavailable is not null) { text.Append(";unavailable=").Append(unavailable); }

        return text.ToString();
    }

    private static int Level(int value) => Math.Clamp(value, 1, 3);

    private static bool IsTrivial(string[] words, Settings settings)
    {
        if (words.Length == 0 || words.Length > settings.MaxWords)
        {
            return false;
        }

        string joined = string.Join(' ', words);

        return settings.Exact.Contains(joined) || settings.TranslatePrefixes.Contains(words[0]);
    }

    /// <summary>Parole in minuscolo, senza accenti e punteggiatura.</summary>
    private static string[] Words(string input)
    {
        string decomposed = input.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        StringBuilder builder = new(decomposed.Length);

        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark) { continue; }
            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static int CodeLines(string input)
    {
        int lines = 0;
        bool inside = false;

        foreach (string line in input.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { inside = !inside; continue; }
            if (inside) { lines++; }
        }

        return lines;
    }

    private sealed record Thresholds(int ContextBytes, int Attachments, int CodeLines);

    private sealed record Settings(
        string Version,
        int MaxWords,
        HashSet<string> Exact,
        HashSet<string> TranslatePrefixes,
        Thresholds Tier3,
        Thresholds Tier2,
        double LowConfidence,
        bool RaiseOnLowConfidence,
        int TierWithoutSignals);

    private static Settings Load()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PA.Router.Routing.Demand.demand-v3.json")
            ?? throw new InvalidOperationException("The demand transformation resource is missing.");
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        JsonElement trivial = root.GetProperty("trivial");
        JsonElement forced = root.GetProperty("forced");

        static Thresholds Read(JsonElement element) => new(
            element.GetProperty("contextBytes").GetInt32(),
            element.GetProperty("attachments").GetInt32(),
            element.GetProperty("codeLines").GetInt32());

        return new Settings(
            root.GetProperty("version").GetString()!,
            trivial.GetProperty("maxWords").GetInt32(),
            new HashSet<string>(trivial.GetProperty("exact").EnumerateArray().Select(item => item.GetString()!), StringComparer.Ordinal),
            new HashSet<string>(trivial.GetProperty("translatePrefixes").EnumerateArray().Select(item => item.GetString()!), StringComparer.Ordinal),
            Read(forced.GetProperty("tier3")),
            Read(forced.GetProperty("tier2")),
            root.GetProperty("lowConfidence").GetDouble(),
            root.GetProperty("raiseOnLowConfidence").GetBoolean(),
            root.GetProperty("tierWithoutSignals").GetInt32());
    }
}
