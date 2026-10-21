using System.Text.RegularExpressions;

namespace rNotice.Core.Rules;

internal static class TextMatch
{
    /// <summary>
    /// The pattern that matched, or null. Plain text is a case-insensitive substring; text
    /// wrapped in slashes is a case-insensitive regex with a short timeout, since the patterns
    /// come from a config file.
    /// </summary>
    public static string? FirstSubjectMatch(string subject, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;

            var isRegex = pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/';
            var hit = isRegex
                ? Regex.IsMatch(subject, pattern[1..^1], RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))
                : subject.Contains(pattern, StringComparison.OrdinalIgnoreCase);

            if (hit) return pattern;
        }

        return null;
    }

    /// <summary>Exact address, or <c>@domain</c> matching the address's domain. Case-insensitive.</summary>
    public static bool SenderMatches(string address, IEnumerable<string> entries) =>
        entries.Any(e => !string.IsNullOrWhiteSpace(e) &&
            (e.StartsWith('@')
                ? address.EndsWith(e, StringComparison.OrdinalIgnoreCase)
                : address.Equals(e, StringComparison.OrdinalIgnoreCase)));

    public static string? Truncate(string? text, int length)
    {
        if (text is null) return null;
        text = text.Trim();
        return text.Length <= length ? text : text[..length].TrimEnd() + "…";
    }
}
