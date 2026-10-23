using System.Text.Json;
using rNotice.Core.Graph;
using rNotice.Core.Items;
using rNotice.Core.Rules;
using rNotice.Core.Sources;
using rNotice.Core.Store;
using rNotice.Core.Sync;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class TeamsRuleTests
{
    private readonly Intake _intake = new(new RulesOptions { WatchedChatIds = ["chat-watched"] }, User);

    [Fact]
    public void Group_message_without_a_mention_waits_for_the_classifier() =>
        Assert.Equal("Group chat, no mention; waits for the classifier", _intake.FromTeams(Chat()).SkipReason);

    [Fact]
    public void Every_one_on_one_chat_is_watched_without_picking() =>
        Assert.False(_intake.FromTeams(Chat(chatId: "chat-dm", type: ChatType.OneOnOne)).Skipped);

    [Fact]
    public void One_on_one_watching_can_be_turned_off() =>
        Assert.True(new Intake(new RulesOptions { WatchOneOnOneChats = false }, User)
            .FromTeams(Chat(chatId: "chat-dm", type: ChatType.OneOnOne)).Skipped);

    [Fact]
    public void One_on_one_is_one_item_per_chat_and_a_mention_one_per_message()
    {
        Assert.Equal("chat-dm", _intake.FromTeams(Chat(chatId: "chat-dm", type: ChatType.OneOnOne)).Item!.SourceId);
        var mention = _intake.FromTeams(Chat(mentions: ["user-me"], id: "m7")).Item!;
        Assert.Equal("chat-watched|m7", mention.SourceId);
        Assert.Equal("Alice in SaaS Team", mention.From);
    }

    [Fact]
    public void Unpicked_group_chat_is_skipped_even_with_a_mention() =>
        Assert.Equal("Chat not watched", _intake.FromTeams(Chat(chatId: "chat-other", mentions: ["user-me"])).SkipReason);
}

public class TeamsClientTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;
    private static readonly ChatInfo Saas = new("19:abc@thread.v2", ChatType.Group, "SaaS Team", null);

    [Fact]
    public void Maps_a_message_with_a_mention()
    {
        var m = TeamsClient.MapMessage(Json("""
            {
              "id": "1729700000000", "messageType": "message", "deletedDateTime": null,
              "createdDateTime": "2026-10-23T18:00:00.123Z", "lastModifiedDateTime": "2026-10-23T18:00:00.123Z",
              "webUrl": "https://teams.microsoft.com/l/message/19:abc/1729700000000",
              "from": { "user": { "id": "user-alice", "displayName": "Alice" } },
              "body": { "contentType": "html", "content": "<p><at id=\"0\">Robert</at> can you check&nbsp;this?</p><p>Thanks &amp; cheers</p>" },
              "mentions": [ { "id": 0, "mentionText": "Robert", "mentioned": { "user": { "id": "user-me" } } } ],
              "attachments": []
            }
            """), Saas);

        Assert.Equal(["user-me"], m.MentionedUserIds);
        Assert.Equal("Robert can you check this?\nThanks & cheers", m.TextPreview);
        Assert.Equal(("user-alice", "Alice", "SaaS Team"), (m.FromUserId, m.FromName, m.ChatName));
        Assert.False(m.IsSystemMessage);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 18, 0, 0, 123, TimeSpan.Zero), m.CreatedUtc);
    }

    [Fact]
    public void Deleted_and_system_messages_are_system()
    {
        const string body = """
            "createdDateTime": "2026-10-23T18:00:00Z", "from": null, "body": { "content": "" }, "mentions": []
            """;
        Assert.True(TeamsClient.MapMessage(Json($$"""{ "id": "1", "messageType": "systemEventMessage", {{body}} }"""), Saas).IsSystemMessage);
        Assert.True(TeamsClient.MapMessage(Json($$"""{ "id": "2", "messageType": "message", "deletedDateTime": "2026-10-23T18:01:00Z", {{body}} }"""), Saas).IsSystemMessage);
    }

    [Fact]
    public void Attachment_only_message_says_so() =>
        Assert.Equal("(attachment)", TeamsClient.MapMessage(Json("""
            { "id": "3", "messageType": "message", "createdDateTime": "2026-10-23T18:00:00Z",
              "body": { "content": "<attachment id=\"x\"></attachment>" }, "attachments": [ { "id": "x" } ] }
            """), Saas).TextPreview);

    [Fact]
    public void A_chat_without_a_topic_is_named_after_the_others()
    {
        var chat = TeamsClient.MapChat(Json("""
            { "id": "c1", "chatType": "oneOnOne", "topic": null,
              "members": [ { "userId": "user-me", "displayName": "Robert" }, { "userId": "u2", "displayName": "Sami Cokar" } ],
              "lastMessagePreview": { "createdDateTime": "2026-10-23T17:00:00Z" } }
            """), "user-me")!;
        Assert.Equal((ChatType.OneOnOne, "Sami Cokar"), (chat.Type, chat.Name));
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 17, 0, 0, TimeSpan.Zero), chat.LastMessageUtc);
    }

    [Fact]
    public void Unknown_chat_types_are_left_out() =>
        Assert.Null(TeamsClient.MapChat(Json("""{ "id": "c2", "chatType": "unknownFutureValue", "members": [] }"""), "user-me"));
}

