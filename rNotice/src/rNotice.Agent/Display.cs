using System.Globalization;
using rNotice.Core.Items;

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
        Source.Workbook => "Workbook",
        Source.Calendar => "Meeting",
        Source.Mail => "Mail",
        Source.Teams => "Teams",
        _ => s.ToString()
    };
}

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

    public void Tick() => PropertyChanged?.Invoke(this, new(nameof(Countdown)));

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
    public string Meta => $"{Display.SourceLabel(Item.Source)} · {Display.When(Item)}" +
                          (Item.From is null ? "" : $" · {Item.From}");
    public string Reason => $"{Item.Classification.Priority}/{Item.Classification.Interrupt.ToString().ToLowerInvariant()} — {Item.Classification.Reason}";
    public string? Snippet => Item.Snippet;
    public bool HasSnippet => !string.IsNullOrWhiteSpace(Item.Snippet);
    public bool HasSource => Item.SourceRef is not null;
    public bool HasJoin => Item.JoinUrl is not null;
    public bool CanPlan => Item.Stage == Stage.Intake;
    public bool IsSnoozed => Item.SnoozedUntilUtc > DateTimeOffset.UtcNow;
    public string Snoozed => IsSnoozed ? $"Snoozed until {Display.Time(Item.SnoozedUntilUtc!.Value)}" : "";
}
