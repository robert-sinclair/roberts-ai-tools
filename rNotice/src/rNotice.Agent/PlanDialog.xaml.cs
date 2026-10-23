using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using rNotice.Core.Items;
using rNotice.Core.Scheduling;

namespace rNotice.Agent;

/// <summary>
/// "How long, and when?" — opened by P, the right-click menu, dragging from Intake to Planned,
/// and the alert's Plan button.
///
/// Left: a duration and the next open slots for it. Right: a working day in quarter hours —
/// meetings and other plans filled in, the past greyed out — where you can see a suggestion in
/// context or pick your own time by clicking and dragging across free quarter hours. Enter
/// plans what's selected; the first suggestion is selected when the dialog opens.
/// </summary>
public partial class PlanDialog : Window
{
    private static readonly int[] Durations = [15, 30, 45, 60, 90, 120];
    private const double CellHeight = 22;

    private static readonly Brush FreeBrush = Brushes.Transparent;
    private static readonly Brush MeetingBrush = Frozen(0xDC, 0xE7, 0xF5);
    private static readonly Brush PlannedBrush = Frozen(0xFC, 0xE8, 0xC8);
    private static readonly Brush PastBrush = Frozen(0xEE, 0xEE, 0xEE);
    private static readonly Brush SelectedBrush = Frozen(0x2B, 0x6C, 0xB0);
    private static readonly Brush LineBrush = Frozen(0xD8, 0xD8, 0xD8);
    private static readonly Brush HourLineBrush = Frozen(0xB0, 0xB0, 0xB0);

    private readonly Item _item;
    private readonly Func<int, IReadOnlyList<DateTimeOffset>> _suggest;
    private readonly Func<DateOnly, IReadOnlyList<DayCell>> _day;
    private readonly WorkHours _hours;

    private DateOnly _shownDay;
    private IReadOnlyList<DayCell> _cells = [];
    private readonly List<Border> _cellViews = [];

    // The selection, held as times so it survives moving between days and back.
    private DateTimeOffset? _selStart;
    private int _selMinutes;
    private int _anchor = -1;
    private bool _dragging;

    /// <summary>Set when the user chose to plan. <see cref="StartUtc"/> null means no time.</summary>
    public bool Planned { get; private set; }
    public DateTimeOffset? StartUtc { get; private set; }
    public int Minutes => _selMinutes > 0 ? _selMinutes : Durations[Math.Max(0, Duration.SelectedIndex)];

    internal PlanDialog(Item item, int defaultMinutes, Func<int, IReadOnlyList<DateTimeOffset>> suggest,
        DateTimeOffset? calendarReadUtc, WorkHours hours, Func<DateOnly, IReadOnlyList<DayCell>> day)
    {
        _item = item;
        _suggest = suggest;
        _day = day;
        _hours = hours;
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(new System.Windows.Interop.WindowInteropHelper(this).Handle);

        var view = new ItemView(item);
        ItemTitle.Text = view.Title;
        ItemMeta.Text = view.Meta;

        NoTime.Content = item.DueUtc is { } due
            ? $"No time — remind me as usual ({Display.Time(due)})"
            : "No time — just mark it planned";

        if (calendarReadUtc is null)
        {
            SlotsNote.Text = "Your calendar hasn't been read yet, so neither the suggestions nor the day view show meetings.";
            SlotsNote.Visibility = Visibility.Visible;
        }

        Legend.Inlines.Add(Swatch(MeetingBrush, "meeting"));
        Legend.Inlines.Add(Swatch(PlannedBrush, "planned"));
        Legend.Inlines.Add(Swatch(PastBrush, "past"));
        Legend.Inlines.Add(Swatch(SelectedBrush, "your selection"));
        if (item.PlannedStartUtc is not null) Legend.Inlines.Add(new System.Windows.Documents.Run("   dashed: the current plan"));

        _shownDay = NextWorkDay(Today(), includeToday: true);
        foreach (var m in Durations) Duration.Items.Add(Label(m));
        var start = Array.IndexOf(Durations, defaultMinutes);
        Duration.SelectedIndex = start >= 0 ? start : 1;   // fills the suggestions and selects the first
    }

    // ── Suggestions ─────────────────────────────────────────────────────────────────────────

