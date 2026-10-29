using rNotice.Core.Items;
using rNotice.Core.Rules;
using rNotice.Core.Sources;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class CalendarIntakeTests
{
    private readonly Intake _intake = new(new RulesOptions { MeetingBlocklist = ["Standup", "/^focus/"] }, User);

    [Fact]
    public void Meeting_is_P1_takeover_firing_a_minute_before_start()
    {
        var e = Event();
        var item = _intake.FromCalendar(e, Now).Item!;

        Assert.Equal(Priority.P1, item.Classification.Priority);
        Assert.Equal(Interrupt.Takeover, item.Classification.Interrupt);
        Assert.Equal(e.StartUtc.AddSeconds(-60), item.FireAtUtc);
        Assert.Equal(e.StartUtc, item.DueUtc);
        Assert.Equal(e.JoinUrl, item.JoinUrl);
    }

    [Fact]
    public void A_meetings_description_is_its_snippet() =>
        Assert.Equal("Case list", _intake.FromCalendar(Event() with { Body = "Case list" }, Now).Item!.Snippet);

    [Fact]
    public void A_meeting_you_organised_has_no_from_and_others_name_the_organiser()
    {
        Assert.Null(_intake.FromCalendar(Event() with { IsOrganizer = true }, Now).Item!.From);
        Assert.Equal("Alice", _intake.FromCalendar(Event(), Now).Item!.From);
    }

    [Fact]
    public void Meeting_enters_as_Planned_not_Intake() =>
        Assert.Equal(Stage.Planned, _intake.FromCalendar(Event(), Now).Item!.Stage);

    [Theory]
    [InlineData("Daily standup")]      // substring, any case
    [InlineData("Focus time")]         // regex anchored at start
    public void Blocklisted_meetings_are_skipped(string subject) =>
        Assert.True(_intake.FromCalendar(Event(subject), Now).Skipped);

    [Fact]
    public void Regex_pattern_only_matches_where_it_says() =>
        Assert.False(_intake.FromCalendar(Event("Team focus review"), Now).Skipped);

    [Fact]
    public void Events_rNotice_promoted_are_skipped() =>
        Assert.True(_intake.FromCalendar(Event(categories: ["rNotice"]), Now).Skipped);

    [Fact]
    public void Cancelled_all_day_and_finished_events_are_skipped()
    {
        Assert.True(_intake.FromCalendar(Event(cancelled: true), Now).Skipped);
        Assert.True(_intake.FromCalendar(Event(allDay: true), Now).Skipped);
        Assert.True(_intake.FromCalendar(Event(start: Now.AddHours(-1), minutes: 30), Now).Skipped);
    }

    [Fact]
    public void A_meeting_already_under_way_is_still_an_item() =>
        Assert.False(_intake.FromCalendar(Event(start: Now.AddMinutes(-5), minutes: 30), Now).Skipped);
}

public class MailIntakeTests
{
    private readonly Intake _intake = new(new RulesOptions
    {
        IgnoreSenders = ["@builds.bravurasecurity.com", "noreply@github.com"],
        VipSenders = ["ceo@bravurasecurity.com"]
    }, User);

    private static readonly IReadOnlyDictionary<string, DateTimeOffset> NoThreads = new Dictionary<string, DateTimeOffset>();

    // You first sent in the conversation an hour before the test mail arrived.
    private static IReadOnlyDictionary<string, DateTimeOffset> SentIn(string conv) =>
        new Dictionary<string, DateTimeOffset> { [conv] = Now.AddHours(-1) };

    [Fact]
    public void Reply_on_a_thread_you_sent_in_is_P1_toast()
    {
        var c = _intake.FromMail(Mail(conversationId: "conv-9"), SentIn("conv-9")).Item!.Classification;

        Assert.Equal((Priority.P1, Interrupt.Toast, ClassifiedBy.Rule), (c.Priority, c.Interrupt, c.By));
    }

