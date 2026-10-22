using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using rNotice.Core.Items;

namespace rNotice.Agent;

/// <summary>
/// "When will you do it?" — opened by P, the right-click menu, dragging from Intake to
/// Planned, and the alert's Plan button. Enter plans without a time; Esc cancels.
///
/// The choice is local: a planned start time becomes a reminder from rNotice. Putting it on
/// the calendar is promotion, step 8.
/// </summary>
public partial class PlanDialog : Window
{
    private readonly Dictionary<Button, DateTimeOffset> _presets = [];

    /// <summary>Set when the user chose to plan. <see cref="StartUtc"/> null means no time.</summary>
    public bool Planned { get; private set; }
    public DateTimeOffset? StartUtc { get; private set; }

    internal PlanDialog(Item item)
    {
        InitializeComponent();
        var view = new ItemView(item);
        ItemTitle.Text = view.Title;
        ItemMeta.Text = view.Meta;

        NoTime.Content = item.DueUtc is { } due
            ? $"No time — remind me as usual before it's due ({Display.Time(due)})"
            : "No time — just mark it planned";

        var now = DateTimeOffset.Now;
        AddPreset(InOneHour, "In 1 hour", now.AddHours(1));
        AddPreset(InThreeHours, "In 3 hours", now.AddHours(3));
        AddPreset(TomorrowMorning, "Tomorrow morning",
            new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(9), TimeZoneInfo.Local.GetUtcOffset(DateTime.Today.AddDays(1).AddHours(9))));

        CustomDate.SelectedDate = DateTime.Today;
        CustomTime.Text = now.AddHours(1).ToString("HH:00", CultureInfo.InvariantCulture);
    }

    private void AddPreset(Button button, string label, DateTimeOffset local)
    {
        var utc = local.ToUniversalTime();
        _presets[button] = utc;
        button.Content = $"{label} — {Display.Time(utc)}";
    }

    private void OnNoTime(object sender, RoutedEventArgs e) => Finish(null);

    private void OnPreset(object sender, RoutedEventArgs e) => Finish(_presets[(Button)sender]);

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
