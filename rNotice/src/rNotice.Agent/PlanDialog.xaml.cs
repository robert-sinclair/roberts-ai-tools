using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using rNotice.Core.Items;

namespace rNotice.Agent;

/// <summary>
/// "How long, and when?" — opened by P, the right-click menu, dragging from Intake to Planned,
/// and the alert's Plan button.
///
/// The suggestions are the next open slots in your work hours for the chosen duration: on the
/// quarter hour, clear of meetings and of other planned work, finishing by the end of the day.
/// Enter takes the first. The choice is local — a reminder from rNotice, not a calendar entry.
/// </summary>
public partial class PlanDialog : Window
{
    private static readonly int[] Durations = [15, 30, 45, 60, 90, 120];

    private readonly Func<int, IReadOnlyList<DateTimeOffset>> _suggest;

    /// <summary>Set when the user chose to plan. <see cref="StartUtc"/> null means no time.</summary>
    public bool Planned { get; private set; }
    public DateTimeOffset? StartUtc { get; private set; }
    public int Minutes => Durations[Math.Max(0, Duration.SelectedIndex)];

    internal PlanDialog(Item item, int defaultMinutes, Func<int, IReadOnlyList<DateTimeOffset>> suggest,
        DateTimeOffset? calendarReadUtc)
    {
        _suggest = suggest;
        InitializeComponent();

        var view = new ItemView(item);
        ItemTitle.Text = view.Title;
        ItemMeta.Text = view.Meta;

        NoTime.Content = item.DueUtc is { } due
            ? $"No time — remind me as usual before it's due ({Display.Time(due)})"
            : "No time — just mark it planned";

        if (calendarReadUtc is null)
        {
            SlotsNote.Text = "Your calendar hasn't been read yet, so these don't allow for meetings.";
            SlotsNote.Visibility = Visibility.Visible;
        }

        CustomDate.SelectedDate = DateTime.Today;
        CustomTime.Text = DateTime.Now.AddHours(1).ToString("HH:00", CultureInfo.InvariantCulture);

        foreach (var m in Durations) Duration.Items.Add(Label(m));
        var start = Array.IndexOf(Durations, defaultMinutes);
        Duration.SelectedIndex = start >= 0 ? start : 1;   // fires OnDurationChanged, which fills the slots
    }

    private static string Label(int minutes) =>
        minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";

    private void OnDurationChanged(object sender, SelectionChangedEventArgs e)
    {
        var minutes = Minutes;
        var slots = _suggest(minutes);

        Slots.Children.Clear();
        SlotsHeading.Text = slots.Count > 0
            ? $"Next open {Label(minutes)} in your work hours"
            : $"No open {Label(minutes)} in your work hours in the next two weeks";

        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var end = slot.AddMinutes(minutes);
            var button = new Button
            {
                Content = $"{Display.Time(slot)}  –  {end.ToLocalTime():HH:mm}",
                IsDefault = i == 0
            };
            button.Click += (_, _) => Finish(slot);
            Slots.Children.Add(button);
        }
        NoTime.IsDefault = slots.Count == 0;
    }

    private void OnNoTime(object sender, RoutedEventArgs e) => Finish(null);

    private void OnCustom(object sender, RoutedEventArgs e)
    {
        if (CustomDate.SelectedDate is not { } date ||
            !TimeOnly.TryParseExact(CustomTime.Text.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            ShowError("Pick a date and type a time as HH:mm, for example 14:30.");
            return;
        }

        var local = date.Date + time.ToTimeSpan();
        var utc = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
        if (utc <= DateTimeOffset.UtcNow)
        {
            ShowError("That time has already passed.");
            return;
        }
        Finish(utc);
    }

    private void ShowError(string message)
    {
        Error.Text = message;
        Error.Visibility = Visibility.Visible;
    }

    private void Finish(DateTimeOffset? startUtc)
    {
        Planned = true;
        StartUtc = startUtc;
        DialogResult = true;
    }
}