    [Fact]
    public void Mail_from_before_your_first_send_is_not_a_reply()
    {
        // The message you replied to shares the conversationId. 9 of 26 spike false alarms.
        var earlier = Mail() with { ReceivedUtc = Now.AddHours(-2) };
        Assert.Equal(ClassifiedBy.Default, _intake.FromMail(earlier, SentIn("conv-1")).Item!.Classification.By);
    }

    [Fact]
    public void Meeting_messages_are_skipped_even_on_a_thread_you_sent_in()
    {
        // "Accepted:" responses to your invites. 17 of 26 spike false alarms.
        var accepted = Mail() with { IsMeetingMessage = true };
        Assert.True(_intake.FromMail(accepted, SentIn("conv-1")).Skipped);
    }

    [Fact]
    public void Vip_sender_is_P1_alert()
    {
        var c = _intake.FromMail(Mail(from: "CEO@bravurasecurity.com"), NoThreads).Item!.Classification;
        Assert.Equal((Priority.P1, Interrupt.Alert), (c.Priority, c.Interrupt));
    }

    [Fact]
    public void Reply_rule_wins_over_vip()
    {
        var c = _intake.FromMail(Mail(from: "ceo@bravurasecurity.com"), SentIn("conv-1")).Item!.Classification;
        Assert.Equal(Interrupt.Toast, c.Interrupt);
    }

    [Fact]
    public void Unmatched_mail_gets_the_fallback_for_the_model()
    {
        var c = _intake.FromMail(Mail(), NoThreads).Item!.Classification;
        Assert.Equal((Priority.P3, Interrupt.Silent, ClassifiedBy.Default), (c.Priority, c.Interrupt, c.By));
    }

    [Fact]
    public void Sole_To_recipient_steps_priority_up_and_says_so()
    {
        var c = _intake.FromMail(Mail(to: ["robert.sinclair@bravurasecurity.com"]), NoThreads).Item!.Classification;

        Assert.Equal(Priority.P2, c.Priority);
        Assert.Contains("only To recipient", c.Reason);
    }

    [Fact]
    public void Sole_To_recipient_leaves_P1_alone()
    {
        var c = _intake.FromMail(Mail(to: ["robert.sinclair@bravurasecurity.com"]), SentIn("conv-1"))
                       .Item!.Classification;

        Assert.Equal(Priority.P1, c.Priority);
        Assert.DoesNotContain("only To", c.Reason);
    }

    [Theory]
    [InlineData("robert.sinclair@bravurasecurity.com")]   // yourself
    [InlineData("pipeline@builds.bravurasecurity.com")]   // ignored domain
    [InlineData("NOREPLY@github.com")]                    // ignored address, any case
    public void Skipped_senders(string from) =>
        Assert.True(_intake.FromMail(Mail(from: from), NoThreads).Skipped);

    [Fact]
    public void Domain_entry_does_not_match_a_lookalike_domain() =>
        Assert.False(_intake.FromMail(Mail(from: "x@evilbuilds.bravurasecurity.com.attacker.net"), NoThreads).Skipped);

    [Fact]
    public void Snippet_is_truncated_never_the_full_body()
    {
        var item = _intake.FromMail(Mail(preview: new string('x', 2000)), NoThreads).Item!;
        Assert.True(item.Snippet!.Length <= 501);
    }
}

public class TeamsIntakeTests
{
    private readonly Intake _intake = new(new RulesOptions { WatchedChatIds = ["chat-watched"] }, User);

    [Fact]
    public void Mention_is_P2_alert()
    {
        var c = _intake.FromTeams(Chat(mentions: ["user-me"])).Item!.Classification;
        Assert.Equal((Priority.P2, Interrupt.Alert, ClassifiedBy.Rule), (c.Priority, c.Interrupt, c.By));
    }

    [Fact]
    public void One_on_one_without_mention_goes_to_the_model()
    {
        var c = _intake.FromTeams(Chat(type: ChatType.OneOnOne)).Item!.Classification;
        Assert.Equal((Priority.P3, Interrupt.Silent, ClassifiedBy.Default), (c.Priority, c.Interrupt, c.By));
    }

