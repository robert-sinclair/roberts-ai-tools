using System.Text.Json;
using rNotice.Core.Items;
using rNotice.Core.Routines;
using rNotice.Core.Rules;
using rNotice.Core.Store;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class CaseLookupTests
{
    private static Item Meeting(string title, string? caseUrl = null) => Item(stage: Stage.Planned) with
    {
        Source = Source.Calendar, Title = title, CaseUrl = caseUrl
    };

    private static Item Mail(string? caseUrl) => Item() with
    {
        Source = Source.Mail, Title = "Customer replied: Case 00038028", CaseUrl = caseUrl
    };

    [Fact]
    public void A_meeting_called_by_its_bare_case_number_is_about_that_case() =>
        Assert.Equal("00038033", Routine.CaseNumberOf(Meeting("00038033")));

    [Fact]
    public void A_meeting_with_no_case_number_needs_no_link() =>
        Assert.False(Routine.NeedsCaseLink(Meeting("Virtual Office Hours")));

    [Fact]
    public void A_meeting_about_a_case_with_no_link_needs_one_and_with_a_link_doesnt()
    {
        Assert.True(Routine.NeedsCaseLink(Meeting("00038033")));
        Assert.False(Routine.NeedsCaseLink(Meeting("00038033", "https://bravurasecurity.lightning.force.com/lightning/r/Case/500N300001MewUsIAJ/view")));
    }

    [Fact]
    public void Mail_needs_a_lookup_only_after_its_own_message_had_no_link()
    {
        Assert.False(Routine.NeedsCaseLink(Mail(null)));   // not read for a link yet
        Assert.True(Routine.NeedsCaseLink(Mail("")));      // read, none found
    }

    [Fact]
    public void A_closed_item_needs_no_link() =>
        Assert.False(Routine.NeedsCaseLink(Meeting("00038033") with { Stage = Stage.Done }));

    [Fact]
    public void The_lookup_request_names_its_kind_and_numbers()
    {
        var id = Guid.NewGuid();
        using var doc = JsonDocument.Parse(Routine.CaseLookupText(id, "efficiency", ["00038033", "00038079"]));

        Assert.Equal(("caseLookup", id.ToString()), (doc.RootElement.GetProperty("kind").GetString(), doc.RootElement.GetProperty("requestId").GetString()));
        Assert.Equal(["00038033", "00038079"], doc.RootElement.GetProperty("caseNumbers").EnumerateArray().Select(n => n.GetString()));
    }

    [Fact]
    public void A_lookup_result_gives_18_character_ids_and_skips_anything_malformed()
    {
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new
        {
            requestId = id.ToString(), kind = "caseLookup",
            cases = new object[]
            {
                new { caseNumber = "00038033", caseId = "500N300001Mapmw" },        // 15 → 18
                new { caseNumber = "00038079", caseId = "500N300001MewUsIAJ" },
                new { caseNumber = "00038080", caseId = "001N300001MewUsIAJ" },     // an Account, not a Case
                new { caseNumber = "not a case", caseId = "500N300001MewUsIAJ" }
            }
        });

        var result = Routine.ParseCaseLookup(json, out _)!;

        Assert.Equal(id, result.RequestId);
        Assert.Equal([("00038033", "500N300001MapmwIAB"), ("00038079", "500N300001MewUsIAJ")], result.Found);
    }

    [Fact]
    public void A_lookup_result_without_a_request_id_is_rejected()
    {
        Assert.Null(Routine.ParseCaseLookup("""{"cases":[]}""", out var error));
        Assert.Equal("no requestId", error);
    }
}

