using rNotice.Core.Items;
using rNotice.Core.Routines;
using rNotice.Core.Scheduling;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class CreatedTests
{
    [Fact]
    public void It_is_an_unplanned_item_of_its_own_source()
    {
        var item = Created.From("Chase the cert renewal", "Contoso prod expires Friday", Now);

        Assert.Equal(Source.Created, item.Source);
        Assert.Equal(Stage.Intake, item.Stage);
        Assert.Equal("Chase the cert renewal", item.Title);
        Assert.Equal("Contoso prod expires Friday", item.Snippet);
        Assert.Null(item.PlannedStartUtc);
        Assert.Null(item.CaseUrl);
    }

    [Fact]
    public void Both_fields_are_trimmed() =>
        Assert.Equal(("Chase it", "Because"),
            (Created.From("  Chase it  ", "  Because  ", Now).Title, Created.From("  Chase it  ", "  Because  ", Now).Snippet));

    [Fact]
    public void An_empty_description_leaves_no_text() =>
        Assert.Null(Created.From("Chase it", "   ", Now).Snippet);

    [Fact]
    public void A_case_number_in_the_title_is_found() =>
        Assert.Equal("00039100", Routine.CaseNumberOf(Created.From("00039100 node question", "", Now)));

    [Fact]
    public void A_case_number_in_the_description_is_found() =>
        Assert.Equal("00038028", Routine.CaseNumberOf(Created.From("Node question", "Raised as Case 00038028", Now)));

    [Fact]
    public void It_needs_a_case_link_until_it_has_one()
    {
        var item = Created.From("00039100 node question", "", Now);
        Assert.True(Routine.NeedsCaseLink(item));
        Assert.False(Routine.NeedsCaseLink(item with { CaseUrl = "https://x/lightning/r/Case/500N300001MapmwIAB/view" }));
    }
}
