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
