using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BetterTranslator.Bench;

internal sealed record ValidationResult(string Status, int TranslatedValues, int TotalValues, IReadOnlyList<string> Failures)
{
    internal bool Passed => Status == "pass";
}

internal static partial class Validation
{
    private static readonly string[] BrandAllowlist =
    [
        "bettertranslator", "betterguard", "argon2", "github", "discord", "windows", "linux", "macos", "android", "ios",
        "docker", "nuget", "unity", "blazor", "maui", "xamarin", "winforms", "wpf", "asp", "net", "api", "cli", "gui",
        "json", "xml", "html", "css", "pdf", "url", "uri", "sdk", "vpn", "dns", "http", "https", "tls", "ssl", "ip",
        "id", "ok", "pro", "free", "sql", "utf", "zip", "dll", "exe", "il", "jit", "aot", "msil", "linq", "vm", "os",
        "pc", "cpu", "gpu", "ram", "ssd", "usb", "qr", "sms", "faq", "eu", "en", "cs", "de", "fr", "es", "it", "pl",
        "sk", "ru", "ua", "zh", "ja", "ko", "email", "e", "mail", "web", "server", "client", "token", "premium",
    ];

    internal const string IdenticalAllowance =
        "a translated value may equal its source when every alphabetic word in it is either an allowlisted brand, "
        + "protocol or locale token or begins with a capital letter, which covers product names and title-case "
        + "proper nouns, or when the value is a command line carrying an executable name or a long flag";

    [GeneratedRegex(@"[A-Za-z]+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    [GeneratedRegex(@"\.exe\b|\s--[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex CommandLine();

    internal static ValidationResult Validate(string slicePath, string outputPath)
    {
        var failures = new List<string>();

        if (!File.Exists(outputPath))
        {
            return new ValidationResult("fail", 0, 0, ["output file was never written"]);
        }

        JsonObject source;
        JsonObject target;

        try
        {
            source = JsonNode.Parse(File.ReadAllBytes(slicePath))!.AsObject();
        }
        catch (JsonException exception)
        {
            return new ValidationResult("fail", 0, 0, [$"slice unreadable: {exception.Message}"]);
        }

        try
        {
            var parsed = JsonNode.Parse(File.ReadAllBytes(outputPath));

            if (parsed is not JsonObject asObject)
            {
                return new ValidationResult("fail", 0, source.Count, ["output is not a JSON object"]);
            }

            target = asObject;
        }
        catch (JsonException exception)
        {
            return new ValidationResult("fail", 0, source.Count, [$"output is not valid JSON: {exception.Message}"]);
        }

        var sourceKeys = source.Select(pair => pair.Key).ToList();
        var targetKeys = target.Select(pair => pair.Key).ToList();

        if (!sourceKeys.SequenceEqual(targetKeys, StringComparer.Ordinal))
        {
            var missing = sourceKeys.Except(targetKeys, StringComparer.Ordinal).Count();
            var added = targetKeys.Except(sourceKeys, StringComparer.Ordinal).Count();
            var reordered = missing == 0 && added == 0;

            failures.Add(reordered
                ? "key order differs from the slice"
                : $"key set differs from the slice: {missing} missing, {added} unexpected");
        }

        var translated = 0;
        var identical = new List<string>();
        var emptyValues = 0;
        var placeholderBreaks = new List<string>();
        var wrongShape = 0;

        foreach (var (key, value) in source)
        {
            var expected = value!.GetValue<string>();

            if (!target.TryGetPropertyValue(key, out var actualNode))
            {
                continue;
            }

            if (actualNode is not JsonValue actualValue || actualValue.GetValueKind() != JsonValueKind.String)
            {
                wrongShape++;
                continue;
            }

            var actual = actualValue.GetValue<string>();

            if (actual.Trim().Length == 0)
            {
                emptyValues++;
                continue;
            }

            if (!Placeholders.SameMultiset(expected, actual))
            {
                if (placeholderBreaks.Count < 3)
                {
                    placeholderBreaks.Add($"{Shorten(key)} expected [{Placeholders.Describe(expected)}] got [{Placeholders.Describe(actual)}]");
                }
                else if (placeholderBreaks.Count == 3)
                {
                    placeholderBreaks.Add("...");
                }
            }

            if (string.Equals(expected, actual, StringComparison.Ordinal))
            {
                if (!BrandOnly(expected))
                {
                    identical.Add(Shorten(key));
                }
            }
            else
            {
                translated++;
            }
        }

        if (wrongShape > 0)
        {
            failures.Add($"{wrongShape} values are not strings");
        }

        if (emptyValues > 0)
        {
            failures.Add($"{emptyValues} values are empty");
        }

        if (placeholderBreaks.Count > 0)
        {
            failures.Add($"placeholder multiset differs in {placeholderBreaks.Count} values: {string.Join("; ", placeholderBreaks.Take(3))}");
        }

        if (identical.Count > 0)
        {
            failures.Add($"{identical.Count} values equal their source outside the brand allowlist: {string.Join("; ", identical.Take(3))}");
        }

        return new ValidationResult(failures.Count == 0 ? "pass" : "fail", translated, source.Count, failures);
    }

    private static bool BrandOnly(string value)
    {
        if (CommandLine().IsMatch(value))
        {
            return true;
        }

        var words = Words().Matches(value).Select(match => match.Value).ToList();

        return words.Count == 0
            || words.All(word => BrandAllowlist.Contains(word.ToLowerInvariant()) || char.IsAsciiLetterUpper(word[0]));
    }

    private static string Shorten(string key) => key.Length <= 42 ? key : key[..39] + "...";
}
