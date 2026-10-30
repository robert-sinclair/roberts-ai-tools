using rNotice.Core.Items;

namespace rNotice.Core.Scheduling;

/// <summary>
/// "Follow up" on the alert (asked 2026-10-30): a new item of your own, titled after the one it
/// follows, taken straight into the Plan dialog a week on. Nothing about the original changes —
/// a meeting you follow up still happens, and is still yours to join, finish or move.
///
/// The new item is <see cref="Source.FollowUp"/>, so no poll owns it: a calendar item is deleted
/// the moment it leaves the calendar read, and a workbook item the moment its row goes.
/// </summary>
public static class FollowUp
{
    public const string Prefix = "Follow up - ";

    /// <summary>How far on the planning starts. A week, by decision.</summary>
    public const int DaysOn = 7;

    /// <summary>"Follow up - X", and never "Follow up - Follow up - X".</summary>
    public static string TitleFor(string title) =>
        title.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? title : Prefix + title;

    /// <summary>Where the planning starts: midnight, local, a week on from <paramref name="fromUtc"/>.</summary>
    public static DateTimeOffset PlanFrom(DateTimeOffset fromUtc, WorkHours hours) =>
        FreeSlots.StartOfWorkingDay(fromUtc, hours, DaysOn);

    /// <summary>
    /// The draft. Not saved: the Plan dialog decides whether it exists at all, so cancelling
    /// leaves nothing behind. It keeps the original's text and case link, which is what makes
    /// the Outlook event worth reading a week later.
    /// </summary>
    public static Item From(Item source, DateTimeOffset nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        Source = Source.FollowUp,
        SourceId = Guid.NewGuid().ToString("N"),
        Title = TitleFor(source.Title),
        TitleEdited = true,
        Snippet = source.Snippet,
        From = source.From,
        CaseUrl = source.CaseUrl,
        ReceivedUtc = nowUtc,
        Stage = Stage.Intake,
        Classification = new Classification(Priority.P3, Interrupt.Silent, ClassifiedBy.User, "Follow-up")
    };
}
