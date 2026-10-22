using rNotice.Core.Items;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class QueueOrderTests
{
    [Fact]
    public void Current_items_come_before_upcoming_ones_whatever_their_priority()
    {
        var nextWeekMeeting = Item(Interrupt.Takeover, Priority.P1, fireAt: Now.AddDays(7));
        var overdueTask = Item(Interrupt.Alert, Priority.P3, fireAt: Now.AddHours(-1));

        Assert.Equal([overdueTask.Id, nextWeekMeeting.Id],
            QueueOrder.Sort([nextWeekMeeting, overdueTask], Now).Select(i => i.Id));
    }

    [Fact]
    public void Current_items_sort_by_priority_upcoming_by_time()
    {
        var currentP2 = Item(priority: Priority.P2, fireAt: Now.AddMinutes(-1));
        var currentP1 = Item(priority: Priority.P1, fireAt: Now.AddMinutes(-30));
        var tomorrowP3 = Item(priority: Priority.P3, fireAt: Now.AddDays(1), due: Now.AddDays(1));
        var nextWeekP1 = Item(priority: Priority.P1, fireAt: Now.AddDays(7), due: Now.AddDays(7));

        Assert.Equal([currentP1.Id, currentP2.Id, tomorrowP3.Id, nextWeekP1.Id],
            QueueOrder.Sort([nextWeekP1, tomorrowP3, currentP2, currentP1], Now).Select(i => i.Id));
    }

    private static readonly TimeZoneInfo Mountain = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");

    [Fact]
    public void Day_is_the_local_day_not_the_UTC_day()
    {
        // 03:00 UTC on the 22nd is 21:00 MDT on the 21st.
        var evening = Item(due: new DateTimeOffset(2026, 10, 22, 3, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 10, 21), QueueOrder.DayOf(evening, Mountain));
    }

    [Fact]
    public void Undated_items_belong_to_the_day_they_arrived() =>
        Assert.Equal(new DateOnly(2026, 10, 21), QueueOrder.DayOf(Item() with { DueUtc = null }, Mountain));

    [Fact]
    public void By_day_puts_days_in_order_and_keeps_queue_order_within_a_day()
    {
        var tomorrow = Item(priority: Priority.P1, fireAt: Now.AddDays(1), due: Now.AddDays(1));
        var todayLater = Item(priority: Priority.P1, fireAt: Now.AddHours(2), due: Now.AddHours(2));
        var todayNowP2 = Item(priority: Priority.P2, fireAt: Now.AddMinutes(-5), due: Now.AddHours(1));
        var todayNowP1 = Item(priority: Priority.P1, fireAt: Now.AddMinutes(-5), due: Now.AddHours(3));

        Assert.Equal([todayNowP1.Id, todayNowP2.Id, todayLater.Id, tomorrow.Id],
            QueueOrder.SortByDay([tomorrow, todayLater, todayNowP2, todayNowP1], Now, Mountain).Select(i => i.Id));
    }

    [Fact]
    public void Only_current_unsnoozed_P1_P2_intake_items_need_attention()
    {
        Assert.True(QueueOrder.NeedsAttention(Item(priority: Priority.P1), Now));
        Assert.False(QueueOrder.NeedsAttention(Item(priority: Priority.P1, fireAt: Now.AddDays(1)), Now));
        Assert.False(QueueOrder.NeedsAttention(Item(priority: Priority.P3), Now));
        Assert.False(QueueOrder.NeedsAttention(Item(priority: Priority.P1, stage: Stage.Planned), Now));
        Assert.False(QueueOrder.NeedsAttention(Item(priority: Priority.P1).Snooze(Now.AddMinutes(5)), Now));
    }
}
