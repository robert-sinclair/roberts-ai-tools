using rNotice.Core.Items;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class ItemTests
{
    [Fact]
    public void Classification_refuses_an_empty_reason() =>
        Assert.Throws<ArgumentException>(() => new Classification(Priority.P1, Interrupt.Alert, ClassifiedBy.Rule, " "));

    [Theory]
    [InlineData(Priority.P4, Priority.P3)]
    [InlineData(Priority.P2, Priority.P1)]
    [InlineData(Priority.P1, Priority.P1)]
    public void StepUp(Priority from, Priority to) => Assert.Equal(to, from.StepUp());

    [Fact]
    public void Merge_takes_source_fields_and_keeps_user_state()
    {
        var stored = Item(stage: Stage.Planned) with
        {
            AckedAtUtc = Now, SnoozedUntilUtc = Now.AddHours(1), CalendarEventId = "evt", PlannedStartUtc = Now
        };
        var polled = Item() with { Title = "Renamed", Id = Guid.NewGuid() };

        var merged = stored.MergeFrom(polled);

        Assert.Equal("Renamed", merged.Title);
        Assert.Equal(stored.Id, merged.Id);
        Assert.Equal(Stage.Planned, merged.Stage);
        Assert.Equal(stored.AckedAtUtc, merged.AckedAtUtc);
        Assert.Equal(stored.SnoozedUntilUtc, merged.SnoozedUntilUtc);
        Assert.Equal("evt", merged.CalendarEventId);
        Assert.Equal(stored.PlannedStartUtc, merged.PlannedStartUtc);
    }

    [Fact]
    public void Merge_rearms_when_the_fire_time_moves()
    {
        var stored = Item() with { LastFiredUtc = Now };

        Assert.Equal(Now, stored.MergeFrom(stored with { Title = "x" }).LastFiredUtc);
        Assert.Null(stored.MergeFrom(stored with { FireAtUtc = Now.AddHours(1) }).LastFiredUtc);
    }

    [Fact]
    public void Merge_keeps_user_and_model_classifications_but_refreshes_rules()
    {
        var polled = Item(Interrupt.Toast, Priority.P1);

        var user = Item().Reclassify(Priority.P4, Interrupt.Silent, "Never urgent");
        Assert.Equal(ClassifiedBy.User, user.MergeFrom(polled).Classification.By);

        var model = Item() with { Classification = new(Priority.P2, Interrupt.Alert, ClassifiedBy.Model, "Asks a question") };
        Assert.Equal(ClassifiedBy.Model, model.MergeFrom(polled).Classification.By);

        var rule = Item();
        Assert.Equal(Interrupt.Toast, rule.MergeFrom(polled).Classification.Interrupt);
    }

    [Fact]
    public void Merge_refuses_a_different_source_item() =>
        Assert.Throws<ArgumentException>(() => Item(sourceId: "a").MergeFrom(Item(sourceId: "b")));
}
