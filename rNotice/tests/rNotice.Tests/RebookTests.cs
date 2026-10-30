using rNotice.Core.Scheduling;

namespace rNotice.Tests;

public class RebookTests
{
    // Fixed UTC-6, no DST. Thu 22 Oct 2026; Fri 23 and Mon 26 follow.
    private static readonly TimeZoneInfo Cst = TimeZoneInfo.FindSystemTimeZoneById("Canada Central Standard Time");
    private static readonly WorkHours Hours = new(new TimeOnly(8, 0), new TimeOnly(16, 0), Cst,
        new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday });

    private static DateTimeOffset T(int hour, int minute = 0, int day = 22) =>
        new DateTimeOffset(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(-6)).ToUniversalTime();

    private static RebookChoice Pick(IReadOnlyList<RebookChoice> choices, RebookKind kind, int minutes) =>
        Assert.Single(choices, c => c.Kind == kind && c.Minutes == minutes);

    [Fact]
    public void Three_choices_for_each_length_on_a_clear_day()
    {
        var choices = Rebook.Choices(T(11, 2), Hours, []);

        Assert.Equal(T(11, 15), Pick(choices, RebookKind.NextFree, 30).StartUtc);
        Assert.Equal(T(12, 15), Pick(choices, RebookKind.HourLater, 30).StartUtc);
        Assert.Equal(T(8, 0, day: 23), Pick(choices, RebookKind.Tomorrow, 30).StartUtc);
        Assert.Equal(6, choices.Count);
    }

    [Fact]
    public void A_meeting_being_moved_is_offered_its_own_length_only()
    {
        var choices = Rebook.Choices(T(11, 2), Hours, [], [45]);

        Assert.All(choices, c => Assert.Equal(45, c.Minutes));
        Assert.Equal(T(11, 15), Pick(choices, RebookKind.NextFree, 45).StartUtc);
        Assert.Equal(3, choices.Count);
    }

    [Fact]
    public void Every_choice_avoids_meetings()
    {
        // 11:15-12:00 and 12:15-13:00 booked.
        var busy = new List<TimeBlock> { new(T(11, 15), T(12, 0)), new(T(12, 15), T(13, 0)) };

        var choices = Rebook.Choices(T(11, 2), Hours, busy);

        // 12:00-12:15 is too short for 30 minutes, so the first fit is after the second meeting.
        Assert.Equal(T(13, 0), Pick(choices, RebookKind.NextFree, 30).StartUtc);
    }

    [Fact]
    public void An_hour_slot_skips_gaps_too_short_for_it()
    {
        var busy = new List<TimeBlock> { new(T(11, 45), T(12, 0)) };
        Assert.Equal(T(12, 0), Pick(Rebook.Choices(T(11, 2), Hours, busy), RebookKind.NextFree, 60).StartUtc);
    }

    [Fact]
    public void Tomorrow_on_a_Friday_means_Monday() =>
        Assert.Equal(T(8, 0, day: 26), Pick(Rebook.Choices(T(10, 0, day: 23), Hours, []), RebookKind.Tomorrow, 30).StartUtc);

    [Fact]
    public void Late_in_the_day_later_is_dropped_when_it_would_be_tomorrow_anyway()
    {
        // 15:20: next free 30 min is 15:30; an hour later is past 16:00, so its slot is tomorrow 08:00 —
        // the same as Tomorrow, which keeps it.
        var choices = Rebook.Choices(T(15, 20), Hours, []);
        Assert.DoesNotContain(choices, c => c.Kind == RebookKind.HourLater && c.Minutes == 30);
        Assert.Equal(T(8, 0, day: 23), Pick(choices, RebookKind.Tomorrow, 30).StartUtc);
    }
}
