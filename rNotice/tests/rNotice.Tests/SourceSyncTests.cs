using rNotice.Core.Items;
using rNotice.Core.Rules;
using rNotice.Core.Sources;
using rNotice.Core.Store;
using rNotice.Core.Sync;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public sealed class SourceSyncTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rnotice-sync-{Guid.NewGuid():N}.db");
    private readonly ItemStore _store;
    private readonly SourceSync _sync;

    public SourceSyncTests()
    {
        _store = new ItemStore(_path, new DpapiFieldProtector());
        _sync = new SourceSync(_store, new Intake(new RulesOptions { MeetingBlocklist = ["Standup"] }, User));
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private static CalendarEvent Ev(string id, string subject = "Review", int inMinutes = 30) =>
        Event(subject, Now.AddMinutes(inMinutes)) with { Id = id };

    [Fact]
    public void Calendar_inserts_then_updates_without_duplicates()
    {
        Assert.Equal(1, _sync.SyncCalendar([Ev("a")], Now).Inserted);
        var second = _sync.SyncCalendar([Ev("a", "Review (moved)")], Now);

        Assert.Equal((0, 1), (second.Inserted, second.Updated));
        Assert.Equal("Review (moved)", Assert.Single(_store.List()).Title);
    }

    [Fact]
    public void Calendar_item_is_removed_when_its_event_is_gone_or_now_blocklisted()
    {
        _sync.SyncCalendar([Ev("a"), Ev("b")], Now);

        var report = _sync.SyncCalendar([Ev("b", "Daily Standup")], Now);

        Assert.Equal(2, report.Removed);
        Assert.Empty(_store.List());
    }

    [Fact]
    public void Dismissed_meeting_stays_dismissed_while_it_is_in_the_window()
    {
        _sync.SyncCalendar([Ev("a")], Now);
        var item = Assert.Single(_store.List());
        _store.Save(item with { Stage = Stage.Dismissed }, Now);

        _sync.SyncCalendar([Ev("a")], Now);

        Assert.Equal(Stage.Dismissed, Assert.Single(_store.List()).Stage);
    }

    [Fact]
    public void Workbook_row_acked_elsewhere_closes_its_item()
    {
        _sync.SyncWorkbook([Row()], Now);
        var report = _sync.SyncWorkbook([Row(status: "Acked")], Now);

        Assert.Equal(1, report.Closed);
        Assert.Equal(Stage.Done, Assert.Single(_store.List()).Stage);
    }

    [Fact]
    public void Workbook_row_deleted_removes_its_item()
    {
        _sync.SyncWorkbook([Row(id: "a"), Row(id: "b")], Now);
        Assert.Equal(1, _sync.SyncWorkbook([Row(id: "b")], Now).Removed);
        Assert.Equal("b", Assert.Single(_store.List()).SourceId);
    }

    [Fact]
    public void Workbook_row_with_a_broken_date_keeps_its_item()
    {
        _sync.SyncWorkbook([Row()], Now);
        var report = _sync.SyncWorkbook([Row(due: "next Tuesday")], Now);

        Assert.Equal((0, 0), (report.Removed, report.Closed));
        Assert.Single(_store.List());
    }

    [Fact]
    public void Local_done_survives_a_repoll_of_a_still_pending_row()
    {
        _sync.SyncWorkbook([Row()], Now);
        _store.Save(Assert.Single(_store.List()) with { Stage = Stage.Done }, Now);

        _sync.SyncWorkbook([Row()], Now);

        Assert.Equal(Stage.Done, Assert.Single(_store.List()).Stage);
    }
}
