using rNotice.Core.Items;
using rNotice.Core.Routines;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class JiraTests
{
    private const string Site = "https://bravura-security.atlassian.net";
    private static readonly Guid Id = Guid.Parse("5b0c4a1e-2d3f-4e5a-9b6c-7d8e9f0a1b2c");

    private static JiraTicket Ticket(string key, string? priority, int daysOld, string? owner = "Jane Doe") =>
        new(key, $"Summary of {key}", "To Do", priority, owner, Now.AddDays(-daysOld), "Task");

    [Fact]
    public void The_request_carries_the_filter_and_where_to_write()
    {
        var text = Jira.RequestText(Id, "efficiency", "project = SAASK AND statusCategory != Done");

        Assert.Contains("\"kind\":\"jiraTickets\"", text);
        Assert.Contains("\"requestId\":\"5b0c4a1e-2d3f-4e5a-9b6c-7d8e9f0a1b2c\"", text);
        Assert.Contains("project = SAASK AND statusCategory != Done", text);
        Assert.Contains("\"maxTickets\":200", text);
    }

    [Fact]
    public void A_result_is_read_and_bad_tickets_are_skipped()
    {
        var json = """
            { "requestId": "5b0c4a1e-2d3f-4e5a-9b6c-7d8e9f0a1b2c", "kind": "jiraTickets", "tickets": [
              { "key": "SAASK-12", "summary": "Rotate the certs", "status": "To Do", "priority": "High",
                "assignee": "Jane Doe", "created": "2026-10-01T15:30:00.000-0600", "type": "Task" },
              { "key": "SAASK-12", "summary": "duplicate", "created": "2026-10-01T00:00:00Z" },
              { "key": "not a key", "summary": "x", "created": "2026-10-01T00:00:00Z" },
              { "key": "SAASK-13", "created": "2026-10-01T00:00:00Z" },
              { "key": "SAASK-14", "summary": "No date" }
            ] }
            """;

        var result = Jira.Parse(json, out var error);

        Assert.Null(error);
        Assert.Equal(Id, result!.Value.RequestId);
        var t = Assert.Single(result.Value.Tickets);
        Assert.Equal(("SAASK-12", "Rotate the certs", "High", "Jane Doe"), (t.Key, t.Summary, t.Priority, t.Owner));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 21, 30, 0, TimeSpan.Zero), t.CreatedUtc);
        Assert.Equal(3, result.Value.Skipped);
    }

    [Theory]
    [InlineData("{}", "no requestId")]
    [InlineData("""{ "requestId": "5b0c4a1e-2d3f-4e5a-9b6c-7d8e9f0a1b2c" }""", "no tickets list")]
    [InlineData("not json", null)]
    public void A_broken_result_is_rejected(string json, string? why)
    {
        Assert.Null(Jira.Parse(json, out var error));
        if (why is not null) Assert.Equal(why, error);
        else Assert.StartsWith("not valid JSON", error);
    }

    [Fact]
    public void Grouped_by_priority_then_oldest_first()
    {
        var groups = Jira.Grouped([
            Ticket("SAASK-1", "Low", 3),
            Ticket("SAASK-2", "None", 90),
            Ticket("SAASK-3", "High", 2),
            Ticket("SAASK-4", "High", 40),
            Ticket("SAASK-5", null, 5),
            Ticket("SAASK-6", "Odd", 1),
        ]);

        Assert.Equal(["High", "Low", "Odd", "None"], groups.Select(g => g.Priority));
        Assert.Equal(["SAASK-4", "SAASK-3"], groups[0].Tickets.Select(t => t.Key));
        Assert.Equal(["SAASK-2", "SAASK-5"], groups[3].Tickets.Select(t => t.Key));   // no priority counts as None
    }

    [Fact]
    public void Planning_a_ticket_makes_an_item_of_its_own()
    {
        var item = Jira.From(Ticket("SAASK-12", "High", 10), Site, Now);

        Assert.Equal(Source.Jira, item.Source);
        Assert.Equal(Stage.Intake, item.Stage);
        Assert.Equal("SAASK-12 Summary of SAASK-12", item.Title);
        Assert.Equal($"{Site}/browse/SAASK-12", item.SourceRef);
        Assert.Equal("SAASK-12", Jira.KeyOf(item));
        Assert.Contains("Owner: Jane Doe", item.Snippet);
    }

    [Fact]
    public void An_open_item_is_reused_but_a_done_one_is_not()
    {
        var first = Jira.From(Ticket("SAASK-12", "High", 10), Site, Now);
        var other = Jira.From(Ticket("SAASK-13", "High", 10), Site, Now);

        Assert.Equal(first.Id, Jira.OpenItemFor([first, other], "SAASK-12")?.Id);
        Assert.Null(Jira.OpenItemFor([first with { Stage = Stage.Done }, other], "SAASK-12"));

        // Two items from one ticket can both exist: the store keys on source id, which differs.
        Assert.NotEqual(first.SourceId, Jira.From(Ticket("SAASK-12", "High", 10), Site, Now).SourceId);
    }

    [Fact]
    public void A_snapshot_survives_a_round_trip()
    {
        var snapshot = new JiraSnapshot(Now, "project = SAASK", [Ticket("SAASK-1", "High", 3)]);
        Assert.Equal(snapshot.Tickets[0], Jira.Deserialize(Jira.Serialize(snapshot))!.Tickets[0]);
        Assert.Null(Jira.Deserialize("garbage"));
    }
}
