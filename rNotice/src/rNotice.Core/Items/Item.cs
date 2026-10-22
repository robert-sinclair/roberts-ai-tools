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

    public DateTimeOffset? PlannedStartUtc { get; init; }
    public string? CalendarEventId { get; init; }
    public DateTimeOffset? AckedAtUtc { get; init; }
    public DateTimeOffset? SnoozedUntilUtc { get; init; }
    public DateTimeOffset? LastFiredUtc { get; init; }

    public bool IsOpen => Stage is Stage.Intake or Stage.Planned;

    /// <summary>Snoozing re-arms the item, so it fires again once the snooze ends.</summary>
    public Item Snooze(DateTimeOffset untilUtc) => this with { SnoozedUntilUtc = untilUtc, LastFiredUtc = null };

    /// <summary>
    /// Moves the item to Planned. With a start time, rNotice reminds you then: the fire time
    /// becomes the planned start and the item is re-armed. Without one, the item keeps its own
    /// timing — a workbook row still alerts at its lead time. Local only; nothing reaches the
    /// calendar until promotion (step 8).
    /// </summary>
    public Item Plan(DateTimeOffset? startUtc) => startUtc is { } start
        ? this with { Stage = Stage.Planned, PlannedStartUtc = start, FireAtUtc = start, LastFiredUtc = null, SnoozedUntilUtc = null }
        : this with { Stage = Stage.Planned };

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

        var fireAt = PlannedStartUtc ?? incoming.FireAtUtc;
        var fireMoved = fireAt != FireAtUtc;

        return this with
        {
            SourceRef = incoming.SourceRef,
            Title = incoming.Title,
            Snippet = incoming.Snippet,
            From = incoming.From,
            DueUtc = incoming.DueUtc,
            FireAtUtc = fireAt,
            JoinUrl = incoming.JoinUrl,
            IsRead = incoming.IsRead ?? IsRead,
            Classification = Classification.IsSticky ? Classification : incoming.Classification,
            LastFiredUtc = fireMoved ? null : LastFiredUtc
        };
    }
}
