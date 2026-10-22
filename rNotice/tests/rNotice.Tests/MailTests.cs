using System.Net;
using System.Text.Json;
using rNotice.Core.Graph;
using rNotice.Core.Items;
using rNotice.Core.Rules;
using rNotice.Core.Sources;
using rNotice.Core.Store;
using rNotice.Core.Sync;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class MailClientTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Maps_a_message()
    {
        var m = MailClient.Map(Json("""
            {
              "id": "m1", "conversationId": "c1", "subject": "RE: cert expiry",
              "from": { "emailAddress": { "name": "Alice", "address": "alice@contoso.com" } },
              "toRecipients": [ { "emailAddress": { "address": "robert.sinclair@bravurasecurity.com" } } ],
              "receivedDateTime": "2026-10-22T19:00:00Z",
              "bodyPreview": "Attached.", "webLink": "https://outlook.office365.com/owa/?ItemID=m1"
            }
            """))!;

        Assert.Equal(("c1", "alice@contoso.com", "Alice"), (m.ConversationId, m.FromAddress, m.FromName));
        Assert.Equal(["robert.sinclair@bravurasecurity.com"], m.To);
        Assert.False(m.IsMeetingMessage);
    }

    [Fact]
    public void Meeting_messages_are_recognised_by_odata_type() =>
        Assert.True(MailClient.Map(Json("""
            { "@odata.type": "#microsoft.graph.eventMessageResponse", "id": "m2", "subject": "Accepted: x",
              "from": { "emailAddress": { "address": "a@x.com" } }, "receivedDateTime": "2026-10-22T19:00:00Z" }
            """))!.IsMeetingMessage);

    [Fact]
    public void A_bare_change_entry_is_not_a_message() =>
        Assert.Null(MailClient.Map(Json("""{ "id": "m3", "isRead": true }""")));

    [Fact]
    public async Task First_round_filters_by_received_time_then_follows_links_to_the_deltaLink()
    {
        var fake = new FakeHandler()
            .Respond(HttpStatusCode.OK, """
                { "value": [ { "id": "gone", "@removed": { "reason": "deleted" } } ],
                  "@odata.nextLink": "https://graph.microsoft.com/v1.0/next" }
                """)
            .Respond(HttpStatusCode.OK, """
                { "value": [ { "id": "m1", "subject": "s", "conversationId": "c",
                               "from": { "emailAddress": { "address": "a@x.com" } },
                               "receivedDateTime": "2026-10-22T19:00:00Z" } ],
                  "@odata.deltaLink": "https://graph.microsoft.com/v1.0/delta?token=abc" }
                """);

        var delta = await new MailClient(fake.Client()).GetInboxChangesAsync(null, Now.AddDays(-3), default);

        Assert.Contains("$filter=receivedDateTime%20ge%202026-10-18T14%3A00%3A00Z", fake.Requests[0].RequestUri!.OriginalString);
        Assert.Equal(["gone"], delta.RemovedIds);
        Assert.Equal("m1", Assert.Single(delta.Changed).Id);
        Assert.Equal("https://graph.microsoft.com/v1.0/delta?token=abc", delta.DeltaLink);
        Assert.True(delta.Restarted);
    }

    [Fact]
    public async Task Expired_delta_token_restarts_from_the_backfill_window()
    {
        var fake = new FakeHandler()
            .Respond(HttpStatusCode.Gone, """{ "error": { "code": "SyncStateNotFound", "message": "expired" } }""")
            .Respond(HttpStatusCode.OK, """{ "value": [], "@odata.deltaLink": "https://graph.microsoft.com/v1.0/delta?token=new" }""");

        var delta = await new MailClient(fake.Client())
            .GetInboxChangesAsync("https://graph.microsoft.com/v1.0/delta?token=old", Now.AddDays(-3), default);

        Assert.True(delta.Restarted);
        Assert.Equal("https://graph.microsoft.com/v1.0/delta?token=new", delta.DeltaLink);
        Assert.Contains("$filter=", fake.Requests[1].RequestUri!.OriginalString);
    }

    [Fact]
    public async Task First_sent_per_conversation_is_the_earliest()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.OK, """
            { "value": [
                { "conversationId": "c1", "sentDateTime": "2026-10-20T10:00:00Z" },
                { "conversationId": "c1", "sentDateTime": "2026-10-18T10:00:00Z" },
                { "conversationId": "c2", "sentDateTime": "2026-10-21T10:00:00Z" } ] }
            """);

        var first = await new MailClient(fake.Client()).GetFirstSentByConversationAsync(Now.AddDays(-60), default);

        Assert.Equal(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero), first["c1"]);
        Assert.Equal(2, first.Count);
    }
}

