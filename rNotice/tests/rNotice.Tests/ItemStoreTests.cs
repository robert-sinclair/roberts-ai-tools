using System.Text;
using rNotice.Core.Items;
using rNotice.Core.Store;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public sealed class ItemStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rnotice-test-{Guid.NewGuid():N}.db");
    private readonly ItemStore _store;

    public ItemStoreTests() => _store = new ItemStore(_path, new DpapiFieldProtector());

    public void Dispose()
    {
        _store.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private static Item Full() => Item(Interrupt.Takeover, Priority.P1, due: Now.AddMinutes(1)) with
    {
        Title = "Contoso prod cutover",
        Snippet = "Window opens at 14:00 UTC",
        From = "Alice <alice@contoso.com>",
        SourceRef = "https://outlook.office.com/x",
        JoinUrl = "https://teams.microsoft.com/l/meetup-join/x",
        CalendarEventId = "evt-9",
        SnoozedUntilUtc = Now.AddMinutes(3),
        IsRead = true,
        PlannedStartUtc = Now.AddHours(2),
        PlannedMinutes = 45
    };

    [Fact]
    public void Round_trips_every_field()
    {
        var item = Full();
        _store.Save(item, Now);

        Assert.Equal(item, _store.Get(item.Id));
    }

    [Fact]
    public void Content_is_not_readable_in_the_file()
    {
        _store.Save(Full(), Now);
        _store.Dispose();

        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(_path)) +
                  (File.Exists(_path + "-wal") ? Encoding.UTF8.GetString(File.ReadAllBytes(_path + "-wal")) : "");

        Assert.DoesNotContain("Contoso", raw);
        Assert.DoesNotContain("alice@contoso.com", raw);
        Assert.DoesNotContain("14:00 UTC", raw);
    }

    [Fact]
    public void Another_protector_cannot_read_the_content()
    {
        _store.Save(Full(), Now);
        using var other = new ItemStore(_path, new ReversingProtector());

        Assert.ThrowsAny<Exception>(() => other.List());
    }

    [Fact]
    public void Ingest_inserts_then_merges_keeping_user_state()
    {
        var first = Item();
        Assert.Equal(IngestOutcome.Inserted, _store.Ingest(first, Now).Outcome);

        _store.Save(first with { Stage = Stage.Planned }, Now);

        var repoll = Item() with { Title = "Renamed" };
        var result = _store.Ingest(repoll, Now);

        Assert.Equal(IngestOutcome.Updated, result.Outcome);
        Assert.Equal(first.Id, result.Item!.Id);
        Assert.Equal(Stage.Planned, _store.Get(first.Id)!.Stage);
        Assert.Equal("Renamed", _store.Get(first.Id)!.Title);
        Assert.Single(_store.List());
    }

    [Fact]
    public void Dismissed_items_are_not_reingested_even_after_purge()
    {
        var item = Item();
        _store.Ingest(item, Now);
        _store.Save(item with { Stage = Stage.Dismissed }, Now);

        Assert.Equal(IngestOutcome.Ignored, _store.Ingest(Item(), Now).Outcome);

        Assert.Equal(1, _store.Purge(Now.AddDays(31), retentionDays: 30));
        Assert.Empty(_store.List());
        Assert.Equal(IngestOutcome.Ignored, _store.Ingest(Item(), Now.AddDays(31)).Outcome);
    }

    [Fact]
    public void Purge_keeps_open_and_recent_items()
    {
        _store.Save(Item(stage: Stage.Done, sourceId: "old-done"), Now);
        _store.Save(Item(stage: Stage.Intake, sourceId: "old-open"), Now);
        _store.Save(Item(stage: Stage.Done, sourceId: "new-done"), Now.AddDays(20));

        Assert.Equal(1, _store.Purge(Now.AddDays(31), retentionDays: 30));
        Assert.Equal(["new-done", "old-open"], _store.List().Select(i => i.SourceId).Order());
    }

    [Fact]
    public void List_filters_by_stage()
    {
        _store.Save(Item(stage: Stage.Intake, sourceId: "a"), Now);
        _store.Save(Item(stage: Stage.Planned, sourceId: "b"), Now);
        _store.Save(Item(stage: Stage.Done, sourceId: "c"), Now);

        Assert.Equal(2, _store.List(Stage.Intake, Stage.Planned).Count);
        Assert.Equal(3, _store.List().Count);
    }

    [Fact]
    public void A_database_from_before_read_state_is_migrated()
    {
        var old = Path.Combine(Path.GetTempPath(), $"rnotice-old-{Guid.NewGuid():N}.db");
        try
        {
            // The items table as 0.4.1 and earlier created it: no is_read column.
            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={old};Pooling=False"))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE items (
                        id TEXT PRIMARY KEY, source INTEGER NOT NULL, source_id TEXT NOT NULL,
                        stage INTEGER NOT NULL, priority INTEGER NOT NULL, interrupt INTEGER NOT NULL,
                        classified_by INTEGER NOT NULL, received_utc INTEGER NOT NULL, due_utc INTEGER,
                        fire_at_utc INTEGER, planned_start_utc INTEGER, acked_at_utc INTEGER,
                        snoozed_until_utc INTEGER, last_fired_utc INTEGER, calendar_event_id TEXT,
                        updated_utc INTEGER NOT NULL, title BLOB NOT NULL, reason BLOB NOT NULL,
                        snippet BLOB, sender BLOB, source_ref BLOB, join_url BLOB,
                        UNIQUE (source, source_id));
                    """;
                cmd.ExecuteNonQuery();
            }

            using var store = new ItemStore(old, new DpapiFieldProtector());
            var item = Item() with { IsRead = false };
            store.Save(item, Now);

            Assert.False(store.Get(item.Id)!.IsRead);
        }
        finally
        {
            foreach (var f in new[] { old, old + "-wal", old + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    /// <summary>Stands in for "a different key".</summary>
    private sealed class ReversingProtector : IFieldProtector
    {
        public byte[] Protect(string plaintext) => Encoding.UTF8.GetBytes(plaintext).Reverse().ToArray();
        public string Unprotect(byte[] ciphertext) =>
            throw new System.Security.Cryptography.CryptographicException("Not our key");
    }
}
