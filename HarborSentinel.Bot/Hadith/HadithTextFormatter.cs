using System.Text.RegularExpressions;

namespace HarborSentinel.Bot.Hadith;

public static class HadithTextFormatter
{
    private const int DefaultMaxSourceLength = 80;

    public static string BuildSourceLine(string? source, int maxLen = DefaultMaxSourceLength)
    {
        var shortSource = ShortenSource(source, maxLen);
        if (string.IsNullOrWhiteSpace(shortSource))
            return string.Empty;

        return "\n\nИсточник: " + shortSource;
    }

    public static string ShortenSource(string? source, int maxLen = DefaultMaxSourceLength)
    {
        if (string.IsNullOrWhiteSpace(source))
            return string.Empty;

        var s = source.Trim();

        // Normalize accidental prefixes.
        if (s.StartsWith("Источник:", StringComparison.OrdinalIgnoreCase))
            s = s["Источник:".Length..].Trim();

        // Make HadeethEnc sources compact.
        if (s.Contains("HadeethEnc", StringComparison.OrdinalIgnoreCase))
        {
            var id = TryExtractId(s);
            return string.IsNullOrWhiteSpace(id) ? "HadeethEnc" : $"HadeethEnc #{id}";
        }

        // Collapse whitespace.
        s = Regex.Replace(s, "\\s+", " ", RegexOptions.CultureInvariant).Trim();

        if (maxLen < 8)
            maxLen = 8;

        if (s.Length > maxLen)
            s = s[..(maxLen - 1)].TrimEnd() + "…";

        return s;
    }

    private static string? TryExtractId(string text)
    {
        var m = Regex.Match(text, "\\bid\\s*=\\s*(?<id>[A-Za-z0-9_\\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? m.Groups["id"].Value : null;
    }
}

