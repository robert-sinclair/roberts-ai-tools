using rNotice.Core.Items;
using rNotice.Core.Rules;
using rNotice.Core.Scheduling;
using rNotice.Core.Sources;
using rNotice.Core.Store;
using rNotice.Core.Sync;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class WorkingTests
{
    private readonly Scheduler _scheduler = new(new SchedulerOptions());

    private static Item PlannedTask(int startMin, int minutes = 30) =>
        (Item(Interrupt.Silent, Priority.P3) with { Source = Source.Teams }).Plan(Now.AddMinutes(startMin), minutes);

    /// <summary>A meeting as intake makes one: already Planned, with no planned time of its own.</summary>
    private static Item Meeting() =>
        Item(Interrupt.Takeover, Priority.P1, due: Now, stage: Stage.Planned) with { Source = Source.Calendar };

    [Fact]
    public void Check_in_comes_when_the_planned_block_ends_and_the_plan_stays() =>
        Assert.Equal((Now.AddMinutes(25), Now.AddMinutes(-5)),
            (PlannedTask(-5).StartWork(Now, 30).FireAtUtc, PlannedTask(-5).StartWork(Now, 30).PlannedStartUtc));

    [Fact]
    public void With_no_planned_time_the_check_in_is_the_default_length_away() =>
        Assert.Equal(Now.AddMinutes(30), Item(Interrupt.Silent, Priority.P3).StartWork(Now, 30).FireAtUtc);

    [Fact]
    public void A_block_already_over_checks_in_the_default_length_from_now() =>
        Assert.Equal(Now.AddMinutes(30), PlannedTask(-60).StartWork(Now, 30).FireAtUtc);

    [Fact]
    public void A_meeting_checks_in_when_the_meeting_ends() =>
        Assert.Equal(Now.AddMinutes(45), Meeting().StartWork(Now, 30, Now.AddMinutes(45)).FireAtUtc);

    [Fact]
    public void A_meeting_already_over_checks_in_the_default_length_from_now() =>
        Assert.Equal(Now.AddMinutes(30), Meeting().StartWork(Now, 30, Now.AddMinutes(-5)).FireAtUtc);

    [Fact]
    public void Starting_again_while_on_it_changes_nothing()
    {
        var working = PlannedTask(0).StartWork(Now, 30);
        Assert.Same(working, working.StartWork(Now.AddMinutes(5), 30));
    }

    [Fact]
    public void Time_adds_up_across_sessions()
    {
        var item = PlannedTask(0).StartWork(Now, 30).StopWork(Now.AddMinutes(20))
            .StartWork(Now.AddHours(1), 30);

        Assert.Equal(20, item.WorkedMinutes);
        Assert.Equal(TimeSpan.FromMinutes(45), item.Worked(Now.AddHours(1).AddMinutes(25)));
        Assert.False(item.StopWork(Now.AddHours(2)).IsWorking);
    }

    [Fact]
    public void While_working_nothing_fires_until_the_check_in_and_that_is_an_alert_not_a_takeover()
    {
        var working = PlannedTask(-5).StartWork(Now, 30);   // plan would take over; check-in at +25

        Assert.True(_scheduler.Tick([working], Now.AddMinutes(24), []).IsEmpty);
        var result = _scheduler.Tick([working], Now.AddMinutes(25), []);
        Assert.Single(result.Alerts);
        Assert.Empty(result.Takeovers);
    }

    [Fact]
    public void Still_working_moves_the_next_check_in_and_rearms_it()
    {
        var checkedIn = Scheduler.MarkFired(PlannedTask(0).StartWork(Now, 30), Now.AddMinutes(30));
        var kept = checkedIn.KeepWorking(Now.AddMinutes(30), 15);

        Assert.Equal((Now.AddMinutes(45), (DateTimeOffset?)null), (kept.FireAtUtc, kept.LastFiredUtc));
    }

    [Fact]
    public void A_poll_keeps_the_check_in_of_an_unplanned_item_you_are_working_on()
    {
        var working = Item(Interrupt.Silent, Priority.P3).StartWork(Now, 30);
        Assert.Equal(Now.AddMinutes(30), working.MergeFrom(Item(Interrupt.Silent, Priority.P3)).FireAtUtc);
    }

    [Fact]
    public void A_meeting_over_a_task_you_are_working_on_does_not_bounce_it()
    {
        var working = PlannedTask(0).StartWork(Now, 30);
        var meeting = new AgendaEntry(Now.AddMinutes(10), Now.AddMinutes(40), "Standup", true, "evt-9");
        Assert.Empty(PlanConflicts.Find([working], [meeting], Now, 30));
    }
}

public sealed class WorkingStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rnotice-work-{Guid.NewGuid():N}.db");
    private readonly ItemStore _store;

    public WorkingStoreTests() => _store = new ItemStore(_path, new DpapiFieldProtector());

    public void Dispose()
    {
        _store.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void Work_state_survives_a_restart()
    {
        var item = Item() with { WorkStartedUtc = Now, WorkedMinutes = 42 };
        _store.Save(item, Now);

        using var fresh = new ItemStore(_path, new DpapiFieldProtector());
        Assert.Equal((Now, 42), (fresh.Get(item.Id)!.WorkStartedUtc, fresh.Get(item.Id)!.WorkedMinutes));
    }

    [Fact]
    public void Your_Teams_reply_does_not_close_a_chat_you_are_working_on()
    {
        var sync = new SourceSync(_store, new Intake(new RulesOptions(), User));
        sync.SyncTeams([Chat(chatId: "dm", type: ChatType.OneOnOne, id: "1", text: "can you check this?")], Now);
        var item = _store.GetBySource(Source.Teams, "dm")!;
        _store.Save(item.StartWork(Now, 30), Now);

        sync.SyncTeams([Chat(chatId: "dm", type: ChatType.OneOnOne, from: "user-me", id: "2", at: Now.AddMinutes(2), text: "on it")], Now.AddMinutes(3));

        Assert.Equal(Stage.Intake, _store.Get(item.Id)!.Stage);
    }
}
