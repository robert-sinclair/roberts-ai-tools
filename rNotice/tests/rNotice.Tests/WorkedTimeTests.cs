using rNotice.Core.Items;
using rNotice.Core.Scheduling;
using rNotice.Core.Store;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class WorkedTimeTests
{
    // Planned Now–Now+60.
    private static Item Block() => (Item(Interrupt.Silent, Priority.P3) with { Source = Source.Teams }).Plan(Now, 60, promote: true);

    [Fact]
    public void Rebooking_a_started_block_keeps_the_time_so_far()
    {
        var before = Block();
        var at = Now.AddMinutes(25).AddSeconds(40);

        Assert.Equal((Now, Now.AddMinutes(25)), WorkedTime.Kept(before, before.Plan(Now.AddHours(3), 30), at, 30));
    }

    [Fact]
    public void After_the_block_ended_the_whole_block_is_kept() =>
        Assert.Equal((Now, Now.AddMinutes(60)), WorkedTime.Kept(Block(), Block().Plan(Now.AddDays(1), 30), Now.AddHours(2), 30));

    [Theory]
    [InlineData(Stage.Intake)]
    [InlineData(Stage.Dismissed)]
    public void Leaving_the_plan_any_other_way_keeps_it_too(Stage stage) =>
        Assert.NotNull(WorkedTime.Kept(Block(), Block() with { Stage = stage }, Now.AddMinutes(10), 30));

    [Fact]
    public void Not_started_yet_moves_as_before() =>
        Assert.Null(WorkedTime.Kept(Block(), Block().Plan(Now.AddHours(3), 30), Now.AddSeconds(30), 30));

    [Fact]
    public void Done_keeps_its_whole_block_instead() =>
        Assert.Null(WorkedTime.Kept(Block(), Block() with { Stage = Stage.Done }, Now.AddMinutes(10), 30));

    [Fact]
    public void Running_over_is_the_same_block_carrying_on()
    {
        var before = Block().StartWork(Now, 30);
        Assert.Null(WorkedTime.Kept(before, before.RunOver(Now.AddMinutes(90), Now.AddMinutes(90)), Now.AddMinutes(60), 30));
    }

    [Fact]
    public void Meetings_and_unplanned_items_have_nothing_to_keep()
    {
        var meeting = Block() with { Source = Source.Calendar };
        var unplanned = Item(Interrupt.Silent, Priority.P3);
        Assert.Null(WorkedTime.Kept(meeting, meeting with { Stage = Stage.Done }, Now.AddMinutes(10), 30));
        Assert.Null(WorkedTime.Kept(unplanned, unplanned.Plan(Now.AddHours(1), 30), Now.AddMinutes(10), 30));
    }
}

public sealed class WorkedBlockStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rnotice-worked-{Guid.NewGuid():N}.db");
    private readonly ItemStore _store;

    public WorkedBlockStoreTests() => _store = new ItemStore(_path, new DpapiFieldProtector());

    public void Dispose()
    {
        _store.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void Blocks_are_found_by_time_and_go_when_their_item_is_purged()
    {
        var item = Item() with { Stage = Stage.Done };
        _store.Save(item, Now.AddDays(-40));
        var block = new WorkedBlock("evt-1", item.Id, Now, Now.AddMinutes(25), "evt-1");
        _store.AddWorkedBlock(block);

        Assert.Equal([block], _store.WorkedBlocks(Now.AddHours(-1), Now.AddHours(1)));
        Assert.Empty(_store.WorkedBlocks(Now.AddHours(1), Now.AddHours(2)));

        _store.Purge(Now, retentionDays: 30);
        Assert.Empty(_store.WorkedBlocks(Now.AddHours(-1), Now.AddHours(1)));
    }
}
