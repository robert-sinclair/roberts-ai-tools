using rNotice.Core.Items;
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
public sealed class SourceSync(ItemStore store, Intake intake)
{
    private readonly ItemStore _store = store;
    private readonly Intake _intake = intake;

    /// <summary>
    /// Calendar items mirror the calendar. A meeting that was cancelled, moved out of the window,
    /// blocklisted or is over loses its item outright — no tombstone, so if it comes back it
    /// comes back as new.
    /// </summary>
    public SyncReport SyncCalendar(IReadOnlyList<CalendarEvent> events, DateTimeOffset nowUtc)
    {
        int inserted = 0, updated = 0, removed = 0;
        var skipped = new List<(string, string)>();
        var live = new HashSet<string>();

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

        foreach (var item in _store.List().Where(i => i.Source == Source.Calendar && !live.Contains(i.SourceId)))
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

    private static string? Text(object? cell)
    {
        var s = Convert.ToString(cell, System.Globalization.CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}