public sealed class TeamsSyncTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rnotice-teams-{Guid.NewGuid():N}.db");
    private readonly ItemStore _store;
    private readonly SourceSync _sync;

    public TeamsSyncTests()
    {
        _store = new ItemStore(_path, new DpapiFieldProtector());
        _sync = new SourceSync(_store, new Intake(new RulesOptions { WatchedChatIds = ["chat-watched"] }, User));
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private static ChatMessage Dm(string id, int minutes, string text) =>
        Chat(chatId: "chat-dm", type: ChatType.OneOnOne, id: id, at: Now.AddMinutes(minutes), text: text);

    private Item DmItem => _store.GetBySource(Source.Teams, "chat-dm")!;

    [Fact]
    public void A_back_and_forth_is_one_item_showing_the_latest_message()
    {
        _sync.SyncTeams([Dm("1", 0, "first"), Dm("2", 5, "second")], Now.AddMinutes(6));

        Assert.Single(_store.List());
        Assert.Equal(("second", Now.AddMinutes(5)), (DmItem.Title, DmItem.ReceivedUtc));
    }

    [Fact]
    public void A_new_message_reopens_a_chat_you_had_finished()
    {
        _sync.SyncTeams([Dm("1", 0, "first")], Now);
        _store.Save(DmItem with { Stage = Stage.Done }, Now);

        _sync.SyncTeams([Dm("2", 10, "one more thing")], Now.AddMinutes(10));

        Assert.Equal((Stage.Intake, "one more thing"), (DmItem.Stage, DmItem.Title));
    }

    [Fact]
    public void A_dismissed_chat_also_reopens_on_a_new_message()
    {
        _sync.SyncTeams([Dm("1", 0, "first")], Now);
        _store.Save(DmItem with { Stage = Stage.Dismissed }, Now);

        _sync.SyncTeams([Dm("2", 10, "again")], Now.AddMinutes(10));

        Assert.Equal(Stage.Intake, DmItem.Stage);
    }

    [Fact]
    public void An_older_message_coming_back_never_overwrites_a_newer_one()
    {
        _sync.SyncTeams([Dm("1", 0, "first"), Dm("2", 5, "second")], Now.AddMinutes(6));
        _store.Save(DmItem with { Stage = Stage.Done }, Now.AddMinutes(6));

        _sync.SyncTeams([Dm("1", 0, "first (edited)")], Now.AddMinutes(7));

        Assert.Equal((Stage.Done, "second"), (DmItem.Stage, DmItem.Title));
    }

    [Fact]
    public void Your_reply_closes_the_chat_and_their_next_message_reopens_it()
    {
        var mine = Chat(chatId: "chat-dm", type: ChatType.OneOnOne, from: "user-me", id: "2", at: Now.AddMinutes(3), text: "done");
        _sync.SyncTeams([Dm("1", 0, "can you send it?"), mine], Now.AddMinutes(4));
        Assert.Equal((Stage.Done, "You replied in Teams"), (DmItem.Stage, DmItem.Note));

        _sync.SyncTeams([Dm("3", 10, "thanks, one more")], Now.AddMinutes(11));
        Assert.Equal((Stage.Intake, (string?)null), (DmItem.Stage, DmItem.Note));
    }

    [Fact]
    public void Your_reply_leaves_a_planned_chat_planned()
    {
        _sync.SyncTeams([Dm("1", 0, "can you look at this today?")], Now);
        _store.Save(DmItem.Plan(Now.AddHours(2), 30), Now);

        _sync.SyncTeams([Chat(chatId: "chat-dm", type: ChatType.OneOnOne, from: "user-me", id: "2", at: Now.AddMinutes(3), text: "will do")], Now.AddMinutes(4));

        Assert.Equal(Stage.Planned, DmItem.Stage);
    }

    [Fact]
    public void A_mention_from_the_backlog_is_queued_without_alerting()
    {
        var report = _sync.SyncTeams([Chat(mentions: ["user-me"], at: Now.AddHours(-3))], Now);

        Assert.Equal(1, report.Inserted);
        Assert.NotNull(_store.List().Single().LastFiredUtc);
    }
}
