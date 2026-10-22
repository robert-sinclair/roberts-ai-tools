using rNotice.Core.Rules;

namespace rNotice.Tests;

public class SendersTests
{
    [Theory]
    [InlineData("Alice <Alice@Contoso.com>", "alice@contoso.com")]
    [InlineData("alice@contoso.com", "alice@contoso.com")]
    [InlineData("Release Pipeline", null)]
    [InlineData(null, null)]
    public void Address_out_of_From(string? from, string? expected) =>
        Assert.Equal(expected, Senders.AddressOf(from));

    [Theory]
    [InlineData("Andreea Oncescu <andreea.oncescu@bravurasecurity.com>", "Andreea Oncescu", "andreea.oncescu@bravurasecurity.com")]
    [InlineData("Alice <alice@contoso.com>", "Alice <alice@contoso.com>", null)]
    [InlineData("noreply@contoso.com", "noreply@contoso.com", null)]
    [InlineData("Bart Allan", "Bart Allan", null)]
    [InlineData(null, null, null)]
    public void Sender_label_puts_colleagues_by_name_and_others_with_address(string? from, string? label, string? trailing) =>
        Assert.Equal((label, trailing), Senders.Describe(from, "@bravurasecurity.com"));

    [Fact]
    public void Domain_entry() => Assert.Equal("@contoso.com", Senders.DomainEntry("alice@Contoso.com"));

    [Theory]
    [InlineData(" Bob@Contoso.com ", "bob@contoso.com")]
    [InlineData("@Contoso.com", "@contoso.com")]
    [InlineData("bob", null)]
    [InlineData("@com", null)]
    [InlineData("bob@", null)]
    [InlineData("a@b@c.com", null)]
    [InlineData("bob smith@x.com", null)]
    [InlineData("", null)]
    public void Typed_entries_are_normalised_or_refused(string typed, string? expected) =>
        Assert.Equal(expected, Senders.Normalise(typed));

    [Fact]
    public void Adding_is_idempotent_and_case_insensitive()
    {
        IReadOnlyList<string> list = ["alice@x.com"];
        Assert.Same(list, Senders.With(list, "ALICE@x.com"));
        Assert.Equal(["alice@x.com", "bob@x.com"], Senders.With(list, "bob@x.com"));
    }

    [Fact]
    public void Ignored_sender_mail_is_skipped_by_intake()
    {
        var intake = new Intake(new RulesOptions { IgnoreSenders = Senders.With([], "noisy@contoso.com") }, Make.User);
        Assert.True(intake.FromMail(Make.Mail(from: "Noisy@Contoso.com"), new Dictionary<string, DateTimeOffset>()).Skipped);
    }
}
