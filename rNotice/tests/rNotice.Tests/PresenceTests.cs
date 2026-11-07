using System.Net;
using rNotice.Core.Graph;
using rNotice.Core.Items;
using rNotice.Core.Rules;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class PresenceTests
{
    private static Item Meeting(int startedMinutesAgo, string id = "evt-1") =>
        Item(Interrupt.Takeover, Priority.P1, due: Now.AddMinutes(-startedMinutesAgo), stage: Stage.Planned, sourceId: id)
            with { Source = Source.Calendar };

    [Theory]
    [InlineData("InACall", true)]
    [InlineData("InAConferenceCall", true)]
    [InlineData("Presenting", true)]
    [InlineData("InAMeeting", false)]   // from the Outlook calendar: shows whether you joined or not
    [InlineData("Available", false)]
    [InlineData("Busy", false)]
    [InlineData(null, false)]
    public void Only_the_call_states_mean_joined(string? activity, bool joined) =>
        Assert.Equal(joined, Presence.IsInACall(activity));

    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(90), MaxWait = TimeSpan.FromSeconds(30);

    private static TakeoverCheck Check(Item meeting, string? activity, DateTimeOffset? readUtc) =>
        Presence.BeforeTakeover(meeting, Now, activity, readUtc, Fresh, MaxWait);

    [Fact]
    public void Already_in_the_call_means_no_takeover()
    {
        Assert.Equal(TakeoverCheck.InACall, Check(Meeting(3), "InACall", Now.AddSeconds(-20)));
        Assert.Equal(TakeoverCheck.Show, Check(Meeting(3), "InAMeeting", Now.AddSeconds(-20)));
    }

    [Fact]
    public void A_meeting_just_started_waits_for_a_fresh_reading_but_not_for_long()
    {
        var justStarted = Meeting(0) with { DueUtc = Now.AddSeconds(-5) };
        Assert.Equal(TakeoverCheck.Wait, Check(justStarted, null, null));
        Assert.Equal(TakeoverCheck.Wait, Check(justStarted, "InACall", Now.AddMinutes(-5)));   // stale
        Assert.Equal(TakeoverCheck.Show, Check(Meeting(1), null, null));                         // no reading: show anyway
    }

    [Fact]
    public void A_meeting_not_started_yet_takes_over_without_asking() =>
        Assert.Equal(TakeoverCheck.Show, Check(Meeting(0) with { DueUtc = Now.AddMinutes(1) }, "InACall", Now));

    [Fact]
    public void In_a_call_during_one_meeting_marks_that_meeting() =>
        Assert.Equal("evt-1", Presence.MeetingJoined([Meeting(5)], Now, "InAConferenceCall")?.SourceId);

    [Fact]
    public void Two_meetings_under_way_are_ambiguous_so_neither_is_marked() =>
        Assert.Null(Presence.MeetingJoined([Meeting(5, "a"), Meeting(10, "b")], Now, "InACall"));

    [Fact]
    public void A_meeting_not_yet_started_or_already_joined_is_not_under_way()
    {
        var future = Meeting(-5);
        var joined = Meeting(5) with { JoinedUtc = Now.AddMinutes(-4) };
        Assert.Empty(Presence.UnderWayNotJoined([future, joined], Now));
    }

    [Fact]
    public void A_joined_meeting_is_still_under_way_so_presence_stays_live() =>
        Assert.Single(Presence.UnderWay([Meeting(5) with { JoinedUtc = Now.AddMinutes(-4) }], Now));

    [Fact]
    public void Done_meetings_and_tasks_are_never_polled_for()
    {
        var done = Meeting(5) with { Stage = Stage.Done };
        var task = Meeting(5, "t") with { Source = Source.Mail };
        Assert.Empty(Presence.UnderWayNotJoined([done, task], Now));
    }

    [Fact]
    public async Task Reads_only_the_activity()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.OK,
            """{ "id": "u1", "availability": "Busy", "activity": "InAConferenceCall", "statusMessage": { "message": { "content": "secret" } } }""");

        Assert.Equal("InAConferenceCall", await new PresenceClient(fake.Client()).GetActivityAsync(default));
        Assert.Equal("https://graph.microsoft.com/v1.0/me/presence", fake.Requests.Single().RequestUri!.ToString());
    }
}
