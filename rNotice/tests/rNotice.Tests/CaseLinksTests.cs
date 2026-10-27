using rNotice.Core.Rules;

namespace rNotice.Tests;

public class CaseLinksTests
{
    [Theory]
    [InlineData("Case Assignment: 00038001 - Help with Role Assignments", "00038001")]
    [InlineData("Needs attention: Case 00037339 — no movement in 4 days", "00037339")]
    [InlineData("RE: case #00012345 follow up", "00012345")]
    [InlineData("Release 12.10.00038001 notes", null)]
    [InlineData(null, null)]
    public void Case_number_from_a_subject(string? subject, string? expected) =>
        Assert.Equal(expected, CaseLinks.FindCaseNumber(subject));

    [Fact]
    public void Fifteen_character_ID_converts_to_the_eighteen_in_the_Lightning_link() =>
        // From Robert's example: case 00038001.
        Assert.Equal("500N300001MapmwIAB", CaseLinks.To18("500N300001Mapmw"));

    [Fact]
    public void Record_ID_from_the_Case_Assignment_email_link()
    {
        // The href in the real Case Assignment notification.
        var html = """<p>View the Case: <a href="https://bravurasecurity.my.salesforce.com/500N300001Mapmw" style="color:#0176D3">00038001</a></p>""";
        Assert.Equal("500N300001MapmwIAB", CaseLinks.FindRecordId(html));
    }

    [Fact]
    public void Record_ID_from_the_portal_link_in_a_Needs_attention_email() =>
        Assert.Equal("500N300001JH9jUIAT",
            CaseLinks.FindRecordId("Open the case: https://bravurasecurity.my.salesforce.com/500N300001JH9jUIAT (Aut"));

    [Fact]
    public void Record_ID_from_the_customer_portal_link_in_a_Customer_replied_email() =>
        // Case 00038028, 2026-10-26: the only link in the message is the portal's.
        Assert.Equal("500N300001MewUsIAJ",
            CaseLinks.FindRecordId("Open the case: https://bravurasecurity.my.site.com/portal/500N300001MewUsIAJ"));

    [Fact]
    public void A_record_ID_on_some_other_site_is_not_a_case() =>
        Assert.Null(CaseLinks.FindRecordId("https://example.com/portal/500N300001MewUsIAJ"));

    [Fact]
    public void Record_ID_from_a_Lightning_link() =>
        Assert.Equal("500N300001JH9jUIAT",
            CaseLinks.FindRecordId("https://bravurasecurity.lightning.force.com/lightning/r/Case/500N300001JH9jUIAT/view"));

    [Theory]
    [InlineData("Order 500N300001Mapmw is ready")]                          // not inside a Salesforce link
    [InlineData("https://example.com/500N300001Mapmw")]                     // not Salesforce
    [InlineData("https://bravurasecurity.my.salesforce.com/001N300001Mapmw")] // an Account, not a Case
    public void Nothing_that_isnt_a_case_link(string text) =>
        Assert.Null(CaseLinks.FindRecordId(text));

    [Fact]
    public void Lightning_url_matches_the_internal_site() =>
        Assert.Equal("https://bravurasecurity.lightning.force.com/lightning/r/Case/500N300001MapmwIAB/view",
            CaseLinks.LightningUrl("https://bravurasecurity.lightning.force.com/", "500N300001Mapmw"));
}
