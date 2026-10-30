using System.Text.Json;

namespace rNotice.Core.Graph;

/// <summary>A file in the results folder.</summary>
public sealed record DriveFile(string Id, string Name);

/// <summary>
/// The routine's result files, in the OneDrive folder that holds the workbook. Delegated
/// <c>Files.ReadWrite</c>, already granted for the workbook, covers listing, reading and
/// deleting them.
/// </summary>
public sealed class ResultsFolderClient(GraphClient graph, string folder)
{
    private readonly GraphClient _graph = graph;
    private readonly string _folder = folder;

    public string Folder => _folder;

    /// <summary>The <c>.json</c> files in the folder. Other files are left alone.</summary>
    public async Task<IReadOnlyList<DriveFile>> ListJsonAsync(CancellationToken ct)
    {
        var files = new List<DriveFile>();
        var path = $"/me/drive/root:/{Uri.EscapeDataString(_folder)}:/children?$select=id,name,file&$top=200";
        await foreach (var f in _graph.GetPagedAsync(path, ct).ConfigureAwait(false))
            if (f.TryGetProperty("file", out _) && f.GetProperty("name").GetString() is { } name &&
                name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                files.Add(new DriveFile(f.GetProperty("id").GetString()!, name));
        return files;
    }

    /// <summary>
    /// A file's content as JSON text. Graph answers <c>/content</c> with a redirect to a
    /// pre-authenticated download address, which the HTTP client follows.
    /// </summary>
    public async Task<string> ReadAsync(string itemId, CancellationToken ct)
    {
        var content = await _graph.GetAsync($"/me/drive/items/{Uri.EscapeDataString(itemId)}/content", ct).ConfigureAwait(false);
        return content.ValueKind == JsonValueKind.Undefined ? "" : content.GetRawText();
    }

    public Task DeleteAsync(string itemId, CancellationToken ct) =>
        _graph.SendAsync(HttpMethod.Delete, $"/me/drive/items/{Uri.EscapeDataString(itemId)}", null, ct);
}
