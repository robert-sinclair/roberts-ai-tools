using rNotice.Core.Items;
using rNotice.Core.Sources;

namespace rNotice.Core.Scheduling;

/// <summary>A calendar instance, as far as suppression cares.</summary>
public sealed record BusyBlock(DateTimeOffset StartUtc, DateTimeOffset EndUtc, ShowAs ShowAs)
{
    /// <summary>Only busy and out-of-office suppress. Free and tentative — including rNotice's own blocks — never do.</summary>
    public bool Suppresses => ShowAs is ShowAs.Busy or ShowAs.Oof;

    public bool Covers(DateTimeOffset t) => StartUtc <= t && t < EndUtc;
}

/// <summary>
/// A daily local-time window. <see cref="Start"/> after <see cref="End"/> wraps midnight,
/// so 22:00–07:00 works as written.
/// </summary>
public sealed record QuietHours(TimeOnly Start, TimeOnly End, TimeZoneInfo Zone)
{
    public bool Contains(DateTimeOffset utc)
    {
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, Zone).DateTime);
        return Start <= End
            ? local >= Start && local < End
            : local >= Start || local < End;
    }

    /// <summary>The next moment quiet hours end, at or after <paramref name="utc"/>.</summary>
    public DateTimeOffset NextEnd(DateTimeOffset utc)
    {
        var local = TimeZoneInfo.ConvertTime(utc, Zone);
        var candidate = local.Date + End.ToTimeSpan();
        if (candidate <= local.DateTime) candidate = candidate.AddDays(1);
        return new DateTimeOffset(candidate, Zone.GetUtcOffset(candidate)).ToUniversalTime();
    }
}

public sealed record SchedulerOptions
{
    /// <summary>
    /// How long after a meeting's start its takeover may still fire. Past this — the app was
    /// off, the machine asleep — the meeting is under way and stays in the queue without a
    /// window. A non-meeting takeover past this comes up as an ordinary alert.
    /// </summary>
    public int TakeoverGraceSeconds { get; init; } = 300;

    public QuietHours? QuietHours { get; init; }

    /// <summary>
    /// How a task you planned with a time interrupts when its time comes, at the least. The
    /// plan is your decision that it matters then, so it never inherits a silent classification
    /// from the mail or chat it came from. Found 2026-10-26: four planned tasks passed silently.
    /// </summary>
    public Interrupt PlanInterrupt { get; init; } = Interrupt.Takeover;
}
