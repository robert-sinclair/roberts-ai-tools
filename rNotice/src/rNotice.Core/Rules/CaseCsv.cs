using System.Globalization;
using System.Text.RegularExpressions;

namespace rNotice.Core.Rules;

/// <summary>One case from an export: its number, 18-character record ID, and when it last changed.</summary>
public sealed record CaseRow(string CaseNumber, string RecordId, DateTimeOffset? LastModifiedUtc);

/// <summary>What an import read: the cases, and how many CSV-looking lines were rejected.</summary>
public sealed record CaseCsvResult(IReadOnlyList<CaseRow> Rows, int Rejected);

/// <summary>
/// Fills the case map from a Salesforce export made in another Claude chat with the read-only
/// Salesforce connector: <see cref="Prompt"/> asks for the CSV, <see cref="Parse"/> reads it
/// back from the clipboard.
/// </summary>
public static class CaseCsv
{
    /// <summary>The prompt the Dev view copies. The CSV it asks for is what <see cref="Parse"/> reads.</summary>
    public const string Prompt = """
        Using the Salesforce connector (read only), list every Case changed in the last 6 months.
        Change nothing in Salesforce.

        Query:
        SELECT CaseNumber, Id, LastModifiedDate FROM Case WHERE LastModifiedDate = LAST_N_DAYS:183 ORDER BY LastModifiedDate DESC, Id DESC

        If the results are paged or capped, keep going until there are no more rows: use the
        next-page token if the tool gives one, otherwise repeat the query adding
        AND LastModifiedDate <= <the oldest LastModifiedDate so far>, and drop rows you already
        have (same Id). Don't skip, sample or summarise rows.

        Output the result as CSV in code blocks, with nothing else inside the blocks:
        - Header line: CaseNumber,CaseId,LastModifiedDate
        - CaseNumber exactly as Salesforce has it, leading zeros kept (for example 00038033).
        - CaseId: the 18-character Id.
        - LastModifiedDate: ISO 8601 in UTC, for example 2026-10-30T15:04:00Z.
        - At most 1000 rows per code block. Start each block with the header line.

        After the last block, write one line with the total number of rows.
        """;

    private static readonly Regex Number = new(@"^\d{8}$", RegexOptions.CultureInvariant);
    private static readonly Regex RecordId = new(@"^500[0-9A-Za-z]{12}(?:[0-9A-Za-z]{3})?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads every <c>CaseNumber,CaseId,LastModifiedDate</c> row in the text, wherever it is:
    /// code fences, header lines and the chat's own words around them are ignored. A line with
    /// a comma that isn't a valid row counts as rejected. The date is optional. A case listed
    /// twice keeps its latest change.
    /// </summary>
    public static CaseCsvResult Parse(string text)
    {
        var rows = new Dictionary<string, CaseRow>();
        var rejected = 0;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal) || !line.Contains(',')) continue;

            var cells = line.Split(',').Select(c => c.Trim().Trim('"').Trim()).ToArray();
            if (cells[0].Equals("CaseNumber", StringComparison.OrdinalIgnoreCase)) continue;

            if (cells.Length < 2 || !Number.IsMatch(cells[0]) || !RecordId.IsMatch(cells[1]))
            {
                rejected++;
                continue;
            }

            DateTimeOffset? modified = cells.Length > 2 && DateTimeOffset.TryParse(cells[2], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

            var row = new CaseRow(cells[0], CaseLinks.To18(cells[1]), modified);
            if (!rows.TryGetValue(row.CaseNumber, out var known) || (row.LastModifiedUtc ?? DateTimeOffset.MinValue) > (known.LastModifiedUtc ?? DateTimeOffset.MinValue))
                rows[row.CaseNumber] = row;
        }

        return new CaseCsvResult(rows.Values.ToList(), rejected);
    }
}
