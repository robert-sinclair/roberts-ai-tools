namespace rNotice.Core.Scheduling;

/// <summary>The hours you'll plan work into, in one fixed zone, on these days.</summary>
public sealed record WorkHours(TimeOnly Start, TimeOnly End, TimeZoneInfo Zone, IReadOnlySet<DayOfWeek> Days);

/// <summary>Time that's taken: a meeting, or something already planned.</summary>
public sealed record TimeBlock(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => StartUtc < end && EndUtc > start;
}

/// <summary>
/// Finds open time for planned work. Starts sit on the quarter hour; a slot must end by the
/// end of the working day and overlap nothing booked. Pure: calendar and planned items are
/// passed in.
/// </summary>
public static class FreeSlots
{
    public static readonly TimeSpan Step = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The earliest start in each separate free gap, soonest first, up to <paramref name="max"/>.
    /// One per gap rather than every quarter hour, so five suggestions are five real choices —
    /// not 09:15, 09:30, 09:45 in the same hole.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> Find(
        DateTimeOffset nowUtc, TimeSpan duration, WorkHours hours, IEnumerable<TimeBlock> busy,
        int max = 5, int horizonDays = 14)
    {
        var blocks = busy.OrderBy(b => b.StartUtc).ToList();
        var slots = new List<DateTimeOffset>();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, hours.Zone).DateTime);

        for (var d = 0; d <= horizonDays && slots.Count < max; d++)
        {
            var day = today.AddDays(d);
            if (!hours.Days.Contains(day.DayOfWeek)) continue;

            var dayStart = At(day, hours.Start, hours.Zone);
            var dayEnd = At(day, hours.End, hours.Zone);
            var cursor = RoundUp(dayStart > nowUtc ? dayStart : nowUtc, hours.Zone);

            while (cursor + duration <= dayEnd && slots.Count < max)
            {
                var end = cursor + duration;
                var clash = blocks.Where(b => b.Overlaps(cursor, end)).ToList();
                if (clash.Count > 0)
                {
                    // Jump past whatever's in the way, onto the next quarter hour.
                    cursor = RoundUp(clash.Max(b => b.EndUtc), hours.Zone);
                    continue;
                }

                slots.Add(cursor);

                // Skip the rest of this gap: the next suggestion comes after the next booking.
                var next = blocks.FirstOrDefault(b => b.StartUtc >= end);
                if (next is null || next.StartUtc >= dayEnd) break;
                cursor = RoundUp(next.EndUtc, hours.Zone);
            }
        }

        return slots;
    }

    /// <summary>
    /// Up to the next quarter hour on the local clock. Local, so the grid still lines up on
    /// :00 :15 :30 :45 in a zone whose offset isn't a whole hour.
    /// </summary>
    public static DateTimeOffset RoundUp(DateTimeOffset utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        var ticks = Step.Ticks;
        var rounded = new DateTime((local.DateTime.Ticks + ticks - 1) / ticks * ticks);
        return new DateTimeOffset(rounded, zone.GetUtcOffset(rounded)).ToUniversalTime();
    }

    private static DateTimeOffset At(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
