using System.Globalization;
using rNotice.Core.Items;
using rNotice.Core.Rules;
using rNotice.Core.Scheduling;

namespace rNotice.Agent;

/// <summary>
/// Display formatting. Stored times are UTC; shown times are local first, UTC beside it, always.
/// </summary>
internal static class Display
{
    public static string Time(DateTimeOffset utc)
    {
        var local = utc.ToLocalTime();
        var day = local.Date == DateTime.Today ? "" : local.ToString("ddd d MMM ", CultureInfo.InvariantCulture);
        return $"{day}{local:HH:mm} · {utc.UtcDateTime:HH:mm} UTC";
    }

    public static string Relative(DateTimeOffset utc, DateTimeOffset now)
    {
        var d = utc - now;
        var abs = d.Duration();
        var text = abs.TotalMinutes < 1 ? "now"
            : abs.TotalMinutes < 60 ? $"{(int)abs.TotalMinutes} min"
            : abs.TotalHours < 24 ? $"{abs.TotalHours:0.#} h"
            : $"{(int)abs.TotalDays} d";
        return text == "now" ? text : d > TimeSpan.Zero ? $"in {text}" : $"{text} ago";
    }

    /// <summary>The one line that says when an item matters.</summary>
    public static string When(Item item)
    {
        var now = DateTimeOffset.UtcNow;
        var when = item.DueUtc is { } due
            ? $"{(item.Source == Source.Calendar ? "Starts" : "Due")} {Time(due)} ({Relative(due, now)})"
            : $"Received {Time(item.ReceivedUtc)}";

        return item.PlannedStartUtc is { } planned
            ? $"Planned {Time(planned)} ({Relative(planned, now)}) · {when}"
            : when;
    }

    /// <summary>Group header: Today, Tomorrow, Yesterday, else "Fri 24 Oct".</summary>
    public static string DayLabel(DateOnly day)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return (day.DayNumber - today.DayNumber) switch
        {
            0 => "Today",
            1 => "Tomorrow",
            -1 => "Yesterday",
            _ => day.ToString("ddd d MMM", CultureInfo.InvariantCulture)
        };
    }

    public static string SourceLabel(Source s) => s switch
    {
        Source.Workbook => "Task",
        Source.Calendar => "Meeting",
        Source.Mail => "Email",
        Source.Teams => "Chat",
        _ => s.ToString()
    };

    /// <summary>
    /// <c>@yourdomain.com</c>, once sign-in has said who you are. Colleagues' addresses move to
    /// the end of the line.
    /// </summary>
    public static string? MyDomain { get; set; }
}

public enum CalendarKind
{
    /// <summary>A meeting rNotice tracks: select it, act on it.</summary>
    Meeting,

    /// <summary>A task you planned with rNotice.</summary>
    Task,

    /// <summary>A meeting rNotice doesn't track — blocklisted, done or dismissed. Busy time, nothing more.</summary>
    Busy
}

/// <summary>One block on the Planned calendar.</summary>
public sealed record CalendarEntry(Item? Item, string Title, DateTimeOffset StartUtc, DateTimeOffset EndUtc, CalendarKind Kind, bool AllDay);

