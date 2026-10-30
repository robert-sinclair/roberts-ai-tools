using rNotice.Core.Items;
using rNotice.Core.Routines;
using rNotice.Core.Rules;
using rNotice.Core.Sources;
using rNotice.Core.Store;

namespace rNotice.Core.Sync;

public sealed record SyncReport(
    int Inserted,
    int Updated,
    int Removed,
    int Closed,
    IReadOnlyList<(string SourceId, string Reason)> Skipped)
{
    public override string ToString() =>
        $"{Inserted} new, {Updated} updated, {Removed} removed, {Closed} closed, {Skipped.Count} skipped";
}

/// <summary>
/// Reconciles one complete poll of a source with the store.
///
/// Only call these with a <b>complete</b> poll — every page fetched. Anything missing from the
/// poll is treated as gone, so a partial poll would delete live items.
/// </summary>
/// <param name="routine">The classify routine is set up: one-on-one chats with new messages are queued for it.</param>
public sealed class SourceSync(ItemStore store, Intake intake, bool routine = false)
{
    private readonly ItemStore _store = store;
    private readonly Intake _intake = intake;
    private readonly bool _routine = routine;

    /// <summary>
    /// Calendar items mirror the calendar. A meeting that was cancelled, moved out of the window,
    /// blocklisted or is over loses its item outright — no tombstone, so if it comes back it
    /// comes back as new. The exception: one you marked done stays while it's over but still in
    /// the calendar read, so it shows as done.
    /// </summary>
    public SyncReport SyncCalendar(IReadOnlyList<CalendarEvent> events, DateTimeOffset nowUtc)
    {
        int inserted = 0, updated = 0, removed = 0;
        var skipped = new List<(string, string)>();
        var live = new HashSet<string>();
        var seen = events.Select(e => e.Id).ToHashSet();

        foreach (var e in events)
        {
            var result = _intake.FromCalendar(e, nowUtc);
            if (result.Skipped) { skipped.Add((e.Id, result.SkipReason!)); continue; }

            live.Add(e.Id);
            var before = _store.GetBySource(Source.Calendar, e.Id);
            var ingest = _store.Ingest(result.Item!, nowUtc);
            switch (ingest.Outcome)
            {
                case IngestOutcome.Inserted: inserted++; break;
                case IngestOutcome.Updated: updated++; break;
            }

            // Meetings are never in Intake. A merge keeps the stored stage, so this also moves
            // items stored before meetings started entering as Planned.
            if (ingest.Item is { Stage: Stage.Intake } stale)
                _store.Save(stale with { Stage = Stage.Planned }, nowUtc);

            // Moved to a new time after you'd marked it done: it's a meeting you still have to
            // go to. Back to Planned, so it alerts at the new time — the merge has already
            // re-armed it.
            if (before is { Stage: Stage.Done } && ingest.Item is { } moved && moved.DueUtc != before.DueUtc)
                _store.Save(moved with { Stage = Stage.Planned }, nowUtc);
        }

        // A meeting you marked done keeps its item once it's over, while the calendar still shows
        // it, so the schedule can show it as done. Retention, or the read window moving on, ends it.
        foreach (var item in _store.List().Where(i => i.Source == Source.Calendar && !live.Contains(i.SourceId) &&
                     !(i.Stage == Stage.Done && seen.Contains(i.SourceId))))
        {
            _store.Delete(item.Id);
            removed++;
        }

        return new SyncReport(inserted, updated, removed, 0, skipped);
    }

    /// <summary>
    /// A row that is no longer pending — someone acked, finished or cancelled it — closes its
    /// open item as Done. A row that has gone from the table removes its item. A row that now
    /// fails to parse leaves its item alone: a typo shouldn't make work disappear.
    /// </summary>
    public SyncReport SyncWorkbook(IReadOnlyList<WorkbookRow> rows, DateTimeOffset nowUtc)
    {
        int inserted = 0, updated = 0, removed = 0, closed = 0;
        var skipped = new List<(string, string)>();
        var present = new HashSet<string>();

        foreach (var row in rows)
        {
            var id = Text(row.Id);
            if (id is not null) present.Add(id);

            var result = _intake.FromWorkbook(row, nowUtc);
            if (result.Skipped)
            {
                skipped.Add((id ?? "(no id)", result.SkipReason!));

                var status = Text(row.Status);
                var noLongerPending = status is not null && !status.Equals("Pending", StringComparison.OrdinalIgnoreCase);
                if (id is not null && noLongerPending &&
                    _store.GetBySource(Source.Workbook, id) is { IsOpen: true } open)
                {
                    _store.Save(open with { Stage = Stage.Done }, nowUtc);
                    closed++;
                }
                continue;
            }

            switch (_store.Ingest(result.Item!, nowUtc).Outcome)
            {
                case IngestOutcome.Inserted: inserted++; break;
                case IngestOutcome.Updated: updated++; break;
            }
        }

        foreach (var item in _store.List().Where(i => i.Source == Source.Workbook && !present.Contains(i.SourceId)))
        {
            _store.Delete(item.Id);
            removed++;
        }

        return new SyncReport(inserted, updated, removed, closed, skipped);
    }

