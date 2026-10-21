using System.Net;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using rNotice.Core.Graph;
using rNotice.Core.Sources;

namespace rNotice.Tests;

/// <summary>Answers requests from a queue of canned responses and records what was asked.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHandler Respond(HttpStatusCode status, string json, Action<HttpResponseMessage>? tweak = null)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        tweak?.Invoke(r);
        _responses.Enqueue(r);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(_responses.Dequeue());
    }

    public GraphClient Client() => new(new HttpClient(this), _ => Task.FromResult("token"));
}

public class GraphClientTests
{
    [Fact]
    public async Task Sends_bearer_token_and_UTC_preference()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.OK, "{}");
        await fake.Client().GetAsync("/me", default);

        var req = fake.Requests.Single();
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Contains("outlook.timezone=\"UTC\"", req.Headers.GetValues("Prefer").Single());
        Assert.Equal("https://graph.microsoft.com/v1.0/me", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task Retries_429_honouring_Retry_After()
    {
        var fake = new FakeHandler()
            .Respond(HttpStatusCode.TooManyRequests, "{}", r => r.Headers.RetryAfter = new(TimeSpan.Zero))
            .Respond(HttpStatusCode.OK, """{"ok":true}""");

        var result = await fake.Client().GetAsync("/me", default);

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task Does_not_retry_a_403_and_surfaces_Graph_error_code()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.Forbidden,
            """{"error":{"code":"ErrorAccessDenied","message":"Access is denied."}}""");

        var ex = await Assert.ThrowsAsync<GraphException>(() => fake.Client().GetAsync("/me", default));

        Assert.Equal((403, "ErrorAccessDenied"), (ex.Status, ex.Code));
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Follows_nextLink_to_the_end()
    {
        var fake = new FakeHandler()
            .Respond(HttpStatusCode.OK, """{"value":[1,2],"@odata.nextLink":"https://graph.microsoft.com/v1.0/next"}""")
            .Respond(HttpStatusCode.OK, """{"value":[3]}""");

        var all = new List<int>();
        await foreach (var v in fake.Client().GetPagedAsync("/first", default)) all.Add(v.GetInt32());

        Assert.Equal([1, 2, 3], all);
        Assert.Equal("https://graph.microsoft.com/v1.0/next", fake.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task Me_falls_back_to_UPN_when_mail_is_null()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.OK,
            """{"id":"u1","mail":null,"userPrincipalName":"r@x.com"}""");

        var me = await fake.Client().GetMeAsync(default);

        Assert.Equal(("u1", "r@x.com"), (me.UserId, me.Address));
    }
}

public class CalendarClientTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Maps_an_event()
    {
        var e = CalendarClient.Map(Json("""
            {
              "id": "AAMk1",
              "subject": "Cutover",
              "start": { "dateTime": "2026-10-21T16:00:00.0000000", "timeZone": "UTC" },
              "end":   { "dateTime": "2026-10-21T16:30:00.0000000", "timeZone": "UTC" },
              "isAllDay": false, "isCancelled": false, "showAs": "busy",
              "categories": ["rNotice"],
              "organizer": { "emailAddress": { "name": "Alice", "address": "a@x.com" } },
              "onlineMeeting": { "joinUrl": "https://teams.microsoft.com/l/meetup-join/abc" },
              "webLink": "https://outlook.office365.com/owa/?itemid=AAMk1"
            }
            """));

        Assert.Equal(new DateTimeOffset(2026, 10, 21, 16, 0, 0, TimeSpan.Zero), e.StartUtc);
        Assert.Equal(ShowAs.Busy, e.ShowAs);
        Assert.Equal(["rNotice"], e.Categories);
        Assert.Equal("Alice", e.Organizer);
        Assert.Equal("https://teams.microsoft.com/l/meetup-join/abc", e.JoinUrl);
    }

    [Fact]
    public void Missing_onlineMeeting_means_no_join_link() =>
        Assert.Null(CalendarClient.Map(Json("""
            { "id": "1", "subject": "s", "showAs": "workingElsewhere",
              "start": { "dateTime": "2026-10-21T16:00:00", "timeZone": "UTC" },
              "end":   { "dateTime": "2026-10-21T16:30:00", "timeZone": "UTC" },
              "onlineMeeting": null }
            """)).JoinUrl);

    [Fact]
    public void Non_UTC_zone_is_converted_not_trusted() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 22, 0, 0, TimeSpan.Zero),
            CalendarClient.Time(Json("""{ "dateTime": "2026-10-21T16:00:00", "timeZone": "Mountain Standard Time" }""")));
}

public class WorkbookClientTests
{
    private static JsonElement Values(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Maps_rows_by_header_name_not_position()
    {
        // Columns reordered and an extra one added by hand.
        var rows = WorkbookClient.MapRows(Values("""
            [
              ["DueUtc", "Notes", "Id", "Title", "LeadMinutes", "Status"],
              ["2026-10-21T16:00:00Z", "mine", "row-1", "Rotate", 30, ""]
            ]
            """), "https://x/rNotice.xlsx");

        var r = Assert.Single(rows);
        Assert.Equal("row-1", r.Id);
        Assert.Equal("2026-10-21T16:00:00Z", r.DueUtc);
        Assert.Equal(30.0, r.LeadMinutes);
        Assert.Equal("", r.Status);
        Assert.Null(r.Priority);   // no such column
        Assert.Equal("https://x/rNotice.xlsx", r.WebUrl);
    }

    [Fact]
    public void Header_only_table_has_no_rows() =>
        Assert.Empty(WorkbookClient.MapRows(Values("""[["Id","DueUtc"]]"""), null));

    [Fact]
    public void Blank_rows_are_not_rows() =>
        // A freshly created table comes back exactly like this: header plus one empty row.
        Assert.Empty(WorkbookClient.MapRows(Values("""[["Id","DueUtc"],["",""]]"""), null));

    [Fact]
    public void Missing_required_column_is_a_schema_error() =>
        Assert.Throws<WorkbookSchemaException>(() => WorkbookClient.MapRows(Values("""[["Id","Title"]]"""), null));

    [Fact]
    public void Blank_workbook_is_a_valid_xlsx_with_Sheet1()
    {
        using var ms = new MemoryStream(WorkbookClient.BlankWorkbook());
        using var doc = SpreadsheetDocument.Open(ms, false);
        Assert.Equal("Sheet1", doc.WorkbookPart!.Workbook.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Single().Name!.Value);
    }
}