/// <summary>What the windows bind to.</summary>
public sealed class ItemView(Item item) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public Item Item { get; } = item;

    /// <summary>
    /// "Starts in 45 s", "Starting now", "Started 3 min ago". Read live; <see cref="Tick"/>
    /// tells bindings to re-read it, so a countdown can update without rebuilding the window's
    /// buttons under the user's mouse.
    /// </summary>
    public string Countdown
    {
        get
        {
            if (Item.DueUtc is not { } start) return "";
            var left = start - DateTimeOffset.UtcNow;
            return left.TotalHours >= 24 ? $"Starts {Display.Time(start)}"
                 : left.TotalMinutes >= 60 ? $"Starts in {(int)left.TotalHours} h {left.Minutes} min"
                 : left.TotalSeconds >= 120 ? $"Starts in {(int)Math.Ceiling(left.TotalMinutes)} min"
                 : left.TotalSeconds > 0 ? $"Starts in {(int)Math.Ceiling(left.TotalSeconds)} s"
                 : left.TotalSeconds > -60 ? "Starting now"
                 : $"Started {(int)-left.TotalMinutes} min ago";
        }
    }

    public void Tick()
    {
        PropertyChanged?.Invoke(this, new(nameof(Countdown)));
        PropertyChanged?.Invoke(this, new(nameof(CanSnoozeMeeting)));
    }

    /// <summary>How long before the start a snoozed meeting alert returns. Set by the alert window.</summary>
    public static int MeetingSnoozeSeconds { get; set; } = 10;

    /// <summary>
    /// A meeting alert can be put off until just before the start — while that's still a few
    /// seconds away. Rechecked every second, so the button goes once there's no time to snooze.
    /// </summary>
    public bool CanSnoozeMeeting =>
        IsMeeting && Item.DueUtc is { } start && DateTimeOffset.UtcNow < start.AddSeconds(-MeetingSnoozeSeconds - 5);

    /// <summary>A meeting's one line: when, both zones, and who organised it.</summary>
    public string MeetingLine => Item.DueUtc is { } start
        ? $"{Display.Time(start)}" + (Item.From is null ? "" : $"  ·  {Item.From}")
        : Item.From ?? "";

    /// <summary>Says where it opens, rather than "open source".</summary>
    public string OpenLabel => Item.Source switch
    {
        Source.Calendar or Source.Mail => "Open in Outlook",
        Source.Workbook => "Open in Excel",
        Source.Teams => "Open in Teams",
        _ => "Open"
    };

    public bool CanSnooze => !IsMeeting;

    /// <summary>rNotice left a note on it — why a plan came back to Intake, for instance.</summary>
    public bool HasNote => !string.IsNullOrWhiteSpace(Item.Note);

    // A clash note ends with the meeting's name, which is shown as a link to the meeting.
    private int NoteSplit
    {
        get
        {
            if (Item.NoteLink is null || Item.Note is not { } note) return -1;
            var i = note.IndexOf(PlanConflicts.ClashedWith, StringComparison.Ordinal);
            return i < 0 ? -1 : i + PlanConflicts.ClashedWith.Length;
        }
    }

    public bool HasNoteLink => NoteSplit > 0;
    public bool HasPlainNote => HasNote && !HasNoteLink;
    public string NoteLead => HasNoteLink ? Item.Note![..NoteSplit] : Item.Note ?? "";
    public string NoteLinkText => HasNoteLink ? Item.Note![NoteSplit..] : "";

    /// <summary>The Salesforce case number in the subject or excerpt, if there is one.</summary>
    public string? CaseNumber => CaseLinks.FindCaseNumber(Item.Title) ?? CaseLinks.FindCaseNumber(Item.Snippet);

    /// <summary>A case number with a Lightning link found for it.</summary>
    public bool HasCaseLink => !string.IsNullOrEmpty(Item.CaseUrl) && CaseNumber is not null;

    // The title split around the case number, so the list can show the number itself as the
    // link — inline, as the detail pane does, without a line of its own.
    private int CaseAt => HasCaseLink ? Item.Title.IndexOf(CaseNumber!, StringComparison.Ordinal) : -1;

    public bool HasCaseInTitle => CaseAt >= 0;
    public bool HasPlainTitle => !HasCaseInTitle;
    public string TitleLead => HasCaseInTitle ? Item.Title[..CaseAt] : Item.Title;
    public string TitleTail => HasCaseInTitle ? Item.Title[(CaseAt + CaseNumber!.Length)..] : "";
    public Guid Id => Item.Id;
    public string Priority => Item.Classification.Priority.ToString();
    public bool IsMeeting => Item.Source == Source.Calendar;

    /// <summary>
    /// What the badge says. Every meeting is P1, so for meetings the badge carries the local
    /// start time instead — the thing that actually tells them apart.
    /// </summary>
    public string Badge => IsMeeting && Item.DueUtc is { } start ? $"{start.ToLocalTime():HH:mm}" : Priority;

    /// <summary>The group this item sits under in the queue.</summary>
    public string Day => Display.DayLabel(QueueOrder.DayOf(Item, TimeZoneInfo.Local));
    public string Title => Item.Title;
    /// <summary>
    /// Who, then when, then the rest:
    /// <c>Email from: Andreea Oncescu - Received 13:32 · 19:32 UTC - andreea.oncescu@… - read in Outlook</c>
    /// for a colleague; <c>Email from: Alice &lt;alice@contoso.com&gt; - Received …</c> for anyone else.
    /// Same shape for every source, so the name is always at the left.
    /// </summary>
    public string Meta
    {
        get
        {
            var (sender, trailing) = Senders.Describe(Item.From, Display.MyDomain);
            var kind = Display.SourceLabel(Item.Source);
            var parts = new List<string> { sender is null ? kind : $"{kind} from: {sender}" };
            if (MeetingState == "joined") parts.Add($"Joined {Display.Time(Item.JoinedUtc!.Value)}");
            else if (MeetingState == "live") parts.Add("In progress, not joined through rNotice");
            parts.Add(Display.When(Item));
            if (Item.CalendarEventId is not null && Item.Source != Source.Calendar) parts.Add("on your Outlook calendar");
            if (trailing is not null) parts.Add(trailing);
            if (Item.IsRead is { } read) parts.Add(read ? "read in Outlook" : "unread");
            return string.Join("  -  ", parts);
        }
    }

    /// <summary>
    /// For a meeting that has started: "joined" if you clicked Join in rNotice, "live" if not.
    /// Empty before it starts and for anything that isn't a meeting. rNotice can't see a join
    /// made straight from Teams or Outlook, so "live" means "not joined through rNotice".
    /// </summary>
    public string MeetingState =>
        !IsMeeting || Item.DueUtc is not { } start || start > DateTimeOffset.UtcNow ? ""
        : Item.JoinedUtc is null ? "live" : "joined";

    /// <summary>Unread mail is bold in the queue, as in Outlook. Read mail says so.</summary>
    public bool IsUnread => Item.IsRead == false;
    public string Reason => $"{Item.Classification.Priority}/{Item.Classification.Interrupt.ToString().ToLowerInvariant()} — {Item.Classification.Reason}";
    public string? Snippet => Item.Snippet;
    public bool HasSnippet => !string.IsNullOrWhiteSpace(Item.Snippet);
    public bool HasSource => Item.SourceRef is not null;
    public bool HasJoin => Item.JoinUrl is not null;
    public bool CanPlan => Item.Stage == Stage.Intake;
    public bool IsSnoozed => Item.SnoozedUntilUtc > DateTimeOffset.UtcNow;
    public string Snoozed => IsSnoozed ? $"Snoozed until {Display.Time(Item.SnoozedUntilUtc!.Value)}" : "";
}