    private void OnDurationChanged(object sender, SelectionChangedEventArgs e)
    {
        var minutes = Durations[Duration.SelectedIndex];
        var slots = _suggest(minutes);

        Slots.Children.Clear();
        SlotsHeading.Text = slots.Count > 0
            ? $"Next open {Label(minutes)} in your work hours"
            : $"No open {Label(minutes)} in your work hours in the next two weeks";

        foreach (var slot in slots)
        {
            var button = new Button { Content = $"{Display.Time(slot)}  –  {Local(slot.AddMinutes(minutes)):HH:mm}" };
            button.Click += (_, _) => Select(slot, minutes, showDay: true);
            Slots.Children.Add(button);
        }

        if (slots.Count > 0) Select(slots[0], minutes, showDay: true);
        else { ClearSelection(); ShowDay(_shownDay); }
    }

    // ── Day view ────────────────────────────────────────────────────────────────────────────

    private void OnPrevDay(object sender, RoutedEventArgs e) => ShowDay(PrevWorkDay(_shownDay));
    private void OnNextDay(object sender, RoutedEventArgs e) => ShowDay(NextWorkDay(_shownDay.AddDays(1), includeToday: true));

    private void ShowDay(DateOnly day)
    {
        if (day < Today()) day = Today();
        _shownDay = day;
        _cells = _day(day);

        var days = day.DayNumber - Today().DayNumber;
        DayLabel.Text = (days == 0 ? "Today, " : days == 1 ? "Tomorrow, " : "") +
                        day.ToString("dddd d MMMM", CultureInfo.InvariantCulture);

        DayCells.Children.Clear();
        DayCells.RowDefinitions.Clear();
        DayCells.ColumnDefinitions.Clear();
        DayCells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        DayCells.ColumnDefinitions.Add(new ColumnDefinition());
        _cellViews.Clear();

        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            var local = Local(cell.StartUtc);
            DayCells.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellHeight) });

            if (local.Minute is 0 or 30)
            {
                var time = new TextBlock
                {
                    Text = local.ToString("HH:mm", CultureInfo.InvariantCulture),
                    FontSize = 11, Opacity = local.Minute == 0 ? 0.8 : 0.5,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -1, 6, 0),
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetRow(time, i);
                DayCells.Children.Add(time);
            }

            var box = new Border
            {
                BorderBrush = local.Minute == 0 ? HourLineBrush : LineBrush,
                BorderThickness = new Thickness(1, local.Minute == 0 ? 1 : 0.5, 1, 0),
                Child = new TextBlock
                {
                    Text = cell.Label ?? "", FontSize = 11, Margin = new Thickness(6, 2, 4, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                Cursor = cell.State == CellState.Free ? Cursors.Hand : Cursors.Arrow,
                ToolTip = cell.State switch
                {
                    CellState.Meeting => $"Meeting{(cell.Label is null ? "" : ": " + cell.Label)}",
                    CellState.Planned => "Already planned",
                    CellState.Past => "Already past",
                    _ => $"{local:HH:mm} — free"
                }
            };
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            DayCells.Children.Add(box);
            _cellViews.Add(box);
        }

        Paint();
    }

    /// <summary>Colours every cell for its state, the selection, and this item's current plan.</summary>
    private void Paint()
    {
        var (first, last) = SelectedRange();
        for (var i = 0; i < _cellViews.Count; i++)
        {
            var cell = _cells[i];
            var selected = i >= first && i <= last;
            var box = _cellViews[i];
            var text = (TextBlock)box.Child;

            box.Background = selected ? SelectedBrush : cell.State switch
            {
                CellState.Meeting => MeetingBrush,
                CellState.Planned => PlannedBrush,
                CellState.Past => PastBrush,
                _ => FreeBrush
            };
            text.Foreground = selected ? Brushes.White : Brushes.Black;
            text.Text = selected && i == first ? _item.Title : cell.Label ?? "";
            text.FontWeight = selected && i == first ? FontWeights.SemiBold : FontWeights.Normal;

            // The plan this item has now, if you're re-planning it.
            var current = _item.PlannedStartUtc is { } ps &&
                          cell.StartUtc >= ps && cell.StartUtc < ps.AddMinutes(_item.PlannedMinutes ?? 30);
            box.BorderBrush = current ? SelectedBrush : Local(cell.StartUtc).Minute == 0 ? HourLineBrush : LineBrush;
            box.BorderThickness = current ? new Thickness(2) : new Thickness(1, Local(cell.StartUtc).Minute == 0 ? 1 : 0.5, 1, 0);
        }

        PlanSelected.Content = _selStart is { } s
            ? $"Plan {Display.Time(s)} – {Local(s.AddMinutes(_selMinutes)):HH:mm} ({Label(_selMinutes)})"
            : "Select a time to plan";
        PlanSelected.IsEnabled = _selStart is not null;
        NoTime.IsDefault = _selStart is null;
    }

    private (int First, int Last) SelectedRange()
    {
        if (_selStart is not { } start) return (-1, -1);
        var first = -1;
        var last = -1;
        for (var i = 0; i < _cells.Count; i++)
        {
            if (_cells[i].StartUtc >= start && _cells[i].StartUtc < start.AddMinutes(_selMinutes))
            {
                if (first < 0) first = i;
                last = i;
            }
        }
        return (first, last);
    }

    private void Select(DateTimeOffset startUtc, int minutes, bool showDay)
    {
        _selStart = startUtc;
        _selMinutes = minutes;
        var day = DateOnly.FromDateTime(Local(startUtc));
        // Build the day if it's a different one — or if no day has been drawn yet. On open the
        // first suggestion is usually today, which "a different day" alone never caught.
        if (showDay && (day != _shownDay || _cellViews.Count == 0)) ShowDay(day);
        else Paint();
        ScrollToSelection();
    }

    private void ClearSelection()
    {
        _selStart = null;
        _selMinutes = 0;
    }

    private void ScrollToSelection() => Dispatcher.BeginInvoke(() =>
    {
        var (first, _) = SelectedRange();
        if (first >= 0) DayScroll.ScrollToVerticalOffset(Math.Max(0, first * CellHeight - 3 * CellHeight));
    });

    // ── Mouse: click a quarter hour, drag to extend, Shift+click to extend ───────────────────

    private int CellAt(MouseEventArgs e)
    {
        var y = e.GetPosition(DayCells).Y;
        var i = (int)Math.Floor(y / CellHeight);
        return i >= 0 && i < _cells.Count ? i : -1;
    }

    private void OnCellsDown(object sender, MouseButtonEventArgs e)
    {
        var i = CellAt(e);
        if (i < 0 || _cells[i].State != CellState.Free) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor >= 0 && _selStart is not null)
        {
            SelectCells(_anchor, i);
            return;
        }

        _anchor = i;
        _dragging = true;
        DayCells.CaptureMouse();
        SelectCells(i, i);
    }

    private void OnCellsMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var i = CellAt(e);
        if (i >= 0) SelectCells(_anchor, i);
    }

    private void OnCellsUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        DayCells.ReleaseMouseCapture();
    }

    private void SelectCells(int from, int to)
    {
        var (first, last) = DayGrid.Selection(_cells, from, to);
        if (first < 0) return;
        _selStart = _cells[first].StartUtc;
        _selMinutes = (last - first + 1) * 15;
        Paint();
    }

    // ── Commit ──────────────────────────────────────────────────────────────────────────────

    private void OnPlanSelected(object sender, RoutedEventArgs e)
    {
        if (_selStart is { } start) Finish(start);
    }

    private void OnNoTime(object sender, RoutedEventArgs e)
    {
        ClearSelection();
        Finish(null);
    }

    private void Finish(DateTimeOffset? startUtc)
    {
        Planned = true;
        StartUtc = startUtc;
        DialogResult = true;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private DateTime Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, _hours.Zone).DateTime;
    private DateOnly Today() => DateOnly.FromDateTime(Local(DateTimeOffset.UtcNow));

    private DateOnly NextWorkDay(DateOnly from, bool includeToday)
    {
        var d = includeToday ? from : from.AddDays(1);
        for (var i = 0; i < 14 && !_hours.Days.Contains(d.DayOfWeek); i++) d = d.AddDays(1);
        return d;
    }

    private DateOnly PrevWorkDay(DateOnly from)
    {
        var d = from.AddDays(-1);
        for (var i = 0; i < 14 && !_hours.Days.Contains(d.DayOfWeek); i++) d = d.AddDays(-1);
        return d < Today() ? Today() : d;
    }

    private static string Label(int minutes) =>
        minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";

    private static System.Windows.Documents.Run Swatch(Brush brush, string text) =>
        new($"■ {text}   ") { Foreground = brush == PastBrush ? Brushes.Gray : brush };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