    /// <summary>
    /// How old a message can be when rNotice first sees it and still interrupt. Older ones —
    /// the backlog on first run, or mail that arrived while the machine was off — go into the
    /// queue quietly. A toast is "it just arrived", so it shouldn't fire for something an hour old.
    /// </summary>
    public static readonly TimeSpan MailAlertWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// One round of inbox changes. Messages go through the mail rules. A message that left the
    /// inbox — deleted, or moved by you or an Outlook rule — removes its item if it's still in
    /// Intake; if you'd planned or finished it, it stays.
    /// </summary>
    public SyncReport SyncMail(IReadOnlyList<MailMessage> changed, IReadOnlyList<string> removedIds,
        IReadOnlyDictionary<string, DateTimeOffset> firstSentByConversation, DateTimeOffset nowUtc,
        IReadOnlyList<(string Id, bool IsRead)>? readChanges = null)
    {
        int inserted = 0, updated = 0, removed = 0;
        var skipped = new List<(string, string)>();

        // A bare read/unread flip from delta: note it on the item, change nothing else.
        foreach (var (id, isRead) in readChanges ?? [])
        {
            if (_store.GetBySource(Source.Mail, id) is { } item && item.IsRead != isRead)
            {
                _store.Save(item with { IsRead = isRead }, nowUtc);
                updated++;
            }
        }

        foreach (var id in removedIds)
        {
            if (_store.GetBySource(Source.Mail, id) is { Stage: Stage.Intake } item)
            {
                _store.Delete(item.Id);
                removed++;
            }
        }

        foreach (var message in changed)
        {
            var result = _intake.FromMail(message, firstSentByConversation);
            if (result.Skipped) { skipped.Add((message.Id, result.SkipReason!)); continue; }

            var item = result.Item!;
            if (nowUtc - item.ReceivedUtc > MailAlertWindow)
                item = item with { LastFiredUtc = nowUtc };   // backlog: queue, don't interrupt

            switch (_store.Ingest(item, nowUtc).Outcome)
            {
                case IngestOutcome.Inserted: inserted++; break;
                case IngestOutcome.Updated: updated++; break;
            }
        }

        return new SyncReport(inserted, updated, removed, 0, skipped);
    }

    /// <summary>
    /// New and changed messages from the watched chats. Oldest first, so a one-on-one chat's
    /// item ends on its latest message.
    ///
    /// A one-on-one item follows the conversation: a newer message updates it, and reopens it
    /// to Intake if you'd marked it done or dismissed it — it's something new to answer. An older
    /// message coming back (edited, or a reaction added) never overwrites a newer one.
    ///
    /// <b>A planned one-on-one item is frozen.</b> It keeps the message you planned and its link;
    /// a newer message in that chat starts a new Intake item instead of rewriting your plan. An
    /// edit or reaction to a message no newer than the frozen one adds nothing.
    ///
    /// Your own reply, newer than the message the item shows, moves an Intake item to Done:
    /// you've answered it. With the classify routine set up, it goes to the routine instead,
    /// which closes it only if your reply settled it. A planned one stays planned — you
    /// planned it for a reason.
    /// </summary>
    public SyncReport SyncTeams(IReadOnlyList<ChatMessage> messages, DateTimeOffset nowUtc)
    {
        int inserted = 0, updated = 0;
        var skipped = new List<(string, string)>();

        foreach (var message in messages.OrderBy(m => m.CreatedUtc))
        {
            if (_intake.IsMyOneOnOneReply(message))
            {
                if (_intake.IsFollowUp(message))
                {
                    if (FollowUp(message, nowUtc)) updated++;
                    continue;
                }
                if (_store.GetBySource(Source.Teams, message.ChatId) is { Stage: Stage.Intake, IsWorking: false } answered &&
                    message.CreatedUtc > answered.ReceivedUtc)
                {
                    // With the routine, a reply doesn't close the chat: it may answer the request or
                    // promise something, and the routine judges which (2026-10-30). Without it, or
                    // for backlog, or a chat you said you'd follow up on, a reply closes it as
                    // before. Either way a request still out is replaced or cancelled.
                    var judge = _routine && answered.Classification.By != ClassifiedBy.User &&
                                nowUtc - message.CreatedUtc <= MailAlertWindow;
                    _store.Save(judge
                        ? answered with { Note = "You replied in Teams", NoteLink = null, Routine = new RoutineInfo { Status = RoutineStatus.Waiting } }
                        : answered with { Stage = Stage.Done, Note = "You replied in Teams", NoteLink = null, Routine = null }, nowUtc);
                    updated++;
                }
                continue;
            }

            var result = _intake.FromTeams(message);
            if (result.Skipped) { skipped.Add((message.Id, result.SkipReason!)); continue; }

            var item = result.Item!;
            if (nowUtc - item.ReceivedUtc > MailAlertWindow)
                item = item with { LastFiredUtc = nowUtc };   // backlog: queue, don't interrupt

            if (message.ChatType == ChatType.OneOnOne && FrozenFrom(message.ChatId).Any(f => f.ReceivedUtc >= item.ReceivedUtc))
            {
                skipped.Add((message.Id, "Already planned from this chat"));
                continue;
            }

            if (_store.GetBySource(Source.Teams, item.SourceId) is { Stage: Stage.Planned } planned &&
                message.ChatType == ChatType.OneOnOne)
            {
                if (item.ReceivedUtc <= planned.ReceivedUtc)
                {
                    skipped.Add((message.Id, "Already planned from this chat"));
                    continue;
                }
                // Freeze the plan under a key of its own; the chat id is free for the new message.
                _store.Save(planned with { SourceId = FrozenKey(planned) }, nowUtc);
            }

            if (_store.GetBySource(Source.Teams, item.SourceId) is { } existing)
            {
                if (item.ReceivedUtc < existing.ReceivedUtc)
                {
                    skipped.Add((message.Id, "Older than the message the item already shows"));
                    continue;
                }
                if (item.ReceivedUtc > existing.ReceivedUtc && existing.Stage is Stage.Done or Stage.Dismissed)
                    _store.Save(existing with { Stage = Stage.Intake, Note = null, NoteLink = null }, nowUtc);
            }

            var ingest = _store.Ingest(item, nowUtc);
            switch (ingest.Outcome)
            {
                case IngestOutcome.Inserted: inserted++; break;
                case IngestOutcome.Updated: updated++; break;
            }

            // A merge keeps the first message's time; the item is about the latest one.
            var newer = ingest.Outcome == IngestOutcome.Inserted;
            if (ingest is { Outcome: IngestOutcome.Updated or IngestOutcome.Unchanged, Item: { } merged } &&
                merged.ReceivedUtc != item.ReceivedUtc)
            {
                newer = item.ReceivedUtc > merged.ReceivedUtc;
                _store.Save(merged with { ReceivedUtc = item.ReceivedUtc, LastFiredUtc = item.LastFiredUtc ?? merged.LastFiredUtc }, nowUtc);
            }

            if (newer) QueueForRoutine(item.SourceId, message, nowUtc);
        }

        return new SyncReport(inserted, updated, 0, 0, skipped);
    }

