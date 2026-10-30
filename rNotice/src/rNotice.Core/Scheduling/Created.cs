using rNotice.Core.Items;

namespace rNotice.Core.Scheduling;

/// <summary>
/// An item you type in yourself, from the queue's Create tab (asked 2026-10-30): a title, a
/// description, and then the ordinary Plan dialog. Nothing polls it, so nothing takes it away.
///
/// The description becomes the item's text, which is what the Outlook event's body is built
/// from — so a case number or a link written here is still there a week later.
/// </summary>
public static class Created
{
    public static Item From(string title, string description, DateTimeOffset nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        Source = Source.Created,
        SourceId = Guid.NewGuid().ToString("N"),
        Title = title.Trim(),
        TitleEdited = true,
        Snippet = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
        ReceivedUtc = nowUtc,
        Stage = Stage.Intake,
        Classification = new Classification(Priority.P3, Interrupt.Silent, ClassifiedBy.User, "Created here")
    };
}
