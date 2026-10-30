using rNotice.Core.Items;
using rNotice.Core.Scheduling;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class FollowUpTests
{
    // Fixed UTC-6, no DST, as in RebookTests. Wed 21 Oct 2026 is Make.Now.
    private static readonly TimeZoneInfo Cst = TimeZoneInfo.FindSystemTimeZoneById("Canada Central Standard Time");
    private static readonly WorkHours Hours = new(new TimeOnly(8, 0), new TimeOnly(16, 0), Cst,
        new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday });

    private static DateTimeOffset Local(int day, int hour = 0, int minute = 0) =>
        new DateTimeOffset(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(-6)).ToUniversalTime();

    private static Item Meeting() =>
        Item(Interrupt.Takeover, Priority.P1, due: Local(21, 9, 30), stage: Stage.Planned) with
        {
            Source = Source.Calendar, Title = "Feedback - UoO", Snippet = "Node question", CaseUrl = "https://x/case"
        };

    [Fact]
    public void The_title_is_prefixed() =>
        Assert.Equal("Follow up - Feedback - UoO", FollowUp.TitleFor("Feedback - UoO"));

    [Fact]
    public void Following_up_a_follow_up_does_not_stack_the_prefix() =>
        Assert.Equal("Follow up - Feedback", FollowUp.TitleFor("Follow up - Feedback"));

    [Fact]
    public void Planning_starts_a_week_on()
    {
        // Wed 21 Oct + 7 = Wed 28 Oct, a working day: midnight local, so 06:00 UTC.
        Assert.Equal(Local(28), FollowUp.PlanFrom(Local(21, 9, 30), Hours));
    }

    [Fact]
    public void A_week_on_that_lands_on_a_weekend_moves_to_the_Monday()
    {
        // Sat 24 Oct + 7 = Sat 31 Oct; the next working day is Mon 2 Nov.
        var monday = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.FromHours(-6)).ToUniversalTime();
        Assert.Equal(monday, FollowUp.PlanFrom(Local(24, 9, 0), Hours));
    }

    [Fact]
    public void The_draft_is_a_new_unplanned_item_of_its_own()
    {
        var source = Meeting();
        var draft = FollowUp.From(source, Now);

        Assert.Equal(Source.FollowUp, draft.Source);
        Assert.Equal(Stage.Intake, draft.Stage);
        Assert.Equal("Follow up - Feedback - UoO", draft.Title);
        Assert.NotEqual(source.Id, draft.Id);
        Assert.Null(draft.PlannedStartUtc);

        // The text and the case link come with it; that's what makes the event worth reading.
        Assert.Equal(source.Snippet, draft.Snippet);
        Assert.Equal(source.CaseUrl, draft.CaseUrl);
    }

    [Fact]
    public void The_meeting_it_follows_is_untouched()
    {
        var source = Meeting();
        FollowUp.From(source, Now);
        Assert.Equal(Stage.Planned, source.Stage);
        Assert.Equal("Feedback - UoO", source.Title);
    }
}
