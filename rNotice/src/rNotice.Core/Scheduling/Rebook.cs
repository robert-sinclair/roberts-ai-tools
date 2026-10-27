namespace rNotice.Core.Scheduling;

public enum RebookKind
{
    /// <summary>The earliest open slot from now.</summary>
    NextFree,

    /// <summary>The first open slot at least an hour from now.</summary>
    HourLater,

    /// <summary>The first open slot on the next working day.</summary>
    Tomorrow
}

public sealed record RebookChoice(RebookKind Kind, DateTimeOffset StartUtc, int Minutes);

/// <summary>
/// The alert window's "Rebook in" choices: next free, an hour or more later, or first thing on
/// the next working day — each for 30 minutes and for an hour. Same rules as the Plan dialog's
/// suggestions (<see cref="FreeSlots"/>): on the quarter hour, inside work hours, clear of
/// meetings and other plans. Asked 2026-10-27 in place of "Snooze 15 min".
/// </summary>
public static class Rebook
{
    public static readonly int[] Durations = [30, 60];

    public static IReadOnlyList<RebookChoice> Choices(DateTimeOffset nowUtc, WorkHours hours, IReadOnlyList<TimeBlock> busy)
    {
        var choices = new List<RebookChoice>();
        foreach (var minutes in Durations)
        {
            var length = TimeSpan.FromMinutes(minutes);
            DateTimeOffset? First(DateTimeOffset from) =>
                FreeSlots.Find(from, length, hours, busy, max: 1) is [var s] ? s : null;

            var next = First(nowUtc);
            var later = First(nowUtc.AddHours(1));
            var tomorrow = First(StartOfNextWorkingDay(nowUtc, hours));

            if (next is { } n) choices.Add(new(RebookKind.NextFree, n, minutes));
            // No duplicates: "later" goes when it's the same slot as next free (already an hour
            // out) or as tomorrow (late in the day), where those names say it better.
            if (later is { } l && l != next && l != tomorrow) choices.Add(new(RebookKind.HourLater, l, minutes));
            if (tomorrow is { } t && t != next) choices.Add(new(RebookKind.Tomorrow, t, minutes));
        }
        return choices;
    }

    /// <summary>Midnight, local, at the start of the next working day after today.</summary>
    internal static DateTimeOffset StartOfNextWorkingDay(DateTimeOffset nowUtc, WorkHours hours)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, hours.Zone).DateTime).AddDays(1);
        for (var i = 0; i < 7 && !hours.Days.Contains(day.DayOfWeek); i++) day = day.AddDays(1);
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, hours.Zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
