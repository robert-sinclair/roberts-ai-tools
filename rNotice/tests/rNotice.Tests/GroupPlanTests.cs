using rNotice.Core.Items;
using rNotice.Core.Rules;
using rNotice.Core.Scheduling;
using rNotice.Core.Store;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class GroupPlanTests
{
    private static Item Member(string title, string? caseUrl = null) =>
        Item(Interrupt.Alert, Priority.P2) with { Id = Guid.NewGuid(), Title = title, CaseUrl = caseUrl, Source = Source.Teams };

    [Fact]
    public void The_description_lists_each_case_number_and_subject()
    {
        var members = new[]
        {
            Member("Hey Rob look at this asap please 00039100"),
            Member("Case 00037339: login loop", caseUrl: "https://x.lightning.force.com/lightning/r/Case/500N300001MapmwIAB/view"),
            Member("Lunch?")
        };

        Assert.Equal(
            "00039100 · Hey Rob look at this asap please 00039100\n" +
            "00037339 · Case 00037339: login loop\n    https://x.lightning.force.com/lightning/r/Case/500N300001MapmwIAB/view\n" +
            "Lunch?",
            GroupPlan.Description(members));
    }

    [Fact]
    public void The_group_takes_your_title_or_a_count_if_you_leave_it_blank()
    {
        var members = new[] { Member("a"), Member("b") };
        Assert.Equal(("Case triage", Source.Group, true), (GroupPlan.Create(" Case triage ", members, Now) is var g ? (g.Title, g.Source, g.TitleEdited) : default));
        Assert.Equal("2 items", GroupPlan.Create("  ", members, Now).Title);
    }

    [Fact]
    public void Members_keep_their_titles_and_have_no_time_or_alert_of_their_own()
    {
        var group = GroupPlan.Create("t", [Member("a")], Now);
        var joined = GroupPlan.Join(Member("Original subject") with { FireAtUtc = Now }, group.Id, Now);

        Assert.Equal(("Original subject", Stage.Planned, (Guid?)group.Id, (DateTimeOffset?)null, (DateTimeOffset?)null),
            (joined.Title, joined.Stage, joined.GroupId, joined.PlannedStartUtc, joined.FireAtUtc));
        Assert.True(new Scheduler(new SchedulerOptions()).Tick([joined], Now.AddHours(1), []).IsEmpty);
    }

    [Fact]
    public void Members_follow_the_group_done_or_dismissed()
    {
        var member = GroupPlan.Join(Member("a"), Guid.NewGuid(), Now);

        Assert.Equal(Stage.Done, GroupPlan.Follow(member, Stage.Done, Now)!.Stage);
        Assert.Equal((Stage.Intake, (Guid?)null), GroupPlan.Follow(member, Stage.Dismissed, Now) is { } back ? (back.Stage, back.GroupId) : default);
        Assert.Null(GroupPlan.Follow(member, Stage.Planned, Now));
    }

    [Theory]
    [InlineData("look at this asap please 00039100", "00039100")]
    [InlineData("Case Assignment: 00038001", "00038001")]
    [InlineData("call me at 403-555-0003", null)]
    [InlineData("order 1200039100", null)]
    [InlineData("nothing here", null)]
    public void Case_numbers_are_found_with_or_without_the_word_case(string text, string? expected) =>
        Assert.Equal(expected, CaseLinks.FindAnyCaseNumber(text));
}

public sealed class GroupStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rnotice-group-{Guid.NewGuid():N}.db");
    private readonly ItemStore _store;

    public GroupStoreTests() => _store = new ItemStore(_path, new DpapiFieldProtector());

    public void Dispose()
    {
        _store.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void Group_id_survives_a_restart()
    {
        var groupId = Guid.NewGuid();
        var item = Item() with { GroupId = groupId };
        _store.Save(item, Now);

        using var fresh = new ItemStore(_path, new DpapiFieldProtector());
        Assert.Equal(groupId, fresh.Get(item.Id)!.GroupId);
    }
}
