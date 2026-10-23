using Microsoft.Data.Sqlite;
using rNotice.Core.Items;

namespace rNotice.Core.Store;

public enum IngestOutcome
{
    Inserted,
    Updated,

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
/// </summary>
public sealed class ItemStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly IFieldProtector _protector;

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
        """);

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
            return new IngestResult(IngestOutcome.Inserted, incoming);
        }

        if (existing.Stage == Stage.Dismissed)
            return new IngestResult(IngestOutcome.Ignored, null);

        var merged = existing.MergeFrom(incoming);
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
                snoozed_until_utc, last_fired_utc, calendar_event_id, is_read, planned_minutes, joined_utc, updated_utc,
                title, reason, snippet, sender, source_ref, join_url)
            VALUES ($id, $source, $source_id, $stage, $priority, $interrupt, $classified_by,
                $received_utc, $due_utc, $fire_at_utc, $planned_start_utc, $acked_at_utc,
                $snoozed_until_utc, $last_fired_utc, $calendar_event_id, $is_read, $planned_minutes, $joined_utc, $updated_utc,
                $title, $reason, $snippet, $sender, $source_ref, $join_url)
            ON CONFLICT (id) DO UPDATE SET
                stage = excluded.stage, priority = excluded.priority, interrupt = excluded.interrupt,
                classified_by = excluded.classified_by, due_utc = excluded.due_utc,
                fire_at_utc = excluded.fire_at_utc, planned_start_utc = excluded.planned_start_utc,
                acked_at_utc = excluded.acked_at_utc, snoozed_until_utc = excluded.snoozed_until_utc,
                last_fired_utc = excluded.last_fired_utc, calendar_event_id = excluded.calendar_event_id,
                is_read = excluded.is_read, planned_minutes = excluded.planned_minutes, joined_utc = excluded.joined_utc,
                updated_utc = excluded.updated_utc, title = excluded.title, reason = excluded.reason,
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
        p.AddWithValue("$updated_utc", nowUtc.ToUnixTimeMilliseconds());
        p.AddWithValue("$title", _protector.Protect(item.Title));
        p.AddWithValue("$reason", _protector.Protect(item.Classification.Reason));
        p.AddWithValue("$snippet", Seal(item.Snippet));
        p.AddWithValue("$sender", Seal(item.From));
        p.AddWithValue("$source_ref", Seal(item.SourceRef));
        p.AddWithValue("$join_url", Seal(item.JoinUrl));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Removes an item outright, leaving no tombstone. For items whose source thing is gone.</summary>
    public void Delete(Guid id) => Execute("DELETE FROM items WHERE id = $id", ("$id", id.ToString()));

    public Item? Get(Guid id) =>
        Query("SELECT * FROM items WHERE id = $id", ("$id", id.ToString())).SingleOrDefault();

    public Item? GetBySource(Source source, string sourceId) =>
        Query("SELECT * FROM items WHERE source = $s AND source_id = $sid",
              ("$s", (int)source), ("$sid", sourceId)).SingleOrDefault();

    /// <summary>Items in the given stages, or every item if none are given.</summary>
    public IReadOnlyList<Item> List(params Stage[] stages)
    {
        if (stages.Length == 0) return Query("SELECT * FROM items");
        var list = string.Join(",", stages.Select(s => (int)s));
        return Query($"SELECT * FROM items WHERE stage IN ({list})");
    }

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

        tx.Commit();
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
