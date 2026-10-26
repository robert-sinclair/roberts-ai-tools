namespace rNotice.Core.Items;

/// <summary>
/// One local record per thing, whatever it came from. All times are UTC.
///
/// Fields beyond SPEC.md's table: <see cref="FireAtUtc"/> (when it should first reach the user,
/// computed at intake), <see cref="JoinUrl"/> (the takeover's Join button),
/// <see cref="SnoozedUntilUtc"/> and <see cref="LastFiredUtc"/> (scheduler state).
/// </summary>
public sealed record Item
{
    public required Guid Id { get; init; }
    public required Source Source { get; init; }

    /// <summary>Row Id, event instance id, message id or chatMessage id. Unique per source.</summary>
    public required string SourceId { get; init; }

    /// <summary>Deep link back to Outlook, Teams or Excel.</summary>
    public string? SourceRef { get; init; }

    public required string Title { get; init; }

    /// <summary>A short excerpt. Never the full body.</summary>
    public string? Snippet { get; init; }

    public string? From { get; init; }
    public required DateTimeOffset ReceivedUtc { get; init; }

    public Stage Stage { get; init; } = Stage.Intake;
    public required Classification Classification { get; init; }

    public DateTimeOffset? DueUtc { get; init; }

    /// <summary>When the item should first interrupt. Null means never on its own — queue only.</summary>
    public DateTimeOffset? FireAtUtc { get; init; }

    /// <summary>Teams join link for a meeting. Read from <c>onlineMeeting.joinUrl</c>, never <c>onlineMeetingUrl</c>.</summary>
    public string? JoinUrl { get; init; }

    /// <summary>
    /// Mail only: whether you've read the message in Outlook. Null for other sources, or until
    /// rNotice has seen the flag. Shown, never acted on — by decision, reading a message doesn't
    /// close its item.
    /// </summary>
    public bool? IsRead { get; init; }

    /// <summary>
    /// Meetings only: when you clicked Join in rNotice. rNotice can't see whether you joined
    /// some other way — that needs Teams presence or attendance reports, neither of which is a
    /// granted permission — so this means "joined through rNotice", nothing more.
    /// </summary>
    public DateTimeOffset? JoinedUtc { get; init; }

    /// <summary>
    /// A Salesforce case's internal Lightning address, when the item is about a case and a
    /// Salesforce link was found in the message. Null: not looked for yet. Empty: looked, none.
    /// </summary>
    public string? CaseUrl { get; init; }

    /// <summary>
    /// A short note from rNotice about something it did to the item — why a planned task came
    /// back to Intake, say. Shown with the item; cleared when you plan it again.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// The calendar event the note is about — the meeting a plan clashed with — so the note can
    /// link to it. Null when the note points at nothing.
    /// </summary>
    public string? NoteLink { get; init; }

    public DateTimeOffset? PlannedStartUtc { get; init; }

    /// <summary>How long you planned it for. With the start, it's time the slot finder won't offer again.</summary>
    public int? PlannedMinutes { get; init; }
    public string? CalendarEventId { get; init; }

    /// <summary>
    /// You chose to have this plan on your Outlook calendar. The event is created, moved and
    /// removed to match; <see cref="CalendarEventId"/> is set once it exists.
    /// </summary>
    public bool PromoteToCalendar { get; init; }

    /// <summary>
    /// The start and end the Outlook event had when rNotice last wrote or read it. Comparing
    /// these with the plan and with the event says which side changed: a re-plan here, or a
    /// drag in Outlook.
    /// </summary>
    public DateTimeOffset? CalendarStartUtc { get; init; }
    public DateTimeOffset? CalendarEndUtc { get; init; }
    public DateTimeOffset? AckedAtUtc { get; init; }

    /// <summary>You renamed it. Your title then survives every later poll of the source.</summary>
    public bool TitleEdited { get; init; }

    public DateTimeOffset? SnoozedUntilUtc { get; init; }
    public DateTimeOffset? LastFiredUtc { get; init; }

    public bool IsOpen => Stage is Stage.Intake or Stage.Planned;