public sealed class CaseMapStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rnotice-cases-{Guid.NewGuid():N}.db");
    private readonly ItemStore _store;

    public CaseMapStoreTests() => _store = new ItemStore(_path, new DpapiFieldProtector());

    public void Dispose()
    {
        _store.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void A_case_is_remembered_and_reports_only_real_changes()
    {
        Assert.True(_store.RememberCase("00038033", "500N300001MewUsIAJ", Now));
        Assert.False(_store.RememberCase("00038033", "500N300001MewUsIAJ", Now.AddMinutes(1)));
        Assert.Equal(("500N300001MewUsIAJ", 1), (_store.CaseRecordId("00038033"), _store.CaseCount));
        Assert.Null(_store.CaseRecordId("00038079"));
    }

    [Fact]
    public void Cases_not_referenced_for_six_months_drop_off()
    {
        _store.RememberCase("00038033", "500N300001MewUsIAJ", Now);
        _store.RememberCase("00038079", "500N300001MapmwIAB", Now);
        _store.TouchCase("00038079", Now.AddDays(100));   // still referenced

        var dropped = _store.ForgetCases(Now.AddDays(182) - Routine.CaseMapKeep.Add(TimeSpan.FromDays(-1)));

        Assert.Equal(1, dropped);
        Assert.Null(_store.CaseRecordId("00038033"));
        Assert.NotNull(_store.CaseRecordId("00038079"));
    }

    [Fact]
    public void The_map_survives_a_restart()
    {
        _store.RememberCase("00038033", "500N300001MewUsIAJ", Now);
        _store.Dispose();
        using var again = new ItemStore(_path, new DpapiFieldProtector());
        Assert.Equal("500N300001MewUsIAJ", again.CaseRecordId("00038033"));
    }
}

public class CaseCsvTests
{
    [Fact]
    public void Reads_rows_from_a_whole_chat_reply_ignoring_fences_headers_and_words()
    {
        var reply = """
            Here are the cases changed in the last 6 months:

            ```csv
            CaseNumber,CaseId,LastModifiedDate
            00038033,500N300001MewUsIAJ,2026-10-30T15:04:00Z
            00038001,500N300001Mapmw,2026-10-20T09:00:00Z
            ```

            ```
            CaseNumber,CaseId,LastModifiedDate
            "00037990","500N300001JH9jUIAT","2026-06-01T12:00:00Z"
            ```
            Total: 3 rows.
            """;

        var result = CaseCsv.Parse(reply);

        Assert.Equal(0, result.Rejected);
        Assert.Equal(
            [("00038033", "500N300001MewUsIAJ"), ("00038001", "500N300001MapmwIAB"), ("00037990", "500N300001JH9jUIAT")],
            result.Rows.Select(r => (r.CaseNumber, r.RecordId)));
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 15, 4, 0, TimeSpan.Zero), result.Rows[0].LastModifiedUtc);
    }

    [Fact]
    public void Bad_rows_are_counted_and_a_repeated_case_keeps_its_latest_change()
    {
        var result = CaseCsv.Parse("""
            0003803,500N300001MewUsIAJ,2026-10-30T15:04:00Z
            00038033,001N300001MewUsIAJ,2026-10-30T15:04:00Z
            00038033,500N300001MewUsIAJ,2026-09-01T00:00:00Z
            00038033,500N300001MewUsIAJ,2026-10-01T00:00:00Z
            """);

        Assert.Equal(2, result.Rejected);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), Assert.Single(result.Rows).LastModifiedUtc);
    }

    [Fact]
    public void The_prompt_asks_for_exactly_the_columns_the_parser_reads() =>
        Assert.Contains("CaseNumber,CaseId,LastModifiedDate", CaseCsv.Prompt);
}

public sealed class CaseImportStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rnotice-import-{Guid.NewGuid():N}.db");
    private readonly ItemStore _store;

    public CaseImportStoreTests() => _store = new ItemStore(_path, new DpapiFieldProtector());

    public void Dispose()
    {
        _store.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void An_import_counts_from_the_cases_last_change_and_keeps_the_later_time()
    {
        Assert.Equal("new", _store.ImportCase("00038033", "500N300001MewUsIAJ", Now.AddDays(-150), Now));
        Assert.Equal("same", _store.ImportCase("00038033", "500N300001MewUsIAJ", Now.AddDays(-170), Now));

        // Last change 150 days ago: still there 30 days on, gone after 33.
        Assert.Equal(0, _store.ForgetCases(Now.AddDays(30) - Routine.CaseMapKeep));
        Assert.Equal(1, _store.ForgetCases(Now.AddDays(33) - Routine.CaseMapKeep));
    }
}
