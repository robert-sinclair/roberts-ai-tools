using Microsoft.Data.Sqlite;
using rNotice.Core.Items;
using rNotice.Core.Scheduling;

namespace rNotice.Core.Store;

public enum IngestOutcome
{
    Inserted,
    Updated,

    /// <summary>Known, and the poll changed nothing. Not written.</summary>
    Unchanged,

    /// <summary>Dismissed earlier. The source is remembered so it doesn't come back.</summary>
    Ignored
}

public sealed record IngestResult(IngestOutcome Outcome, Item? Item);

/// <summary>
/// The local system of record: SQLite under <c>%LOCALAPPDATA%\rNotice</c>.
///
/// Content columns — title, snippet, sender, links, reason — are encrypted with
/// <see cref="IFieldProtector"/>. Ids, stages, priorities and times stay plain so they can be
/// queried; none of them say anything about what a message contained.
///
/// One connection, not thread-safe. The agent serialises access.
///
/// <b>Reads come from memory.</b> Every item is decrypted once, on first use, and kept; saves
/// and deletes write through to SQLite and update the copy. DPAPI costs about 0.3 ms a field,
/// so reading the table each time cost ~170 ms for 100 items — several times per UI refresh.
/// This is safe only because one process owns the file: nothing else may write it.
/// </summary>
public sealed class ItemStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly IFieldProtector _protector;
    private Dictionary<Guid, Item>? _items;
    private readonly Dictionary<(Source, string), Guid> _bySource = [];

    public ItemStore(string path, IFieldProtector protector)
    {
        _protector = protector;
        // Pooling off so Dispose actually releases the file.
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        _db.Open();
        CreateSchema();
        Migrate();
    }

    /// <summary>
    /// Columns added after a database already existed. <c>CREATE TABLE IF NOT EXISTS</c> leaves
    /// an existing table as it was, so each later column is added here if it's missing.
    /// </summary>
    private void Migrate()
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_table_info('items')";
            using var r = cmd.ExecuteReader();
            while (r.Read()) columns.Add(r.GetString(0));
        }

        if (!columns.Contains("is_read")) Execute("ALTER TABLE items ADD COLUMN is_read INTEGER");
        if (!columns.Contains("planned_minutes")) Execute("ALTER TABLE items ADD COLUMN planned_minutes INTEGER");
        if (!columns.Contains("joined_utc")) Execute("ALTER TABLE items ADD COLUMN joined_utc INTEGER");
        if (!columns.Contains("case_url")) Execute("ALTER TABLE items ADD COLUMN case_url BLOB");
        if (!columns.Contains("note")) Execute("ALTER TABLE items ADD COLUMN note BLOB");
        if (!columns.Contains("note_link")) Execute("ALTER TABLE items ADD COLUMN note_link TEXT");
        if (!columns.Contains("promote")) Execute("ALTER TABLE items ADD COLUMN promote INTEGER NOT NULL DEFAULT 0");
        if (!columns.Contains("calendar_start_utc")) Execute("ALTER TABLE items ADD COLUMN calendar_start_utc INTEGER");
        if (!columns.Contains("calendar_end_utc")) Execute("ALTER TABLE items ADD COLUMN calendar_end_utc INTEGER");
        if (!columns.Contains("title_edited")) Execute("ALTER TABLE items ADD COLUMN title_edited INTEGER NOT NULL DEFAULT 0");
        if (!columns.Contains("work_started_utc")) Execute("ALTER TABLE items ADD COLUMN work_started_utc INTEGER");
        if (!columns.Contains("worked_minutes")) Execute("ALTER TABLE items ADD COLUMN worked_minutes INTEGER NOT NULL DEFAULT 0");
    }

    private void CreateSchema() => Execute("""
        PRAGMA journal_mode = WAL;

        CREATE TABLE IF NOT EXISTS items (
            id                TEXT PRIMARY KEY,
            source            INTEGER NOT NULL,
            source_id         TEXT    NOT NULL,
            stage             INTEGER NOT NULL,
            priority          INTEGER NOT NULL,
            interrupt         INTEGER NOT NULL,
            classified_by     INTEGER NOT NULL,
            received_utc      INTEGER NOT NULL,
            due_utc           INTEGER,
            fire_at_utc       INTEGER,
            planned_start_utc INTEGER,
            acked_at_utc      INTEGER,
            snoozed_until_utc INTEGER,
            last_fired_utc    INTEGER,
            calendar_event_id TEXT,
            is_read           INTEGER,
            planned_minutes   INTEGER,
            joined_utc        INTEGER,
            case_url          BLOB,
            note              BLOB,
            note_link         TEXT,
            promote           INTEGER NOT NULL DEFAULT 0,
            calendar_start_utc INTEGER,
            calendar_end_utc  INTEGER,
            title_edited      INTEGER NOT NULL DEFAULT 0,
            work_started_utc  INTEGER,
            worked_minutes    INTEGER NOT NULL DEFAULT 0,
            updated_utc       INTEGER NOT NULL,
            title             BLOB NOT NULL,
            reason            BLOB NOT NULL,
            snippet           BLOB,
            sender            BLOB,
            source_ref        BLOB,
            join_url          BLOB,
            UNIQUE (source, source_id)
        );

        -- Dismissed items outlive retention as a bare (source, source_id): no content, just
        -- enough that a full re-poll of the calendar or workbook doesn't bring them back.
        CREATE TABLE IF NOT EXISTS dismissed_sources (
            source    INTEGER NOT NULL,
            source_id TEXT    NOT NULL,
            PRIMARY KEY (source, source_id)
        );

        -- Sync state that has to survive a restart, such as the inbox deltaLink. Encrypted
        -- like content: a delta token is opaque, but it is still a pointer into the mailbox.
        CREATE TABLE IF NOT EXISTS sync_state (
            key   TEXT PRIMARY KEY,
            value BLOB NOT NULL
        );

        -- Time spent in a planned block that was left behind when the plan moved on. Times and
        -- ids only: the title comes from the item, and the row goes when the item is purged.
        CREATE TABLE IF NOT EXISTS worked_blocks (
            id         TEXT PRIMARY KEY,
            item_id    TEXT    NOT NULL,
            start_utc  INTEGER NOT NULL,
            end_utc    INTEGER NOT NULL,
            event_id   TEXT
        );
        """);

    public void AddWorkedBlock(WorkedBlock block) =>
        Execute("""
            INSERT OR REPLACE INTO worked_blocks (id, item_id, start_utc, end_utc, event_id)
            VALUES ($id, $item, $start, $end, $event)
            """, ("$id", block.Id), ("$item", block.ItemId.ToString()), ("$start", block.StartUtc.ToUnixTimeMilliseconds()),
            ("$end", block.EndUtc.ToUnixTimeMilliseconds()), ("$event", (object?)block.EventId ?? DBNull.Value));

    public void RemoveWorkedBlock(string id) => Execute("DELETE FROM worked_blocks WHERE id = $id", ("$id", id));

    /// <summary>Worked blocks overlapping the span, earliest first.</summary>
    public IReadOnlyList<WorkedBlock> WorkedBlocks(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, item_id, start_utc, end_utc, event_id FROM worked_blocks WHERE start_utc < $to AND end_utc > $from ORDER BY start_utc";
        cmd.Parameters.AddWithValue("$from", fromUtc.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$to", toUtc.ToUnixTimeMilliseconds());
        using var r = cmd.ExecuteReader();
        var blocks = new List<WorkedBlock>();
        while (r.Read())
            blocks.Add(new WorkedBlock(r.GetString(0), Guid.Parse(r.GetString(1)),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
                r.IsDBNull(4) ? null : r.GetString(4)));
        return blocks;
    }

    public string? GetState(string key)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value FROM sync_state WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() is byte[] value ? _protector.Unprotect(value) : null;
    }

    public void SetState(string key, string? value)
    {
        if (value is null)
        {
            Execute("DELETE FROM sync_state WHERE key = $k", ("$k", key));
            return;
        }
        Execute("INSERT INTO sync_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            ("$k", key), ("$v", _protector.Protect(value)));
    }

    /// <summary>
    /// Stores what a poller produced. New → inserted. Known → merged with
    /// <see cref="Item.MergeFrom"/>, keeping user state. Dismissed → ignored.
    /// </summary>
    public IngestResult Ingest(Item incoming, DateTimeOffset nowUtc)
    {
        if (IsTombstoned(incoming.Source, incoming.SourceId))
            return new IngestResult(IngestOutcome.Ignored, null);

        var existing = GetBySource(incoming.Source, incoming.SourceId);
        if (existing is null)
        {
            Save(incoming, nowUtc);
            return new IngestResult(IngestOutcome.Inserted, Get(incoming.Id));
        }

        if (existing.Stage == Stage.Dismissed)
            return new IngestResult(IngestOutcome.Ignored, null);

        // Most polls change nothing — the same 24 meetings every minute. Don't rewrite them.
        var merged = AsStored(existing.MergeFrom(incoming));
        if (merged == existing)
            return new IngestResult(IngestOutcome.Unchanged, existing);

        Save(merged, nowUtc);
        return new IngestResult(IngestOutcome.Updated, merged);
    }

    /// <summary>Writes an item as it is — for changes the user or the scheduler made.</summary>
    public void Save(Item item, DateTimeOffset nowUtc)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO items (id, source, source_id, stage, priority, interrupt, classified_by,
                received_utc, due_utc, fire_at_utc, planned_start_utc, acked_at_utc,
                snoozed_until_utc, last_fired_utc, calendar_event_id, is_read, planned_minutes, joined_utc, case_url, note, note_link, promote, calendar_start_utc, calendar_end_utc, title_edited, work_started_utc, worked_minutes, updated_utc,
                title, reason, snippet, sender, source_ref, join_url)
            VALUES ($id, $source, $source_id, $stage, $priority, $interrupt, $classified_by,
                $received_utc, $due_utc, $fire_at_utc, $planned_start_utc, $acked_at_utc,
                $snoozed_until_utc, $last_fired_utc, $calendar_event_id, $is_read, $planned_minutes, $joined_utc, $case_url, $note, $note_link, $promote, $calendar_start_utc, $calendar_end_utc, $title_edited, $work_started_utc, $worked_minutes, $updated_utc,
                $title, $reason, $snippet, $sender, $source_ref, $join_url)
            ON CONFLICT (id) DO UPDATE SET
                source_id = excluded.source_id, stage = excluded.stage, priority = excluded.priority, interrupt = excluded.interrupt,
                classified_by = excluded.classified_by, received_utc = excluded.received_utc, due_utc = excluded.due_utc,
                fire_at_utc = excluded.fire_at_utc, planned_start_utc = excluded.planned_start_utc,
                acked_at_utc = excluded.acked_at_utc, snoozed_until_utc = excluded.snoozed_until_utc,
                last_fired_utc = excluded.last_fired_utc, calendar_event_id = excluded.calendar_event_id,
                is_read = excluded.is_read, planned_minutes = excluded.planned_minutes, joined_utc = excluded.joined_utc, case_url = excluded.case_url, note = excluded.note, note_link = excluded.note_link, promote = excluded.promote,
                calendar_start_utc = excluded.calendar_start_utc, calendar_end_utc = excluded.calendar_end_utc,
                title_edited = excluded.title_edited, work_started_utc = excluded.work_started_utc,
                worked_minutes = excluded.worked_minutes, updated_utc = excluded.updated_utc, title = excluded.title, reason = excluded.reason,
                snippet = excluded.snippet, sender = excluded.sender,
                source_ref = excluded.source_ref, join_url = excluded.join_url;
            """;

        var p = cmd.Parameters;
        p.AddWithValue("$id", item.Id.ToString());
        p.AddWithValue("$source", (int)item.Source);
        p.AddWithValue("$source_id", item.SourceId);
        p.AddWithValue("$stage", (int)item.Stage);
        p.AddWithValue("$priority", (int)item.Classification.Priority);
        p.AddWithValue("$interrupt", (int)item.Classification.Interrupt);
        p.AddWithValue("$classified_by", (int)item.Classification.By);
        p.AddWithValue("$received_utc", item.ReceivedUtc.ToUnixTimeMilliseconds());
        p.AddWithValue("$due_utc", Ms(item.DueUtc));
        p.AddWithValue("$fire_at_utc", Ms(item.FireAtUtc));
        p.AddWithValue("$planned_start_utc", Ms(item.PlannedStartUtc));
        p.AddWithValue("$acked_at_utc", Ms(item.AckedAtUtc));
        p.AddWithValue("$snoozed_until_utc", Ms(item.SnoozedUntilUtc));
        p.AddWithValue("$last_fired_utc", Ms(item.LastFiredUtc));
        p.AddWithValue("$calendar_event_id", (object?)item.CalendarEventId ?? DBNull.Value);
        p.AddWithValue("$is_read", item.IsRead is { } read ? (read ? 1 : 0) : DBNull.Value);
        p.AddWithValue("$planned_minutes", (object?)item.PlannedMinutes ?? DBNull.Value);
        p.AddWithValue("$joined_utc", Ms(item.JoinedUtc));
        p.AddWithValue("$case_url", Seal(item.CaseUrl));
        p.AddWithValue("$note", Seal(item.Note));
        p.AddWithValue("$note_link", (object?)item.NoteLink ?? DBNull.Value);
        p.AddWithValue("$promote", item.PromoteToCalendar ? 1 : 0);
        p.AddWithValue("$calendar_start_utc", Ms(item.CalendarStartUtc));
        p.AddWithValue("$calendar_end_utc", Ms(item.CalendarEndUtc));
        p.AddWithValue("$title_edited", item.TitleEdited ? 1 : 0);
        p.AddWithValue("$work_started_utc", Ms(item.WorkStartedUtc));
        p.AddWithValue("$worked_minutes", item.WorkedMinutes);
        p.AddWithValue("$updated_utc", nowUtc.ToUnixTimeMilliseconds());
        p.AddWithValue("$title", _protector.Protect(item.Title));
        p.AddWithValue("$reason", _protector.Protect(item.Classification.Reason));
        p.AddWithValue("$snippet", Seal(item.Snippet));
        p.AddWithValue("$sender", Seal(item.From));
        p.AddWithValue("$source_ref", Seal(item.SourceRef));
        p.AddWithValue("$join_url", Seal(item.JoinUrl));
        cmd.ExecuteNonQuery();

        Remember(AsStored(item));
    }

    /// <summary>Removes an item outright, leaving no tombstone. For items whose source thing is gone.</summary>
    public void Delete(Guid id)
    {
        Execute("DELETE FROM items WHERE id = $id", ("$id", id.ToString()));
        if (Items.Remove(id, out var gone)) _bySource.Remove((gone.Source, gone.SourceId));
    }

    public Item? Get(Guid id) => Items.GetValueOrDefault(id);

    public Item? GetBySource(Source source, string sourceId)
    {
        var items = Items;   // loads the index on first use
        return _bySource.TryGetValue((source, sourceId), out var id) ? items[id] : null;
    }

    /// <summary>Items in the given stages, or every item if none are given.</summary>
    public IReadOnlyList<Item> List(params Stage[] stages) =>
        stages.Length == 0
            ? Items.Values.ToList()
            : Items.Values.Where(i => stages.Contains(i.Stage)).ToList();

    /// <summary>The decrypted copy, read from SQLite the first time it's needed.</summary>
    private Dictionary<Guid, Item> Items
    {
        get
        {
            if (_items is not null) return _items;
            _items = [];
            _bySource.Clear();
            foreach (var item in Query("SELECT * FROM items")) Remember(item);
            return _items;
        }
    }

    private void Remember(Item item)
    {
        if (Items.TryGetValue(item.Id, out var old)) _bySource.Remove((old.Source, old.SourceId));
        _items![item.Id] = item;
        _bySource[(item.Source, item.SourceId)] = item.Id;
    }

    /// <summary>
    /// The item as a read from SQLite would return it: times to the millisecond, in UTC. Keeps
    /// the in-memory copy identical to what comes back after a restart.
    /// </summary>
    private static Item AsStored(Item i) => i with
    {
        ReceivedUtc = Round(i.ReceivedUtc),
        DueUtc = Round(i.DueUtc),
        FireAtUtc = Round(i.FireAtUtc),
        PlannedStartUtc = Round(i.PlannedStartUtc),
        AckedAtUtc = Round(i.AckedAtUtc),
        SnoozedUntilUtc = Round(i.SnoozedUntilUtc),
        LastFiredUtc = Round(i.LastFiredUtc),
        JoinedUtc = Round(i.JoinedUtc),
        CalendarStartUtc = Round(i.CalendarStartUtc),
        CalendarEndUtc = Round(i.CalendarEndUtc),
        WorkStartedUtc = Round(i.WorkStartedUtc)
    };

    private static DateTimeOffset Round(DateTimeOffset t) => DateTimeOffset.FromUnixTimeMilliseconds(t.ToUnixTimeMilliseconds());
    private static DateTimeOffset? Round(DateTimeOffset? t) => t is { } v ? Round(v) : null;

    /// <summary>
    /// Deletes Done and Dismissed items not touched for <paramref name="retentionDays"/>. The
    /// app is not an archive of your mail. Dismissed ones leave a tombstone.
    /// </summary>
    public int Purge(DateTimeOffset nowUtc, int retentionDays)
    {
        var cutoff = nowUtc.AddDays(-retentionDays).ToUnixTimeMilliseconds();
        using var tx = _db.BeginTransaction();

        Execute("""
            INSERT OR IGNORE INTO dismissed_sources (source, source_id)
            SELECT source, source_id FROM items WHERE stage = $dismissed AND updated_utc < $cutoff
            """, ("$dismissed", (int)Stage.Dismissed), ("$cutoff", cutoff));

        var deleted = Execute("DELETE FROM items WHERE stage IN ($done, $dismissed) AND updated_utc < $cutoff",
            ("$done", (int)Stage.Done), ("$dismissed", (int)Stage.Dismissed), ("$cutoff", cutoff));
        Execute("DELETE FROM worked_blocks WHERE item_id NOT IN (SELECT id FROM items)");

        tx.Commit();
        if (deleted > 0) _items = null;   // re-read on next use
        return deleted;
    }

    private bool IsTombstoned(Source source, string sourceId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dismissed_sources WHERE source = $s AND source_id = $sid";
        cmd.Parameters.AddWithValue("$s", (int)source);
        cmd.Parameters.AddWithValue("$sid", sourceId);
        return cmd.ExecuteScalar() is not null;
    }

    private List<Item> Query(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);

        var items = new List<Item>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            items.Add(new Item
            {
                Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
                Source = (Source)r.GetInt32(r.GetOrdinal("source")),
                SourceId = r.GetString(r.GetOrdinal("source_id")),
                Stage = (Stage)r.GetInt32(r.GetOrdinal("stage")),
                Classification = new Classification(
                    (Priority)r.GetInt32(r.GetOrdinal("priority")),
                    (Interrupt)r.GetInt32(r.GetOrdinal("interrupt")),
                    (ClassifiedBy)r.GetInt32(r.GetOrdinal("classified_by")),
                    Open(r, "reason")!),
                ReceivedUtc = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("received_utc"))),
                DueUtc = Time(r, "due_utc"),
                FireAtUtc = Time(r, "fire_at_utc"),
                PlannedStartUtc = Time(r, "planned_start_utc"),
                AckedAtUtc = Time(r, "acked_at_utc"),
                SnoozedUntilUtc = Time(r, "snoozed_until_utc"),
                LastFiredUtc = Time(r, "last_fired_utc"),
                CalendarEventId = r.IsDBNull(r.GetOrdinal("calendar_event_id")) ? null : r.GetString(r.GetOrdinal("calendar_event_id")),
                IsRead = r.IsDBNull(r.GetOrdinal("is_read")) ? null : r.GetInt64(r.GetOrdinal("is_read")) != 0,
                PlannedMinutes = r.IsDBNull(r.GetOrdinal("planned_minutes")) ? null : r.GetInt32(r.GetOrdinal("planned_minutes")),
                JoinedUtc = Time(r, "joined_utc"),
                CaseUrl = Open(r, "case_url"),
                Note = Open(r, "note"),
                NoteLink = r.IsDBNull(r.GetOrdinal("note_link")) ? null : r.GetString(r.GetOrdinal("note_link")),
                PromoteToCalendar = r.GetInt64(r.GetOrdinal("promote")) != 0,
                CalendarStartUtc = Time(r, "calendar_start_utc"),
                CalendarEndUtc = Time(r, "calendar_end_utc"),
                TitleEdited = r.GetInt64(r.GetOrdinal("title_edited")) != 0,
                WorkStartedUtc = Time(r, "work_started_utc"),
                WorkedMinutes = r.GetInt32(r.GetOrdinal("worked_minutes")),
                Title = Open(r, "title")!,
                Snippet = Open(r, "snippet"),
                From = Open(r, "sender"),
                SourceRef = Open(r, "source_ref"),
                JoinUrl = Open(r, "join_url")
            });
        }
        return items;
    }

    private int Execute(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteNonQuery();
    }

    private object Seal(string? value) => value is null ? DBNull.Value : _protector.Protect(value);

    private string? Open(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : _protector.Unprotect((byte[])r.GetValue(i));
    }

    private static object Ms(DateTimeOffset? t) => t?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value;

    private static DateTimeOffset? Time(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(i));
    }

    public void Dispose() => _db.Dispose();
}
