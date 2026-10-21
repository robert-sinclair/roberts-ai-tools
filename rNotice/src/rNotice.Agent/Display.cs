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
        return item.DueUtc is { } due
            ? $"{(item.Source == Source.Calendar ? "Starts" : "Due")} {Time(due)} ({Relative(due, now)})"
            : $"Received {Time(item.ReceivedUtc)}";
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
public sealed class ItemView(Item item)
{
    public Item Item { get; } = item;
    public Guid Id => Item.Id;
    public string Priority => Item.Classification.Priority.ToString();
    public string Title => Item.Title;
    public string Meta => $"{Display.SourceLabel(Item.Source)} · {Display.When(Item)}" +
                          (Item.From is null ? "" : $" · {Item.From}");
    public string Reason => $"{Item.Classification.Priority}/{Item.Classification.Interrupt.ToString().ToLowerInvariant()} — {Item.Classification.Reason}";
    public string? Snippet => Item.Snippet;
    public bool HasSnippet => !string.IsNullOrWhiteSpace(Item.Snippet);
    public bool HasSource => Item.SourceRef is not null;
    public bool HasJoin => Item.JoinUrl is not null;
    public bool IsSnoozed => Item.SnoozedUntilUtc > DateTimeOffset.UtcNow;
    public string Snoozed => IsSnoozed ? $"Snoozed until {Display.Time(Item.SnoozedUntilUtc!.Value)}" : "";
}
