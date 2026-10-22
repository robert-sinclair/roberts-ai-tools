using rNotice.Core.Scheduling;

namespace rNotice.Tests;

public class FreeSlotsTests
{
    // Fixed UTC-6, no DST. Thu 22 Oct 2026.
    private static readonly TimeZoneInfo Cst = TimeZoneInfo.FindSystemTimeZoneById("Canada Central Standard Time");
    private static readonly WorkHours Hours = new(new TimeOnly(8, 0), new TimeOnly(16, 0), Cst,
        new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday });

    private static readonly TimeSpan HalfHour = TimeSpan.FromMinutes(30);

    /// <summary>A local CST time on Thu 22 Oct, or a later day.</summary>
    private static DateTimeOffset T(int hour, int minute = 0, int day = 22) =>
        new DateTimeOffset(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(-6)).ToUniversalTime();

    private static TimeBlock Meeting(int h1, int m1, int h2, int m2, int day = 22) => new(T(h1, m1, day), T(h2, m2, day));

    [Fact]
    public void Starts_on_the_next_quarter_hour()
    {
        var slots = FreeSlots.Find(T(9, 2), HalfHour, Hours, []);
        Assert.Equal(T(9, 15), slots[0]);
    }

    [Fact]
    public void Before_work_starts_the_first_slot_is_the_start_of_the_day() =>
        Assert.Equal(T(8, 0), FreeSlots.Find(T(6, 40), HalfHour, Hours, [])[0]);

    [Fact]
    public void A_gap_too_short_for_the_duration_is_skipped()
    {
        // 09:00-09:15 meeting, 09:30 meeting: 09:15-09:30 is only 15 minutes.
        var busy = new[] { Meeting(9, 0, 9, 15), Meeting(9, 30, 10, 0) };

        var slots = FreeSlots.Find(T(9, 0), HalfHour, Hours, busy);

        Assert.Equal(T(10, 0), slots[0]);
    }

    [Fact]
    public void The_same_gap_fits_a_15_minute_slot()
    {
        var busy = new[] { Meeting(9, 0, 9, 15), Meeting(9, 30, 10, 0) };
        Assert.Equal(T(9, 15), FreeSlots.Find(T(9, 0), TimeSpan.FromMinutes(15), Hours, busy)[0]);
    }

    [Fact]
    public void A_slot_must_end_by_the_end_of_the_day()
    {
        // 15:45 + 30 min would run past 16:00, so the next slot is tomorrow 08:00.
        var slots = FreeSlots.Find(T(15, 40), HalfHour, Hours, []);
        Assert.Equal(T(8, 0, day: 23), slots[0]);
    }

    [Fact]
    public void A_meeting_ending_off_the_grid_rounds_up()
    {
        var busy = new[] { Meeting(9, 0, 9, 50) };
        Assert.Equal(T(10, 0), FreeSlots.Find(T(9, 0), HalfHour, Hours, busy)[0]);
    }

    [Fact]
    public void One_suggestion_per_gap()
    {
        // Free 08:00-10:00, busy 10:00-11:00, free 11:00-12:00, busy 12:00-13:00, free after.
        var busy = new[] { Meeting(10, 0, 11, 0), Meeting(12, 0, 13, 0) };

        var slots = FreeSlots.Find(T(7, 0), HalfHour, Hours, busy, max: 3);

        Assert.Equal([T(8, 0), T(11, 0), T(13, 0)], slots);
    }

    [Fact]
    public void Weekends_are_skipped()
    {
        // Friday 23 Oct after hours: next slot is Monday 26 Oct 08:00.
        var slots = FreeSlots.Find(T(17, 0, day: 23), HalfHour, Hours, []);
        Assert.Equal(T(8, 0, day: 26), slots[0]);
    }

    [Fact]
    public void A_fully_booked_day_moves_to_the_next()
    {
        var busy = new[] { Meeting(8, 0, 16, 0) };
        Assert.Equal(T(8, 0, day: 23), FreeSlots.Find(T(7, 0), HalfHour, Hours, busy)[0]);
    }

    [Fact]
    public void Longer_durations_need_longer_gaps()
    {
        // 08:00-09:00 free, then 09:00 meeting; 2 hours first fits at 10:00.
        var busy = new[] { Meeting(9, 0, 10, 0) };
        Assert.Equal(T(10, 0), FreeSlots.Find(T(8, 0), TimeSpan.FromHours(2), Hours, busy)[0]);
    }

    [Fact]
    public void Overlapping_meetings_are_jumped_together()
    {
        var busy = new[] { Meeting(8, 0, 9, 0), Meeting(8, 30, 9, 45) };
        Assert.Equal(T(9, 45), FreeSlots.Find(T(7, 0), HalfHour, Hours, busy)[0]);
    }
}
