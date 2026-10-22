using System.Text.RegularExpressions;

namespace rNotice.Core.Rules;

/// <summary>
/// Sender lists — <see cref="RulesOptions.IgnoreSenders"/> and <see cref="RulesOptions.VipSenders"/>.
/// An entry is an exact address, or <c>@domain.com</c> for a whole domain.
/// </summary>
public static class Senders
{
    /// <summary>
    /// The address out of an item's From, which is stored as <c>Name &lt;address&gt;</c> or a bare
    /// address. Null if there isn't one.
    /// </summary>
    public static string? AddressOf(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return null;
        var bracketed = Regex.Match(from, "<([^<>@\\s]+@[^<>\\s]+)>");
        var address = bracketed.Success ? bracketed.Groups[1].Value : from.Trim();
        return address.Contains('@') ? address.ToLowerInvariant() : null;
    }

    /// <summary>
    /// How a sender reads at the front of an item's line. A colleague is known by name, so the
    /// address goes to the end of the line (<paramref name="TrailingAddress"/>); anyone outside
    /// keeps the address beside the name, since the domain is often what tells you who they are.
    /// </summary>
    public static (string? Label, string? TrailingAddress) Describe(string? from, string? myDomain)
    {
        if (string.IsNullOrWhiteSpace(from)) return (null, null);

        var address = AddressOf(from);
        var lt = from.IndexOf('<');
        var name = lt > 0 ? from[..lt].Trim() : address is null ? from.Trim() : null;
        if (string.IsNullOrEmpty(name)) name = null;

        if (address is null) return (name, null);                       // a meeting organiser: name only
        if (name is null) return (address, null);                       // bare address
        var colleague = myDomain is not null && address.EndsWith(myDomain, StringComparison.OrdinalIgnoreCase);
        return colleague ? (name, address) : ($"{name} <{address}>", null);
    }

    /// <summary><c>@domain.com</c> for an address.</summary>
    public static string DomainEntry(string address) => "@" + address[(address.IndexOf('@') + 1)..].ToLowerInvariant();

    public static bool Matches(string address, IEnumerable<string> entries) => TextMatch.SenderMatches(address, entries);

    /// <summary>
    /// Normalises a typed entry: trimmed, lower case, and either an address or <c>@domain</c>.
    /// Null if it's neither, so the editor can refuse it rather than store something that
    /// never matches.
    /// </summary>
    public static string? Normalise(string? entry)
    {
        var e = entry?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(e)) return null;
        if (e.StartsWith('@')) return e.Length > 3 && e.IndexOf('.', 1) > 1 && !e[1..].Contains('@') ? e : null;
        var at = e.IndexOf('@');
        return at > 0 && at == e.LastIndexOf('@') && e.IndexOf('.', at) > at + 1 && !e.Contains(' ') ? e : null;
    }

    /// <summary>The list with <paramref name="entry"/> added, once, case-insensitively.</summary>
    public static IReadOnlyList<string> With(IReadOnlyList<string> list, string entry) =>
        list.Any(e => e.Equals(entry, StringComparison.OrdinalIgnoreCase)) ? list : [.. list, entry];
}
