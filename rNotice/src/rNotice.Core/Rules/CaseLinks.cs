using System.Text.RegularExpressions;

namespace rNotice.Core.Rules;

/// <summary>
/// Salesforce cases in mail: the case number from the subject, and the record's internal
/// Lightning link from a Salesforce link inside the message. No Salesforce API and no sign-in —
/// the record ID is in the link, and the Lightning address is built from it.
/// </summary>
public static class CaseLinks
{
    // "Case 00037339", "Case Assignment: 00038001", "Case #00038001".
    private static readonly Regex CaseNumber =
        new(@"\bCase\b\D{0,20}?(?<n>\d{8})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // A Case record ID (key prefix 500, 15 or 18 characters) inside a Salesforce link —
    // my.salesforce.com/500…, …/lightning/r/Case/500…/view, or the customer portal,
    // bravurasecurity.my.site.com/portal/500… (an Experience Cloud site: the same record ID).
    // Only inside a Salesforce address, so an unrelated 500-something elsewhere in a message
    // is never taken for one.
    private static readonly Regex RecordInLink =
        new(@"https?://[\w.-]*(?:salesforce\.com|force\.com|\.my\.site\.com)/(?:[\w/]*?/)?(?<id>500[0-9A-Za-z]{12}(?:[0-9A-Za-z]{3})?)(?![0-9A-Za-z])",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    // A bare case number — "look at this asap please 00039100". Bravura case numbers are eight
    // digits with leading zeros, so only those are taken without the word "Case" in front.
    private static readonly Regex BareCaseNumber =
        new(@"(?<![\d.,/:-])(?<n>000\d{5})(?![\d.,/:-]?\d)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>A case number with "Case" in front, or else a bare eight-digit 000… number, or null.</summary>
    public static string? FindAnyCaseNumber(string? text) =>
        FindCaseNumber(text) ?? (text is not null && BareCaseNumber.Match(text) is { Success: true } m ? m.Groups["n"].Value : null);

    /// <summary>The case number in a subject or line, or null.</summary>
    public static string? FindCaseNumber(string? text) =>
        text is not null && CaseNumber.Match(text) is { Success: true } m ? m.Groups["n"].Value : null;

    /// <summary>The first Case record ID in a Salesforce link in the text, as 18 characters, or null.</summary>
    public static string? FindRecordId(string? text) =>
        text is not null && RecordInLink.Match(text) is { Success: true } m ? To18(m.Groups["id"].Value) : null;

    /// <summary>The internal Lightning address for a Case record.</summary>
    public static string LightningUrl(string baseUrl, string recordId) =>
        $"{baseUrl.TrimEnd('/')}/lightning/r/Case/{To18(recordId)}/view";

    /// <summary>
    /// Salesforce's 15-to-18 character ID conversion. Each 5-character chunk gives one suffix
    /// character: a bit set for every uppercase letter in the chunk, read as an index into
    /// A–Z then 0–5. 500N300001Mapmw → 500N300001MapmwIAB.
    /// </summary>
    public static string To18(string id)
    {
        if (id.Length == 18) return id;
        if (id.Length != 15) throw new ArgumentException("A Salesforce ID is 15 or 18 characters.", nameof(id));

        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ012345";
        var suffix = new char[3];
        for (var chunk = 0; chunk < 3; chunk++)
        {
            var bits = 0;
            for (var i = 0; i < 5; i++)
                if (char.IsAsciiLetterUpper(id[chunk * 5 + i])) bits |= 1 << i;
            suffix[chunk] = alphabet[bits];
        }
        return id + new string(suffix);
    }
}
