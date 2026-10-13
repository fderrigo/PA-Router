using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PA.Router.Cli.Localization;

/// <summary>Testi dell'interfaccia; mai dati dell'utente, codici o valori wire.</summary>
internal static class Ui
{
    private static readonly AsyncLocal<string?> Selected = new();
    private static readonly Lazy<IReadOnlyDictionary<string, Catalog>> Catalogs = new(Load);
    internal static IReadOnlyList<string> Languages { get; } = ["en", "it", "es", "pt", "ru", "fr"];
    internal static string Language => Selected.Value ?? "en";
    internal static void SetLanguage(string? language)
    {
        Selected.Value = Languages.Contains(language ?? "") ? language : "en";
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Language);
    }
    internal static string NativeName(string language) => Catalogs.Value[language].NativeName;
    internal static bool Contains(string key) => Catalogs.Value["en"].Messages.ContainsKey(key);
    internal static string Text(string key) => Catalogs.Value[Language].Messages.TryGetValue(key, out string? text) ? text
        : Catalogs.Value["en"].Messages.GetValueOrDefault(key, key);
    // Only router-owned diagnostics enter here. Never translate user text or provider responses.
    internal static string Diagnostic(string message)
    {
        string key = "diagnostic:" + message;
        if (Contains(key)) { return Text(key); }
        foreach ((string template, Regex pattern, int count) in DiagnosticTemplates.Value)
        {
            Match match = pattern.Match(message);
            if (match.Success)
            {
                return Format(template, Enumerable.Range(0, count).Select(index => (object?)match.Groups["arg" + index].Value).ToArray());
            }
        }
        return Text(message);
    }
    private static readonly Lazy<IReadOnlyList<(string Key, Regex Pattern, int Count)>> DiagnosticTemplates = new(() =>
        [.. Catalogs.Value["en"].Messages.Keys.Where(key => key.StartsWith("diagnostic:", StringComparison.Ordinal) && key.Contains("{0}", StringComparison.Ordinal))
            .Select(key =>
            {
                string source = key["diagnostic:".Length..];
                int count = Regex.Matches(source, @"\{\d+\}").Count;
                string pattern = Regex.Replace(Regex.Escape(source), @"\\\{(\d+)}", match => "(?<arg" + match.Groups[1].Value + ">.*?)");
                return (key, new Regex("\\A" + pattern + "\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)), count);
            })]);
    internal static string Interpolate(FormattableString value) => Format(value.Format, value.GetArguments());
    internal static string Format(string key, params object?[] arguments)
    {
        try { return string.Format(CultureInfo.InvariantCulture, Text(key), arguments); }
        catch (FormatException) { return string.Format(CultureInfo.InvariantCulture, Catalogs.Value["en"].Messages.GetValueOrDefault(key, key), arguments); }
    }
    internal static IDisposable UseLanguage(string language)
    {
        string previous = Language;
        SetLanguage(language);
        return new RestoreLanguage(previous);
    }
    private sealed class RestoreLanguage(string previous) : IDisposable
    {
        public void Dispose() => SetLanguage(previous);
    }
    internal sealed record Catalog(string Language, string NativeName, Dictionary<string, string> Messages);
    private static IReadOnlyDictionary<string, Catalog> Load()
    {
        Dictionary<string, Catalog> result = new(StringComparer.Ordinal);
        foreach (string language in Languages)
        {
            result[language] = ReadCatalog(language, Path.Combine(AppContext.BaseDirectory, "Localization"));
        }
        return result;
    }
    internal static Catalog ReadCatalog(string language, string directory)
    {
        using Stream stream = typeof(Ui).Assembly.GetManifestResourceStream("PA.Router.Cli.Localization." + language + ".json")!;
        Catalog bundled = JsonSerializer.Deserialize<Catalog>(stream, JsonOptions)!;
        try
        {
            string file = Path.Combine(directory, language + ".json");
            Catalog? external = File.Exists(file) ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(file), JsonOptions) : null;
            if (external?.Language != language || external.Messages is null) { return bundled; }
            foreach ((string key, string value) in external.Messages)
            {
                if (string.IsNullOrWhiteSpace(value) || !bundled.Messages.TryGetValue(key, out string? original)) { continue; }
                string[] Arguments(string text) => [.. Regex.Matches(text, @"\{\d+(?:[^{}]*)\}").Select(match => match.Value).Order(StringComparer.Ordinal)];
                if (Arguments(value).SequenceEqual(Arguments(original), StringComparer.Ordinal)) { bundled.Messages[key] = value; }
            }
            return bundled;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return bundled; }
    }
    private static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNameCaseInsensitive = true };
}
