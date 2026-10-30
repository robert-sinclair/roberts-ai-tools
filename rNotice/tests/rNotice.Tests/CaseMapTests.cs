using System.Text.Json;
using rNotice.Core.Items;
using rNotice.Core.Routines;
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
