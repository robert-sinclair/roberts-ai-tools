using rNotice.Core.Items;
using rNotice.Core.Rules;

namespace rNotice.Core.Scheduling;

/// <summary>
/// Several Intake items planned as one block under a title you choose (asked 2026-10-28). The
/// group is an item of its own (<see cref="Source.Group"/>): it carries the title, the time and
/// the Outlook event, whose description lists each member's case number and subject. The
/// members keep their own titles and go to Planned with no time of their own, marked with the
/// group's id, so they don't alert or fill the schedule separately.
///
/// Done on the group finishes its members. Dismissing the group lets them go back to Intake.
/// </summary>
public static class GroupPlan
{
    /// <summary>A new group, not yet planned, for <paramref name="members"/> in the order given.</summary>
    public static Item Create(string title, IReadOnlyList<Item> members, DateTimeOffset nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        Source = Source.Group,
        SourceId = Guid.NewGuid().ToString("N"),
        Title = string.IsNullOrWhiteSpace(title) ? $"{members.Count} items" : title.Trim(),
        TitleEdited = true,
        Snippet = Description(members),
        ReceivedUtc = nowUtc,
        Stage = Stage.Intake,
        Classification = new Classification(Priority.P3, Interrupt.Silent, ClassifiedBy.User, "Grouped plan")
    };

    /// <summary>
    /// One line per member: "00039100 · subject" when it names a case, else the subject; the
    /// case's Salesforce link on the next line when rNotice found one.
    /// </summary>
    public static string Description(IEnumerable<Item> members) =>
        string.Join("\n", members.Select(m =>
        {
            var number = CaseLinks.FindAnyCaseNumber(m.Title) ?? CaseLinks.FindAnyCaseNumber(m.Snippet);
            var line = number is null ? m.Title : $"{number} · {m.Title}";
            return string.IsNullOrEmpty(m.CaseUrl) ? line : $"{line}\n    {m.CaseUrl}";
        }));

    /// <summary>A member: planned under the group, with no time or alert of its own.</summary>
    public static Item Join(Item member, Guid groupId, DateTimeOffset nowUtc) => member.StopWork(nowUtc) with
    {
        Stage = Stage.Planned, GroupId = groupId,
        PlannedStartUtc = null, PlannedMinutes = null, FireAtUtc = null, LastFiredUtc = nowUtc, SnoozedUntilUtc = null,
        PromoteToCalendar = false, Note = null, NoteLink = null
    };

    /// <summary>What a member becomes when its group changes stage; null leaves it as it is.</summary>
    public static Item? Follow(Item member, Stage groupStage, DateTimeOffset nowUtc) => groupStage switch
    {
        Stage.Done => member with { Stage = Stage.Done },
        Stage.Dismissed => member with { Stage = Stage.Intake, GroupId = null, LastFiredUtc = nowUtc },
        _ => null
    };
}