    /// <summary>Your own title for it. Blank puts the source's title back on the next poll.</summary>
    public Item Rename(string title) => string.IsNullOrWhiteSpace(title)
        ? this with { TitleEdited = false }
        : this with { Title = title.Trim(), TitleEdited = true };

    /// <summary>Snoozing re-arms the item, so it fires again once the snooze ends.</summary>
    public Item Snooze(DateTimeOffset untilUtc) => this with { SnoozedUntilUtc = untilUtc, LastFiredUtc = null };

    /// <summary>
    /// Moves the item to Planned. With a start time, rNotice reminds you then: the fire time
    /// becomes the planned start and the item is re-armed. Without one, the item keeps its own
    /// timing — a workbook row still alerts at its lead time. Local only; nothing reaches the
    /// calendar until promotion (step 8).
    /// </summary>
    /// <param name="alertLeadMinutes">
    /// How long before the start rNotice alerts. A plan on the Outlook calendar alerts a few
    /// minutes early, so Outlook's own reminder at the start is a backstop, not a duplicate.
    /// </param>
    public Item Plan(DateTimeOffset? startUtc, int? minutes = null, bool? promote = null, int alertLeadMinutes = 0) => startUtc is { } start
        ? this with
        {
            Stage = Stage.Planned, PlannedStartUtc = start, PlannedMinutes = minutes,
            FireAtUtc = start.AddMinutes(-alertLeadMinutes), LastFiredUtc = null, SnoozedUntilUtc = null,
            Note = null, NoteLink = null, PromoteToCalendar = promote ?? PromoteToCalendar
        }
        : this with { Stage = Stage.Planned, PlannedStartUtc = null, PlannedMinutes = null, Note = null, NoteLink = null, PromoteToCalendar = false };

    /// <summary>
    /// Takes a planned task back to Intake with a note saying why: its slot is gone. The planned
    /// time is cleared and it won't fire for the old slot; you plan it again.
    /// </summary>
    public Item BackToIntake(string note, DateTimeOffset nowUtc, string? noteLink = null) => this with
    {
        NoteLink = noteLink,
        Stage = Stage.Intake,
        PlannedStartUtc = null,
        PlannedMinutes = null,
        FireAtUtc = null,
        LastFiredUtc = nowUtc,
        Note = note
    };

    /// <summary>A user override. Sticky: later polls won't replace it.</summary>
    public Item Reclassify(Priority priority, Interrupt interrupt, string reason) =>
        this with { Classification = new Classification(priority, interrupt, ClassifiedBy.User, reason) };

    /// <summary>
    /// Folds a fresh poll of the same source thing into what's already stored.
    ///
    /// Source fields (title, times, links) come from the poll. User state (stage, acks, snooze,
    /// promotion) stays. Sticky classifications stay. If the fire time moved — a meeting dragged
    /// in Outlook, a row's due date edited — the item is re-armed so it fires at the new time.
    ///
    /// A planned start time is the user's choice and wins over the source's fire time, so
    /// editing a row's due date doesn't undo "remind me at 15:00".
    /// </summary>
    public Item MergeFrom(Item incoming)
    {
        if (incoming.Source != Source || incoming.SourceId != SourceId)
            throw new ArgumentException("Can only merge a poll of the same source item.", nameof(incoming));

        // A planned time is yours, alert lead and all: keep the fire time the plan set.
        var fireAt = PlannedStartUtc is not null ? FireAtUtc : incoming.FireAtUtc;
        var fireMoved = fireAt != FireAtUtc;

        return this with
        {
            SourceRef = incoming.SourceRef,
            Title = TitleEdited ? Title : incoming.Title,
            Snippet = incoming.Snippet,
            From = incoming.From,
            DueUtc = incoming.DueUtc,
            FireAtUtc = fireAt,
            JoinUrl = incoming.JoinUrl,
            IsRead = incoming.IsRead ?? IsRead,
            // Joined the meeting at its old time doesn't mean joined at the new one.
            JoinedUtc = incoming.DueUtc != DueUtc ? null : JoinedUtc,
            Classification = Classification.IsSticky ? Classification : incoming.Classification,
            LastFiredUtc = fireMoved ? null : LastFiredUtc
        };
    }
}
