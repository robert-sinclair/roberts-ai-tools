using rNotice.Core.Items;
using rNotice.Core.Scheduling;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class PromotionTests
{
    private static readonly DateTimeOffset WinStart = Now.AddHours(-1);
    private static readonly DateTimeOffset WinEnd = Now.AddDays(14);
    private static readonly Dictionary<string, PromotedEvent> None = [];

    private static Item Planned(int startMin, bool promote = true, int minutes = 30) =>
        Item(stage: Stage.Intake).Plan(Now.AddMinutes(startMin), minutes, promote);

    /// <summary>A plan whose event exists exactly as rNotice wrote it.</summary>
    private static Item Synced(int startMin, string id = "evt-1", int minutes = 30) =>
        Planned(startMin, minutes: minutes) with
        {
            CalendarEventId = id,
            CalendarStartUtc = Now.AddMinutes(startMin),
            CalendarEndUtc = Now.AddMinutes(startMin + minutes)
        };

    private static Dictionary<string, PromotedEvent> Events(params PromotedEvent[] events) => events.ToDictionary(e => e.Id);

    private static PromotionStep One(Item item, Dictionary<string, PromotedEvent> events) =>
        Assert.Single(Promotion.Plan([item], events, WinStart, WinEnd, 30));

    [Fact]
    public void A_promoted_plan_without_an_event_is_created()
    {
        var step = One(Planned(60), None);
        Assert.Equal((PromotionKind.Create, Now.AddMinutes(60), Now.AddMinutes(90)), (step.Kind, step.StartUtc, step.EndUtc));
    }

    [Fact]
    public void A_plan_not_promoted_gets_nothing() =>
        Assert.Empty(Promotion.Plan([Planned(60, promote: false)], None, WinStart, WinEnd, 30));

    [Fact]
    public void In_sync_needs_nothing() =>
        Assert.Empty(Promotion.Plan([Synced(60)], Events(new PromotedEvent("evt-1", Now.AddMinutes(60), Now.AddMinutes(90))), WinStart, WinEnd, 30));

    [Fact]
    public void Re_planned_here_moves_the_event()
    {
        var replanned = Synced(60).Plan(Now.AddMinutes(180), 45);
        var step = One(replanned, Events(new PromotedEvent("evt-1", Now.AddMinutes(60), Now.AddMinutes(90))));
        Assert.Equal((PromotionKind.Move, Now.AddMinutes(180), Now.AddMinutes(225)), (step.Kind, step.StartUtc, step.EndUtc));
    }

    [Fact]
    public void Moved_in_Outlook_the_plan_follows()
    {
        var step = One(Synced(60), Events(new PromotedEvent("evt-1", Now.AddMinutes(120), Now.AddMinutes(150))));
        Assert.Equal((PromotionKind.FollowOutlook, Now.AddMinutes(120), Now.AddMinutes(150)), (step.Kind, step.StartUtc, step.EndUtc));
    }

    [Fact]
    public void Deleted_in_Outlook_is_lost_but_only_when_the_window_covers_it()
    {
        Assert.Equal(PromotionKind.Lost, One(Synced(60), None).Kind);
        Assert.Empty(Promotion.Plan([Synced(60 * 24 * 30)], None, WinStart, WinEnd, 30));   // a month out: can't tell
    }

    [Theory]
    [InlineData(Stage.Done)]
    [InlineData(Stage.Dismissed)]
    [InlineData(Stage.Intake)]
    public void Finished_dismissed_or_back_in_Intake_removes_the_event(Stage stage) =>
        Assert.Equal(PromotionKind.Remove, One(Synced(60) with { Stage = stage }, Events(new PromotedEvent("evt-1", Now.AddMinutes(60), Now.AddMinutes(90)))).Kind);

    [Fact]
    public void Planned_again_with_no_time_removes_the_event() =>
        Assert.Equal(PromotionKind.Remove, One(Synced(60).Plan(null), Events(new PromotedEvent("evt-1", Now.AddMinutes(60), Now.AddMinutes(90)))).Kind);

    [Fact]
    public void Removed_from_the_calendar_by_choice_removes_the_event() =>
        Assert.Equal(PromotionKind.Remove, One(Synced(60) with { PromoteToCalendar = false }, Events(new PromotedEvent("evt-1", Now.AddMinutes(60), Now.AddMinutes(90)))).Kind);

    [Fact]
    public void Meetings_are_never_promoted() =>
        Assert.Empty(Promotion.Plan([Planned(60) with { Source = Source.Calendar }], None, WinStart, WinEnd, 30));

    [Fact]
    public void A_promoted_plan_alerts_early_and_keeps_that_across_a_repoll()
    {
        var planned = Item(stage: Stage.Intake).Plan(Now.AddMinutes(60), 30, promote: true, alertLeadMinutes: 5);
        Assert.Equal(Now.AddMinutes(55), planned.FireAtUtc);
        Assert.Equal(Now.AddMinutes(55), planned.MergeFrom(Item()).FireAtUtc);
    }
}