    [Fact]
    public void Skips_system_own_and_unwatched()
    {
        Assert.True(_intake.FromTeams(Chat(system: true)).Skipped);
        Assert.True(_intake.FromTeams(Chat(from: "user-me")).Skipped);
        Assert.True(_intake.FromTeams(Chat(chatId: "chat-other")).Skipped);
    }

    [Fact]
    public void Title_is_the_first_line_of_the_message() =>
        Assert.Equal("Line one", _intake.FromTeams(Chat(mentions: ["user-me"], text: "Line one\nLine two")).Item!.Title);
}

public class WorkbookIntakeTests
{
    private readonly Intake _intake = new(new RulesOptions { DefaultLeadMinutes = 15, HorizonDays = 14 }, User);

    [Fact]
    public void Blank_columns_default_to_P3_alert_with_default_lead()
    {
        var item = _intake.FromWorkbook(Row(), Now).Item!;

        Assert.Equal((Priority.P3, Interrupt.Alert), (item.Classification.Priority, item.Classification.Interrupt));
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 15, 45, 0, TimeSpan.Zero), item.FireAtUtc);
        Assert.Equal("row-1", item.SourceId);
    }

    [Fact]
    public void Row_columns_decide_classification_and_lead()
    {
        var item = _intake.FromWorkbook(Row(priority: "p1", interrupt: "Takeover", lead: 60.0), Now).Item!;

        Assert.Equal((Priority.P1, Interrupt.Takeover), (item.Classification.Priority, item.Classification.Interrupt));
        Assert.Equal(item.DueUtc!.Value.AddMinutes(-60), item.FireAtUtc);
    }

    [Fact]
    public void Unrecognised_values_fall_back_and_the_reason_says_so()
    {
        var c = _intake.FromWorkbook(Row(priority: "urgent", interrupt: "7"), Now).Item!.Classification;

        Assert.Equal((Priority.P3, Interrupt.Alert), (c.Priority, c.Interrupt));
        Assert.Contains("Priority 'urgent' not recognised", c.Reason);
        Assert.Contains("Interrupt '7' not recognised", c.Reason);
    }

    [Fact]
    public void Blank_status_counts_as_pending() =>
        Assert.False(_intake.FromWorkbook(Row(status: null), Now).Skipped);

    [Theory]
    [InlineData("Acked")]
    [InlineData("Done")]
    [InlineData("Cancelled")]
    public void Non_pending_rows_are_skipped(string status) =>
        Assert.True(_intake.FromWorkbook(Row(status: status), Now).Skipped);

    [Fact]
    public void Excel_date_serial_is_rejected_with_a_fix()
    {
        var result = _intake.FromWorkbook(Row(due: 46316.5), Now);
        Assert.True(result.Skipped);
        Assert.Contains("Format the column as text", result.SkipReason);
    }

    [Fact]
    public void Due_without_offset_is_read_as_UTC() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 16, 0, 0, TimeSpan.Zero),
                     _intake.FromWorkbook(Row(due: "2026-10-21T16:00:00"), Now).Item!.DueUtc);

    [Fact]
    public void Due_with_offset_is_converted_to_UTC() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 22, 0, 0, TimeSpan.Zero),
                     _intake.FromWorkbook(Row(due: "2026-10-21T16:00:00-06:00"), Now).Item!.DueUtc);

    [Fact]
    public void Missing_id_unparseable_due_and_beyond_horizon_are_skipped()
    {
        Assert.True(_intake.FromWorkbook(Row(id: " "), Now).Skipped);
        Assert.True(_intake.FromWorkbook(Row(due: "next Tuesday"), Now).Skipped);
        Assert.True(_intake.FromWorkbook(Row(due: "2026-12-01T00:00:00Z"), Now).Skipped);
    }

    [Fact]
    public void Client_prefixes_the_title() =>
        Assert.Equal("Contoso: Rotate service account", _intake.FromWorkbook(Row(client: "Contoso"), Now).Item!.Title);
}
