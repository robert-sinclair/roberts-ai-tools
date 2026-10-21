using System.Net.Http.Headers;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using rNotice.Core.Sources;

namespace rNotice.Core.Graph;

/// <summary>The workbook file as OneDrive describes it. <see cref="CTag"/> changes when the content does.</summary>
public sealed record WorkbookFile(string ItemId, string? CTag, string? WebUrl);

public sealed class WorkbookSchemaException(string message) : Exception(message);

/// <summary>
/// The task workbook in OneDrive for Business: create it if missing, notice when it changed,
/// read the <c>Actions</c> table.
///
/// Every call here is sessionless — no <c>workbook-session-id</c> — so nothing expires between
/// polls. The create sequence and the read shape were proven in the 2026-10-21 spike (SPEC.md,
/// <i>Writes</i>). This build never writes a row.
/// </summary>
public sealed class WorkbookClient(GraphClient graph, string folder, string fileName = "rNotice.xlsx")
{
    public const string TableName = "Actions";

    /// <summary>Column order in a workbook rNotice creates. Reading goes by header name, not position.</summary>
    public static readonly string[] Headers =
    [
        "Id", "Title", "Details", "Client", "DueUtc", "LeadMinutes", "Priority", "Interrupt",
        "Status", "AckedAtUtc", "AckedBy", "CalendarEventId"
    ];

    private readonly GraphClient _graph = graph;
    private string Path => $"/me/drive/root:/{Uri.EscapeDataString(folder)}/{Uri.EscapeDataString(fileName)}";

    public string DisplayPath => $"/{folder}/{fileName}";

    /// <summary>The file, or null if it doesn't exist.</summary>
    public async Task<WorkbookFile?> GetFileAsync(CancellationToken ct)
    {
        try
        {
            var item = await _graph.GetAsync($"{Path}?$select=id,cTag,webUrl", ct).ConfigureAwait(false);
            return ToFile(item);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Uploads a blank workbook, writes the header row, formats the two date columns as text,
    /// and turns the header into a table named <c>Actions</c>.
    ///
    /// <c>conflictBehavior=fail</c>: if a file appeared since the existence check, this throws
    /// rather than overwriting it.
    /// </summary>
    public async Task<WorkbookFile> CreateAsync(CancellationToken ct)
    {
        var bytes = BlankWorkbook();
        var uploaded = await _graph.SendAsync(HttpMethod.Put, $"{Path}:/content?@microsoft.graph.conflictBehavior=fail",
            () => new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet") }
            }, ct).ConfigureAwait(false);

        var file = ToFile(uploaded);
        var wb = $"/me/drive/items/{file.ItemId}/workbook";
        var lastColumn = (char)('A' + Headers.Length - 1);

        await _graph.SendJsonAsync(HttpMethod.Patch, $"{wb}/worksheets('Sheet1')/range(address='A1:{lastColumn}1')",
            new { values = new[] { Headers } }, ct).ConfigureAwait(false);

        // Dates are ISO 8601 text, deliberately. Format the columns before anyone types in them.
        var text = Enumerable.Range(0, 999).Select(_ => new[] { "@" }).ToArray();
        foreach (var column in new[] { "DueUtc", "AckedAtUtc" })
        {
            var letter = (char)('A' + Array.IndexOf(Headers, column));
            await _graph.SendJsonAsync(HttpMethod.Patch, $"{wb}/worksheets('Sheet1')/range(address='{letter}2:{letter}1000')",
                new { numberFormat = text }, ct).ConfigureAwait(false);
        }

        var table = await _graph.SendJsonAsync(HttpMethod.Post, $"{wb}/tables/add",
            new { address = $"Sheet1!A1:{lastColumn}1", hasHeaders = true }, ct).ConfigureAwait(false);
        await _graph.SendJsonAsync(HttpMethod.Patch, $"{wb}/tables/{Uri.EscapeDataString(table.GetProperty("id").GetString()!)}",
            new { name = TableName }, ct).ConfigureAwait(false);

        return await GetFileAsync(ct).ConfigureAwait(false) ?? file;
    }

    /// <summary>All data rows of the table, cells mapped by header name.</summary>
    public async Task<IReadOnlyList<WorkbookRow>> ReadRowsAsync(WorkbookFile file, CancellationToken ct)
    {
        var range = await _graph.GetAsync(
            $"/me/drive/items/{file.ItemId}/workbook/tables/{TableName}/range?$select=values", ct).ConfigureAwait(false);
        return MapRows(range.GetProperty("values"), file.WebUrl);
    }

    /// <summary>First row is the header. Missing <c>Id</c> or <c>DueUtc</c> columns is a schema error, not empty data.</summary>
    internal static IReadOnlyList<WorkbookRow> MapRows(JsonElement values, string? webUrl)
    {
        var rows = values.EnumerateArray().Select(r => r.EnumerateArray().Select(Cell).ToArray()).ToList();
        if (rows.Count == 0) throw new WorkbookSchemaException("The Actions table has no header row.");

        var index = rows[0]
            .Select((h, i) => (Name: (h as string)?.Trim() ?? "", i))
            .Where(x => x.Name.Length > 0)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);

        foreach (var required in new[] { "Id", "DueUtc" })
            if (!index.ContainsKey(required))
                throw new WorkbookSchemaException($"The Actions table has no '{required}' column.");

        object? Get(object?[] row, string name) =>
            index.TryGetValue(name, out var i) && i < row.Length ? row[i] : null;

        // Excel gives a new table one empty data row, and people leave blank rows. Neither is a task.
        return rows.Skip(1).Where(r => r.Any(c => c is not null && c is not "")).Select(r => new WorkbookRow(
            Get(r, "Id"), Get(r, "Title"), Get(r, "Details"), Get(r, "Client"), Get(r, "DueUtc"),
            Get(r, "LeadMinutes"), Get(r, "Priority"), Get(r, "Interrupt"), Get(r, "Status"), webUrl)).ToList();
    }

    /// <summary>Blank cells arrive as "", numbers as JSON numbers.</summary>
    private static object? Cell(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static WorkbookFile ToFile(JsonElement item) => new(
        item.GetProperty("id").GetString()!,
        item.TryGetProperty("cTag", out var c) ? c.GetString() : null,
        item.TryGetProperty("webUrl", out var w) ? w.GetString() : null);

    /// <summary>A valid, empty .xlsx with one sheet named Sheet1.</summary>
    internal static byte[] BlankWorkbook()
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new Workbook();
            var ws = wb.AddNewPart<WorksheetPart>();
            ws.Worksheet = new Worksheet(new SheetData());
            wb.Workbook.AppendChild(new Sheets()).Append(new Sheet { Id = wb.GetIdOfPart(ws), SheetId = 1, Name = "Sheet1" });
            wb.Workbook.Save();
        }
        return ms.ToArray();
    }
}
