using System.Net;
using System.Text.Json;
using rNotice.Core.Items;
using rNotice.Core.Routines;
using rNotice.Core.Rules;
using rNotice.Core.Sources;
using rNotice.Core.Store;
using rNotice.Core.Sync;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class RoutineTests
{
    private static readonly Guid Request = Guid.Parse("3f2c9a1e-8b4d-4c6e-9f1a-2d7e5b8c0a41");
    private const string Lightning = "https://bravurasecurity.lightning.force.com/lightning/r/Case/500N300001MewUsIAJ/view";

    private static Item Waiting(DateTimeOffset received) => Item() with
    {
        Source = Source.Teams, SourceId = "chat-dm", ReceivedUtc = received,
        Classification = new Classification(Priority.P3, Interrupt.Silent, ClassifiedBy.Default, "One-on-one chat"),
        Routine = new RoutineInfo { Status = RoutineStatus.Waiting }
    };

    private static RoutineResult Result(string priority = "P2", int confidence = 80) =>
        new(Request, priority, confidence, "Confirm cutover time", "Jane asks for the cutover time.", "Needs it by 3 pm.", "00038028", Lightning);

    private static string Json(string priority = "P2", int confidence = 80, string? caseUrl = Lightning) => JsonSerializer.Serialize(new
    {
        requestId = Request.ToString(), priority, confidence, title = "Confirm cutover time",
        summary = "Jane asks for the cutover time.", description = "Needs it by 3 pm.", caseNumber = "00038028", caseUrl
    });

    [Theory]
    [InlineData("Sure, I'll follow up this afternoon", true)]
    [InlineData("I’ll get back to you on that", true)]
    [InlineData("LET ME FOLLOW UP with the team", true)]
    [InlineData("Thanks, done", false)]
    public void Follow_up_phrases_match_anywhere_ignoring_case_and_curly_apostrophes(string text, bool expected) =>
        Assert.Equal(expected, Routine.IsFollowUp(text, new RulesOptions().FollowUpPhrases));

    [Fact]
    public void A_chat_is_sent_only_after_a_quiet_minute()
    {
        var item = Waiting(Now);
        Assert.False(Routine.IsDue(item, Now.AddSeconds(30), Now.AddSeconds(80)));   // your reply 50 s ago
        Assert.True(Routine.IsDue(item, Now.AddSeconds(30), Now.AddSeconds(90)));
        Assert.False(Routine.IsDue(item with { Routine = item.Routine! with { Status = RoutineStatus.Sent } }, null, Now.AddHours(1)));
    }

    [Fact]
    public void The_request_carries_its_id_and_the_newest_20_messages_each_cut_to_2000_characters()
    {
        var messages = Enumerable.Range(0, 25)
            .Select(i => new RoutineMessage("Jane", Now.AddMinutes(i), i == 24 ? new string('x', 3000) : $"m{i}")).ToList();

        using var doc = JsonDocument.Parse(Routine.RequestText(Request, "efficiency", "Jane Smith", null, messages));
        var sent = doc.RootElement.GetProperty("messages");

        Assert.Equal(Request.ToString(), doc.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(20, sent.GetArrayLength());
        Assert.Equal("m5", sent[0].GetProperty("text").GetString());
        Assert.Equal(2001, sent[19].GetProperty("text").GetString()!.Length);   // 2000 and the ellipsis
    }

    [Fact]
    public void A_result_file_is_named_after_its_request() =>
        Assert.Equal((Request, (Guid?)null), (Routine.RequestIdOf($"{Request}.json"), Routine.RequestIdOf("rNotice.json")));

    [Fact]
    public void A_valid_result_parses() =>
        Assert.Equal(Result(), Routine.Parse(Json(), out _));

    [Theory]
    [InlineData("P4", 80)]
    [InlineData("P2", 101)]
    [InlineData("P2", -1)]
    public void A_result_out_of_range_is_rejected(string priority, int confidence)
    {
        Assert.Null(Routine.Parse(Json(priority, confidence), out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Broken_json_is_rejected() => Assert.Null(Routine.Parse("{ not json", out _));

    [Theory]
    [InlineData("https://support.bravurasecurity.com/500N300001MewUsIAJ")]
    [InlineData("https://evil.example/lightning/r/Case/500N300001MewUsIAJ/view")]
    [InlineData("https://bravurasecurity.lightning.force.com/lightning/r/Case/500N300001MewUs/view")]
    public void Only_an_exact_Lightning_case_link_is_kept(string caseUrl) =>
        Assert.Null(Routine.Parse(Json(caseUrl: caseUrl), out _)!.CaseUrl);

    [Fact]
    public void P1_alerts_now_with_the_routines_title_and_case_link()
    {
        var applied = Routine.Apply(Waiting(Now), Result("P1", 90), Now.AddMinutes(2));

        Assert.Equal((Priority.P1, Interrupt.Alert, ClassifiedBy.Model), (applied.Classification.Priority, applied.Classification.Interrupt, applied.Classification.By));
        Assert.Equal((Now.AddMinutes(2), (DateTimeOffset?)null), (applied.FireAtUtc!.Value, applied.LastFiredUtc));
        Assert.Equal(("Confirm cutover time", Lightning, "Needs it by 3 pm."), (applied.Title, applied.CaseUrl, applied.Routine!.Description));
    }

    [Fact]
    public void P2_toasts_and_P3_is_silent()
    {
        Assert.Equal(Interrupt.Toast, Routine.Apply(Waiting(Now), Result("P2"), Now).Classification.Interrupt);
        Assert.Equal(Interrupt.Silent, Routine.Apply(Waiting(Now), Result("P3"), Now).Classification.Interrupt);
    }

    [Fact]
    public void Under_60_is_only_a_suggestion()
    {
        var applied = Routine.Apply(Waiting(Now), Result("P1", 59), Now);

        Assert.Equal((Priority.P3, ClassifiedBy.Default), (applied.Classification.Priority, applied.Classification.By));
        Assert.True(applied.Routine!.IsSuggestion);
        Assert.Equal("Confirm cutover time", applied.Title);
    }

    [Fact]
    public void Not_a_task_at_95_or_more_is_closed_and_under_95_stays_quietly()
    {
        var sure = Routine.Apply(Waiting(Now), Result("N0", 95), Now);
        var unsure = Routine.Apply(Waiting(Now), Result("N0", 94), Now);

        Assert.Equal((Stage.Done, "Routine: not a task (95%)"), (sure.Stage, sure.Note));
        Assert.Equal((Stage.Intake, Priority.P4, Interrupt.Silent), (unsure.Stage, unsure.Classification.Priority, unsure.Classification.Interrupt));
    }

    [Fact]
    public void Your_own_title_survives_a_result() =>
        Assert.Equal("Mine", Routine.Apply(Waiting(Now) with { Title = "Mine", TitleEdited = true }, Result(), Now).Title);

    [Fact]
    public void A_request_with_no_result_after_15_minutes_is_overdue()
    {
        var sent = Routine.Sent(Waiting(Now), Request, Now);
        Assert.False(Routine.IsOverdue(sent, Now.AddMinutes(15)));
        Assert.True(Routine.IsOverdue(sent, Now.AddMinutes(16)));
    }

    [Fact]
    public async Task Fire_sends_the_token_and_version_and_returns_the_session()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.OK,
            """{"type":"routine_fire","claude_code_session_id":"session_01","claude_code_session_url":"https://claude.ai/code/session_01"}""");
        var fire = await new RoutineClient(new HttpClient(fake))
            .FireAsync("https://api.anthropic.com/v1/claude_code/routines/trig_01/fire", "tok", "payload", default);

        var req = fake.Requests.Single();
        Assert.Equal(("Bearer", "tok"), (req.Headers.Authorization!.Scheme, req.Headers.Authorization.Parameter));
        Assert.False(req.Headers.Contains("anthropic-beta"));
        Assert.Equal("2026-10-21", req.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("payload", JsonDocument.Parse(fake.Bodies.Single()!).RootElement.GetProperty("text").GetString());
        Assert.Equal(new RoutineFire("session_01", "https://claude.ai/code/session_01"), fire);
    }

    [Fact]
    public async Task Fire_refuses_anything_but_an_Anthropic_routines_address() =>
        await Assert.ThrowsAsync<RoutineException>(() =>
            new RoutineClient(new HttpClient(new FakeHandler())).FireAsync("https://example.com/fire", "tok", "x", default));
}

public sealed class RoutineSyncTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rnotice-routine-{Guid.NewGuid():N}.db");
    private readonly ItemStore _store;

    public RoutineSyncTests() => _store = new ItemStore(_path, new DpapiFieldProtector());

    public void Dispose()
    {
        _store.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private SourceSync Sync(bool routine = true) => new(_store, new Intake(new RulesOptions(), User), routine);

    private static ChatMessage Theirs(int minutes, string text = "can you check the cutover time?") =>
        Chat(chatId: "chat-dm", type: ChatType.OneOnOne, id: $"t{minutes}", at: Now.AddMinutes(minutes), text: text);

    private static ChatMessage Mine(int minutes, string text) =>
        Chat(chatId: "chat-dm", type: ChatType.OneOnOne, from: "user-me", id: $"m{minutes}", at: Now.AddMinutes(minutes), text: text);

    private Item Dm => _store.GetBySource(Source.Teams, "chat-dm")!;

    [Fact]
    public void A_new_one_on_one_message_waits_for_the_routine()
    {
        Sync().SyncTeams([Theirs(0)], Now);
        Assert.Equal(RoutineStatus.Waiting, Dm.Routine!.Status);
    }

    [Fact]
    public void Without_the_routine_set_up_nothing_waits()
    {
        Sync(routine: false).SyncTeams([Theirs(0)], Now);
        Assert.Null(Dm.Routine);
    }

    [Fact]
    public void A_new_message_replaces_a_request_in_flight()
    {
        Sync().SyncTeams([Theirs(0)], Now);
        _store.Save(Routine.Sent(Dm, Guid.NewGuid(), Now.AddMinutes(1)), Now.AddMinutes(1));

        Sync().SyncTeams([Theirs(3, "also, the case is 00038028")], Now.AddMinutes(3));

        Assert.Equal((RoutineStatus.Waiting, (Guid?)null), (Dm.Routine!.Status, Dm.Routine.RequestId));
    }

    [Fact]
    public void Saying_you_will_follow_up_puts_it_in_Intake_as_P1_and_skips_the_routine()
    {
        Sync().SyncTeams([Theirs(0), Mine(2, "Thanks, I'll follow up after lunch")], Now.AddMinutes(2));

        Assert.Equal((Stage.Intake, Priority.P1, ClassifiedBy.User), (Dm.Stage, Dm.Classification.Priority, Dm.Classification.By));
        Assert.Null(Dm.Routine);

        Sync().SyncTeams([Theirs(5, "great, thanks")], Now.AddMinutes(5));
        Assert.Null(Dm.Routine);   // still yours: their next message doesn't send it
        Assert.Equal(Priority.P1, Dm.Classification.Priority);
    }

    [Fact]
    public void Following_up_on_a_chat_with_no_item_makes_one()
    {
        Sync().SyncTeams([Mine(0, "I will follow up on the licence question")], Now);
        Assert.Equal(("Follow up with SaaS Team", Priority.P1), (Dm.Title, Dm.Classification.Priority));
    }

    [Fact]
    public void An_ordinary_reply_closes_the_chat_and_cancels_the_request()
    {
        Sync().SyncTeams([Theirs(0)], Now);
        _store.Save(Routine.Sent(Dm, Guid.NewGuid(), Now.AddMinutes(1)), Now.AddMinutes(1));

        Sync().SyncTeams([Mine(2, "it's 3 pm")], Now.AddMinutes(2));

        Assert.Equal((Stage.Done, (RoutineInfo?)null), (Dm.Stage, Dm.Routine));
    }

    [Fact]
    public void Routine_state_survives_a_restart()
    {
        Sync().SyncTeams([Theirs(0)], Now);
        var applied = Routine.Apply(Routine.Sent(Dm, Guid.NewGuid(), Now), new RoutineResult(Guid.NewGuid(), "P2", 70, "T", "S", "D", null, null), Now);
        _store.Save(applied, Now);

        _store.Dispose();
        using var again = new ItemStore(_path, new DpapiFieldProtector());

        Assert.Equal(applied.Routine, again.GetBySource(Source.Teams, "chat-dm")!.Routine);
    }
}