public sealed class MailSyncTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rnotice-mail-{Guid.NewGuid():N}.db");
    private readonly ItemStore _store;
    private readonly SourceSync _sync;

    private static readonly IReadOnlyDictionary<string, DateTimeOffset> SentInConv1 =
        new Dictionary<string, DateTimeOffset> { ["conv-1"] = Now.AddDays(-1) };

    public MailSyncTests()
    {
        _store = new ItemStore(_path, new DpapiFieldProtector());
        _sync = new SourceSync(_store, new Intake(new RulesOptions(), User));
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void A_reply_just_arrived_is_armed_to_toast()
    {
        _sync.SyncMail([Mail() with { ReceivedUtc = Now.AddMinutes(-1) }], [], SentInConv1, Now);

        var item = Assert.Single(_store.List());
        Assert.Equal((Priority.P1, Interrupt.Toast), (item.Classification.Priority, item.Classification.Interrupt));
        Assert.Null(item.LastFiredUtc);
    }

    [Fact]
    public void Backlog_goes_into_the_queue_without_interrupting()
    {
        _sync.SyncMail([Mail() with { ReceivedUtc = Now.AddHours(-3) }], [], SentInConv1, Now);

        Assert.Equal(Now, Assert.Single(_store.List()).LastFiredUtc);
    }

    [Fact]
    public void Read_state_change_does_not_fire_again()
    {
        _sync.SyncMail([Mail() with { ReceivedUtc = Now.AddMinutes(-1) }], [], SentInConv1, Now);
        var fired = Assert.Single(_store.List()) with { LastFiredUtc = Now };
        _store.Save(fired, Now);

        // The same message comes back through delta because you read it.
        _sync.SyncMail([Mail() with { ReceivedUtc = Now.AddMinutes(-1) }], [], SentInConv1, Now.AddMinutes(5));

        Assert.Equal(Now, Assert.Single(_store.List()).LastFiredUtc);
    }

    [Fact]
    public void Leaving_the_inbox_removes_an_intake_item_but_not_a_planned_one()
    {
        _sync.SyncMail([Mail() with { Id = "a" }, Mail() with { Id = "b" }], [], SentInConv1, Now);
        _store.Save(_store.GetBySource(Source.Mail, "b")!.Plan(null), Now);

        var report = _sync.SyncMail([], ["a", "b"], SentInConv1, Now);

        Assert.Equal(1, report.Removed);
        Assert.Equal("b", Assert.Single(_store.List()).SourceId);
    }

    [Fact]
    public void Meeting_messages_never_become_items()
    {
        var report = _sync.SyncMail([Mail() with { IsMeetingMessage = true }], [], SentInConv1, Now);

        Assert.Empty(_store.List());
        Assert.Single(report.Skipped);
    }
}

public sealed class SyncStateTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rnotice-state-{Guid.NewGuid():N}.db");
    private readonly ItemStore _store;

    public SyncStateTests() => _store = new ItemStore(_path, new DpapiFieldProtector());

    public void Dispose()
    {
        _store.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void State_round_trips_and_clears()
    {
        Assert.Null(_store.GetState("mail.inbox.deltaLink"));
        _store.SetState("mail.inbox.deltaLink", "https://x/delta?token=1");
        _store.SetState("mail.inbox.deltaLink", "https://x/delta?token=2");
        Assert.Equal("https://x/delta?token=2", _store.GetState("mail.inbox.deltaLink"));

        _store.SetState("mail.inbox.deltaLink", null);
        Assert.Null(_store.GetState("mail.inbox.deltaLink"));
    }
}