    /// <summary>
    /// A new message in a one-on-one chat queues it for the classify routine, replacing any
    /// request still out (its result won't match and is dropped). Not for a chat you said
    /// you'd follow up on, and not for backlog read on a first start.
    /// </summary>
    private void QueueForRoutine(string sourceId, ChatMessage message, DateTimeOffset nowUtc)
    {
        if (!_routine || message.ChatType != ChatType.OneOnOne || nowUtc - message.CreatedUtc > MailAlertWindow) return;
        if (_store.GetBySource(Source.Teams, sourceId) is not { Stage: Stage.Intake } item) return;
        if (item.Classification.By == ClassifiedBy.User) return;
        _store.Save(item with { Routine = new RoutineInfo { Status = RoutineStatus.Waiting } }, nowUtc);
    }

    /// <summary>
    /// You said you'll follow up: the chat goes to Intake as P1, with no routine call, and any
    /// request out for it is cancelled. A planned chat stays planned.
    /// </summary>
    private bool FollowUp(ChatMessage mine, DateTimeOffset nowUtc)
    {
        var classification = new Classification(Priority.P1, Interrupt.Silent, ClassifiedBy.User, "You said you'd follow up");
        if (_store.GetBySource(Source.Teams, mine.ChatId) is { } existing)
        {
            if (existing.Stage == Stage.Planned) return false;
            _store.Save(existing with
            {
                Stage = Stage.Intake, Classification = classification, Routine = null,
                Note = "You said you'd follow up", NoteLink = null
            }, nowUtc);
            return true;
        }

        _store.Save(new Item
        {
            Id = Guid.NewGuid(),
            Source = Source.Teams,
            SourceId = mine.ChatId,
            SourceRef = mine.WebUrl,
            Title = $"Follow up with {mine.ChatName ?? "Teams chat"}",
            Snippet = TextMatch.Truncate(mine.TextPreview, 2000),
            ReceivedUtc = mine.CreatedUtc,
            Classification = classification,
            Note = "You said you'd follow up"
        }, nowUtc);
        return true;
    }

    private const string FrozenMarker = "|planned|";

    private static string FrozenKey(Item planned) =>
        planned.SourceId + FrozenMarker + planned.ReceivedUtc.ToUnixTimeMilliseconds();

    private IEnumerable<Item> FrozenFrom(string chatId) =>
        _store.List().Where(i => i.Source == Source.Teams && i.SourceId.StartsWith(chatId + FrozenMarker, StringComparison.Ordinal));

    private static string? Text(object? cell)
    {
        var s = Convert.ToString(cell, System.Globalization.CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}
