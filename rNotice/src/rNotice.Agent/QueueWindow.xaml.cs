using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using rNotice.Core.Items;
using rNotice.Core.Rules;
using rNotice.Core.Scheduling;

namespace rNotice.Agent;

/// <summary>
/// The main surface. Left: Intake (with Done on a second tab) on the top third and the selected
/// item below. Right: Planned, as a calendar day — meetings and planned tasks as blocks at their
/// times, like Outlook's day view.
///
/// One item is selected at a time, whether it was picked in a list or on the calendar, and the
/// keys act on it. Closing hides the window; the agent keeps running in the tray.
/// </summary>
public partial class QueueWindow : Window
{
    private const double HourPx = 64;

    private readonly AgentHost _host;
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };

    private enum Area { Intake, Done, Calendar }
    private Area _area = Area.Intake;
    private Guid? _selected;
    private DateTimeOffset? _selectedStart;   // where a calendar selection was, to find the next one
    private bool _quiet;                      // set while the code changes list selection itself

    private DateOnly _day;
    private int _firstHour, _lastHour;
    private IReadOnlyList<CalendarEntry> _entries = [];
    private Rectangle? _ghost;

    internal QueueWindow(AgentHost host)
    {
        _host = host;
        InitializeComponent();
        _day = Today();

        SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Reload(); _countdown.Start(); ScrollToWorkingTime(); }
            else _countdown.Stop();
        };
        _countdown.Tick += (_, _) => ShowNextRefresh();
        PreviewKeyDown += OnKey;
        Reload();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    // ── Reload ──────────────────────────────────────────────────────────────────────────────

    public void Reload()
    {
        if (!IsVisible) return;

        var now = DateTimeOffset.UtcNow;
        var items = _host.Items();

        Fill(IntakeList, IntakeHeader, "Intake",
            QueueOrder.SortByDay(items.Where(i => i.Stage == Stage.Intake), now, TimeZoneInfo.Local), byDay: true);
        Fill(DoneList, DoneHeader, "Done",
            items.Where(i => i.Stage == Stage.Done).OrderByDescending(i => i.DueUtc ?? i.ReceivedUtc).Take(50), byDay: false);
        PlannedHeader.Text = $"Planned ({items.Count(i => i.Stage == Stage.Planned && i.GroupId is null)})";

        BuildCalendar();

        // A calendar selection that's gone — done, dismissed, moved off this day — passes to the
        // next block, so the keys can carry on down the day.
        if (_area == Area.Calendar && _selected is { } id && !_entries.Any(e => e.Item?.Id == id) &&
            !_host.PlannedWithoutTime().Any(i => i.Id == id))
        {
            var next = SelectableBlocks().FirstOrDefault(e => e.StartUtc >= (_selectedStart ?? now));
            Select(next?.Item, Area.Calendar);
        }

        SignedInText.Text = _host.NeedsSignIn ? "⚠ Sign-in needed (tray → Sign in)" : $"Signed in as {_host.SignedInAs ?? "…"}";
        SourcesText.Text = string.Join("      ", _host.Statuses.Select(s =>
            $"{s.Name}{(s.Name == "Workbook" ? $" {_host.WorkbookPath}" : "")}: {(s.Failing ? "⚠ " : "")}{s.Summary}" +
            (s.LastOkUtc is { } t ? $" · checked {Display.Time(t)}" : "")));
        ShowNextRefresh();
        ShowDetail(SelectedItem());
    }

    private void ShowNextRefresh()
    {
        var left = _host.NextRefreshUtc - DateTimeOffset.UtcNow;
        NextRefreshText.Text = _host.Refreshing || left <= TimeSpan.Zero
            ? "Refreshing…"
            : left.TotalSeconds < 60
                ? $"Next refresh in {(int)Math.Ceiling(left.TotalSeconds)} s"
                : $"Next refresh {Display.Time(_host.NextRefreshUtc)}";
    }

    // ── Lists ───────────────────────────────────────────────────────────────────────────────

    private void Fill(ListBox list, TextBlock header, string name, IEnumerable<Item> items, bool byDay)
    {
        var selected = (list.SelectedItem as ItemView)?.Id;
        var selectedIndex = list.SelectedIndex;
        var alsoSelected = list.SelectedItems.OfType<ItemView>().Select(v => v.Id).Where(id => id != selected).ToHashSet();
        var views = items.Select(i => new ItemView(i)).ToList();

        // A refresh every minute rebuilds the list; keep the scroll where it was.
        var scroller = FindScrollViewer(list);
        var offset = scroller?.VerticalOffset ?? 0;
        if (scroller is not null && offset > 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => scroller.ScrollToVerticalOffset(offset));

        var view = new ListCollectionView(views);
        if (byDay)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ItemView.Day)));
            if (list.GroupStyle.Count == 0) list.GroupStyle.Add((GroupStyle)FindResource("ByDay"));
        }

        _quiet = true;
        list.ItemsSource = view;
        header.Text = $"{name} ({views.Count})";

        if (views.FirstOrDefault(v => v.Id == selected) is { } still)
        {
            list.SelectedItem = still;
            // A Ctrl/Shift selection survives the minute's refresh too.
            if (list.SelectionMode != SelectionMode.Single)
                foreach (var other in views.Where(v => alsoSelected.Contains(v.Id))) list.SelectedItems.Add(other);
        }
        else if (selected is not null && selectedIndex >= 0 && list.Items.Count > 0)
        {
            // The selected item left this list. Select whatever took its place, so Delete, C or
            // P can go straight on to the next one without reaching for the mouse.
            list.SelectedIndex = Math.Min(selectedIndex, list.Items.Count - 1);
            if (ListArea(list) == _area)
            {
                _selected = (list.SelectedItem as ItemView)?.Id;
                if (IsActive) FocusSelected(list);
            }
        }
        _quiet = false;
    }

    private Area ListArea(ListBox list) => list == DoneList ? Area.Done : Area.Intake;
    private ListBox ListFor(Area area) => area == Area.Done ? DoneList : IntakeList;

    private void Advance(ListBox list)
    {
        if (list.SelectedIndex < 0 || list.SelectedIndex >= list.Items.Count - 1) return;
        list.SelectedIndex++;
        if (IsActive) FocusSelected(list);
    }

    private void FocusSelected(ListBox list) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (list.SelectedItem is null) return;
            list.ScrollIntoView(list.SelectedItem);
            if (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListBoxItem row) row.Focus();
        });

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;   // don't let it bubble into the TabControl as a tab change
        if (_quiet) return;
        var list = (ListBox)sender;
        if (list.SelectedItem is ItemView v) Select(v.Item, ListArea(list));
    }

    private void OnListFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var list = (ListBox)sender;
        if (list.SelectedItem is ItemView v) Select(v.Item, ListArea(list));
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;
        var area = Tabs.SelectedItem == DoneTab ? Area.Done : Area.Intake;
        Select((ListFor(area).SelectedItem as ItemView)?.Item, area);
    }

    // ── Selection and detail ────────────────────────────────────────────────────────────────

    private Item? SelectedItem() => _selected is { } id ? _host.Items().FirstOrDefault(i => i.Id == id) : null;

    private void Select(Item? item, Area area)
    {
        _selected = item?.Id;
        _area = area;
        if (area == Area.Calendar)
        {
            _selectedStart = item?.PlannedStartUtc ?? item?.DueUtc;
            _quiet = true;
            IntakeList.SelectedItem = null;
            DoneList.SelectedItem = null;
            _quiet = false;
        }
        // Repaint outlines only. Redrawing would destroy the block under the mouse, and with it
        // any drag that's about to start from it.
        PaintSelection();
        ShowDetail(item);
    }

    private void ShowDetail(Item? item)
    {
        DetailPlaceholder.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        DetailBody.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item is null) return;

        var view = new ItemView(item);
        var caseUrl = view.HasCaseLink ? item.CaseUrl : null;

        // The case number is the link, in the title and in the excerpt — no separate line.
        // A refresh mid-edit leaves the editor alone.
        if (_editingTitle != item.Id) EndTitleEdit();
        SetRich(DetailTitle, view.Title, view.CaseNumber, caseUrl);
        DetailMeta.Text = view.Meta;
        SetNote(view);
        DetailNote.Visibility = view.HasNote ? Visibility.Visible : Visibility.Collapsed;
        DetailReason.Text = view.Reason;
        SetRich(DetailSnippet, view.Snippet ?? "", view.CaseNumber, caseUrl);
        DetailSnippet.Visibility = view.HasSnippet ? Visibility.Visible : Visibility.Collapsed;

        DetailOpenHint.Text = view.HasSource
            ? item.Source == Source.Mail
                ? "This is the start of the message. Enter or right-click → Open in Outlook for all of it."
                : $"Enter or right-click → {view.OpenLabel}."
            : "";
    }

    /// <summary>
    /// Fills a read-only rich text box, keeping line breaks, with every occurrence of the case
    /// number as a link to the case when there is one.
    /// </summary>
    // An https address in the text. Only https is made a link: OpenLink refuses anything else.
    private static readonly System.Text.RegularExpressions.Regex WebAddress =
        new(@"https://[^\s<>""]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Text with its links live: every https address, and the case number wherever it appears.
    /// In a grouped plan's list, a line's case number links to the case address on the line
    /// under it, so each case opens from its own number.
    /// </summary>
    private void SetRich(RichTextBox box, string text, string? caseNumber, string? caseUrl)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var l = 0; l < lines.Length; l++)
        {
            if (l > 0) paragraph.Inlines.Add(new LineBreak());
            var line = lines[l];

            var (number, url) = (caseNumber, caseUrl);
            if (l + 1 < lines.Length && lines[l + 1].Trim() is var under && WebAddress.Match(under) is { Success: true } m &&
                m.Value == under && CaseLinks.FindAnyCaseNumber(line) is { } own)
                (number, url) = (own, under);

            var at = 0;
            foreach (System.Text.RegularExpressions.Match address in WebAddress.Matches(line))
            {
                AddWithCase(paragraph, line[at..address.Index], number, url);
                paragraph.Inlines.Add(Link(address.Value, address.Value, "Open in the browser"));
                at = address.Index + address.Length;
            }
            AddWithCase(paragraph, line[at..], number, url);
        }

        box.Document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            FontFamily = box.FontFamily,
            FontSize = box.FontSize,
            FontWeight = box.FontWeight
        };
    }

    /// <summary>The note in the detail pane, with the clashing meeting's name as a link.</summary>
    private void SetNote(ItemView view)
    {
        var paragraph = new Paragraph(new Run(view.NoteLead)) { Margin = new Thickness(0) };
        if (view.HasNoteLink)
        {
            var link = new Hyperlink(new Run(view.NoteLinkText)) { ToolTip = "Show this meeting", Cursor = Cursors.Hand };
            var eventId = view.Item.NoteLink!;
            link.Click += (_, _) => GoToMeeting(eventId);
            paragraph.Inlines.Add(link);
        }
        DetailNote.Document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0), FontFamily = DetailNote.FontFamily, FontSize = DetailNote.FontSize, FontStyle = FontStyles.Italic
        };
    }

    /// <summary>A clash note's link, in the Intake list.</summary>
    private void OnNoteLink(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkContentElement)?.DataContext is ItemView { Item.NoteLink: { } eventId }) GoToMeeting(eventId);
        e.Handled = true;
    }

    /// <summary>
    /// Shows the meeting a note points at: the calendar moves to its day and outlines it, and
    /// it fills the detail pane. A meeting rNotice doesn't track (blocklisted, dismissed) can't
    /// be selected, so the calendar just moves to it.
    /// </summary>
    private void GoToMeeting(string eventId)
    {
        if (_host.EventStart(eventId) is not { } start) return;   // gone from the calendar
        _day = DateOnly.FromDateTime(Local(start));
        BuildCalendar();
        // Any meeting rNotice has a record of, done or dismissed included: the note is about it
        // whatever its state. Only a blocklisted meeting, which has no item, can't be shown.
        Select(_host.MeetingItem(eventId), Area.Calendar);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => CalScroll.ScrollToVerticalOffset(Math.Max(0, Y(start) - HourPx)));
    }

    private void AddWithCase(Paragraph paragraph, string text, string? caseNumber, string? caseUrl)
    {
        if (caseNumber is null || caseUrl is null || !text.Contains(caseNumber))
        {
            if (text.Length > 0) paragraph.Inlines.Add(new Run(text));
            return;
        }

        var parts = text.Split(caseNumber);
        for (var p = 0; p < parts.Length; p++)
        {
            if (p > 0) paragraph.Inlines.Add(Link(caseNumber, caseUrl, "Open the case in Salesforce"));
            if (parts[p].Length > 0) paragraph.Inlines.Add(new Run(parts[p]));
        }
    }

    private Hyperlink Link(string text, string url, string tip)
    {
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url), ToolTip = tip, Cursor = Cursors.Hand };
        link.RequestNavigate += OnCaseLink;
        return link;
    }

    private void OnCaseLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        AgentHost.OpenLink(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    // ── Calendar ────────────────────────────────────────────────────────────────────────────

    private DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _host.WorkHours.Zone).DateTime);
    private DateTime Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, _host.WorkHours.Zone).DateTime;

    private void OnPrevDay(object sender, RoutedEventArgs e) => ChangeDay(-1);
    private void OnNextDay(object sender, RoutedEventArgs e) => ChangeDay(+1);
    private void OnToday(object sender, RoutedEventArgs e) { _day = Today(); BuildCalendar(); ScrollToWorkingTime(); }

    /// <summary>Steps to the previous or next working day.</summary>
    private void ChangeDay(int step)
    {
        var days = _host.WorkHours.Days;
        var d = _day.AddDays(step);
        for (var i = 0; i < 7 && !days.Contains(d.DayOfWeek); i++) d = d.AddDays(step);
        _day = d;
        BuildCalendar();
        ScrollToWorkingTime();
    }

    /// <summary>Reads the day's entries and sizes the grid: work hours, stretched to fit anything outside them.</summary>
    private void BuildCalendar()
    {
        var hours = _host.WorkHours;
        _entries = _host.CalendarDay(_day);

        var days = _day.DayNumber - Today().DayNumber;
        DayLabel.Text = (days == 0 ? "Today, " : days == 1 ? "Tomorrow, " : days == -1 ? "Yesterday, " : "") +
                        _day.ToString("dddd d MMMM", CultureInfo.InvariantCulture);

        var timed = _entries.Where(e => !e.AllDay).ToList();
        var midnight = _day.ToDateTime(TimeOnly.MinValue);
        _firstHour = Math.Min(hours.Start.Hour, timed.Count == 0 ? 24 :
            (int)Math.Floor(Math.Max(0, (Local(timed.Min(e => e.StartUtc)) - midnight).TotalHours)));
        _lastHour = Math.Max(hours.End.Minute > 0 ? hours.End.Hour + 1 : hours.End.Hour, timed.Count == 0 ? 0 :
            (int)Math.Ceiling(Math.Min(24, (Local(timed.Max(e => e.EndUtc)) - midnight).TotalHours)));

        var height = (_lastHour - _firstHour) * HourPx;
        CalCanvas.Height = height;
        TimeLabels.Height = height;

        BuildTopStrip();
        DrawCalendar();
    }

    private double Y(DateTimeOffset utc) =>
        Math.Clamp((Local(utc) - _day.ToDateTime(new TimeOnly(_firstHour, 0))).TotalHours * HourPx, 0, CalCanvas.Height);

    private DateTimeOffset TimeAt(double y)
    {
        var minutes = Math.Floor(y / HourPx * 60 / 15) * 15;
        var local = _day.ToDateTime(new TimeOnly(_firstHour, 0)).AddMinutes(minutes);
        return new DateTimeOffset(local, _host.WorkHours.Zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>All-day events and untimed plans, as chips above the grid.</summary>
    private void BuildTopStrip()
    {
        TopStrip.Children.Clear();
        _views.RemoveAll(v => v.View.Parent == null || v.View.Parent == TopStrip);
        foreach (var e in _entries.Where(e => e.AllDay))
            TopStrip.Children.Add(Chip($"All day: {e.Title}", e.Kind, e.Item));
        foreach (var item in _host.PlannedWithoutTime())
            TopStrip.Children.Add(Chip($"No time: {item.Title}", CalendarKind.Task, item));
        TopStrip.Visibility = TopStrip.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Every block and chip that stands for an item, with its normal outline, for repainting selection.</summary>
    private readonly List<(Guid Id, Border View, Brush Edge, Thickness Normal)> _views = [];

    private void PaintSelection()
    {
        foreach (var (id, view, edge, normal) in _views)
        {
            var selected = id == _selected;
            view.BorderBrush = selected ? Brushes.Black : edge;
            view.BorderThickness = selected ? new Thickness(2) : normal;
        }
    }

    private Border Chip(string text, CalendarKind kind, Item? item)
    {
        var (fill, edge, _) = Colours(kind, item);
        var chip = new Border
        {
            Background = fill, BorderBrush = item?.Id == _selected ? Brushes.Black : edge,
            BorderThickness = new Thickness(item?.Id == _selected ? 2 : 1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 6, 4), MaxWidth = 320,
            Child = new TextBlock { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
            ToolTip = text
        };
        if (item is not null)
        {
            Interactive(chip, item);
            _views.Add((item.Id, chip, edge, new Thickness(1)));
        }
        return chip;
    }

    /// <summary>Draws the hour grid, the blocks and the now line. Runs on resize, so widths are real.</summary>
    private void DrawCalendar()
    {
        if (!IsLoaded && !IsVisible) return;
        var width = CalCanvas.ActualWidth;
        _views.RemoveAll(v => v.View.Parent == CalCanvas);
        CalCanvas.Children.Clear();
        TimeLabels.Children.Clear();
        _ghost = null;
        if (width <= 0) return;

        var hours = _host.WorkHours;
        var workStart = (hours.Start.ToTimeSpan().TotalHours - _firstHour) * HourPx;
        var workEnd = (hours.End.ToTimeSpan().TotalHours - _firstHour) * HourPx;
        var offDay = !hours.Days.Contains(_day.DayOfWeek);

        // Outside work hours, shaded.
        Shade(0, offDay ? CalCanvas.Height : workStart);
        if (!offDay) Shade(workEnd, CalCanvas.Height - workEnd);

        for (var h = _firstHour; h <= _lastHour; h++)
        {
            var y = (h - _firstHour) * HourPx;
            CalCanvas.Children.Add(Line(y, width, 0.35));
            if (h < _lastHour) CalCanvas.Children.Add(Line(y + HourPx / 2, width, 0.12));
            if (h < _lastHour)
            {
                var label = new TextBlock { Text = $"{h:00}:00", FontSize = 11, Opacity = 0.7 };
                Canvas.SetTop(label, y - 1);
                Canvas.SetLeft(label, 8);
                TimeLabels.Children.Add(label);
            }
        }

        var timed = _entries.Where(e => !e.AllDay).ToList();
        foreach (var placed in DayLayout.Arrange(timed, e => e.StartUtc, e => e.EndUtc))
        {
            var e = placed.Block;
            var colWidth = (width - 4) / placed.Columns;
            var top = Y(e.StartUtc);
            var height = Math.Max(20, Y(e.EndUtc) - top - 1);
            var block = Block(e, colWidth - 3, height);
            Canvas.SetLeft(block, 2 + placed.Column * colWidth);
            Canvas.SetTop(block, top);
            CalCanvas.Children.Add(block);
        }

        // The current time, on today's view.
        if (_day == Today())
        {
            var now = new Rectangle { Width = width, Height = 2, Fill = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)), IsHitTestVisible = false };
            Canvas.SetTop(now, Y(DateTimeOffset.UtcNow));
            CalCanvas.Children.Add(now);
        }
    }

    private void Shade(double top, double height)
    {
        if (height <= 0) return;
        var r = new Rectangle { Width = CalCanvas.ActualWidth, Height = height, Fill = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4)), IsHitTestVisible = false };
        Canvas.SetTop(r, top);
        CalCanvas.Children.Add(r);
    }

    private static Rectangle Line(double y, double width, double opacity)
    {
        var r = new Rectangle { Width = width, Height = 1, Fill = Brushes.Gray, Opacity = opacity, IsHitTestVisible = false };
        Canvas.SetTop(r, y);
        return r;
    }

    private static (Brush Fill, Brush Edge, Brush Text) Colours(CalendarKind kind, Item? item)
    {
        var state = item is null ? "" : new ItemView(item).MeetingState;
        return kind switch
        {
            CalendarKind.Meeting when state == "joined" => (Hex(0xD9F0E3), Hex(0x2E8B57), Brushes.Black),
            CalendarKind.Meeting when state == "live" => (Hex(0xFDEBD3), Hex(0xE08B1E), Brushes.Black),
            CalendarKind.Meeting => (Hex(0xDCE7F5), Hex(0x2B6CB0), Brushes.Black),
            CalendarKind.Task => (Hex(0xFCE8C8), Hex(0xE08B1E), Brushes.Black),
            CalendarKind.Done => (Hex(0xEDE7F6), Hex(0x7E57C2), Hex(0x4A3B6B)),
            CalendarKind.Worked => (Hex(0xEEEEEE), Hex(0x9AA5B1), Brushes.DimGray),
            _ => (Hex(0xEEEEEE), Hex(0xBBBBBB), Brushes.DimGray)
        };
    }

    private static SolidColorBrush Hex(int rgb) =>
        new(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8 & 0xFF), (byte)(rgb & 0xFF)));

    private Border Block(CalendarEntry e, double width, double height)
    {
        var (fill, edge, text) = Colours(e.Kind, e.Item);
        var selected = e.Item is not null && e.Item.Id == _selected;
        var when = $"{Local(e.StartUtc):HH:mm}–{Local(e.EndUtc):HH:mm}";

        var content = new StackPanel();
        // Too short for a second line: the start time goes in front of the title, as Outlook does.
        content.Children.Add(new TextBlock
        {
            Text = (e.Kind == CalendarKind.Done ? "✓ " : "") + (height > 34 ? e.Title : $"{Local(e.StartUtc):HH:mm} · {e.Title}"),
            FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = text,
            TextWrapping = height > 34 ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (height > 34)
            content.Children.Add(new TextBlock { Text = e.Kind switch
            {
                CalendarKind.Task => $"{when} · {(e.Item?.IsWorking == true ? "working" : "planned")}{(e.Item?.CalendarEventId is not null ? " · in Outlook" : "")}",
                CalendarKind.Done => $"{when} · done" + (e.Item?.WorkedMinutes is > 0 and var m ? $" · worked {Display.Duration(TimeSpan.FromMinutes(m))}" : ""),
                CalendarKind.Worked => $"{when} · worked",
                _ => when
            }, FontSize = 11, Opacity = 0.75 });

        var block = new Border
        {
            Width = Math.Max(10, width), Height = height,
            Background = fill, BorderBrush = selected ? Brushes.Black : edge,
            BorderThickness = selected ? new Thickness(2) : new Thickness(3, 1, 1, 1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 2, 4, 2),
            Child = content, ClipToBounds = true,
            ToolTip = $"{e.Title}\n{when}" + (e.Kind == CalendarKind.Busy ? "\n(not tracked by rNotice)" : "")
        };
        if (e.Item is not null)
        {
            Interactive(block, e.Item);
            _views.Add((e.Item.Id, block, edge, new Thickness(3, 1, 1, 1)));
        }
        return block;
    }

    /// <summary>Click selects, right-click opens the menu, drag moves it.</summary>
    private void Interactive(FrameworkElement element, Item item)
    {
        element.Cursor = Cursors.Hand;
        element.MouseLeftButtonDown += (_, ev) =>
        {
            Select(item, Area.Calendar);
            _dragItem = item;
            _dragStart = ev.GetPosition(this);
            Focus();
            ev.Handled = true;
        };
        element.MouseMove += (s, ev) => MaybeStartDrag((DependencyObject)s, ev);
        element.ContextMenu = new ContextMenu();
        element.ContextMenuOpening += (_, ev) =>
        {
            Select(item, Area.Calendar);
            BuildMenu(element.ContextMenu, item, () => { });
        };
    }

    /// <summary>Clicking empty calendar space clears a calendar selection.</summary>
    private void OnCalendarBackgroundClick(object sender, MouseButtonEventArgs e)
    {
        if (_area == Area.Calendar) Select(null, Area.Calendar);
    }

    private void OnCalendarResized(object sender, SizeChangedEventArgs e) => DrawCalendar();

    private void ScrollToWorkingTime() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        var focus = _day == Today() ? DateTimeOffset.UtcNow : TimeAt((_host.WorkHours.Start.ToTimeSpan().TotalHours - _firstHour) * HourPx);
        CalScroll.ScrollToVerticalOffset(Math.Max(0, Y(focus) - HourPx));
    });

    private IEnumerable<CalendarEntry> SelectableBlocks() =>
        _entries.Where(e => !e.AllDay && e.Item is not null).OrderBy(e => e.StartUtc);

    // ── Keys ────────────────────────────────────────────────────────────────────────────────

    // ── Editing a title ─────────────────────────────────────────────────────────────────────

    private Guid? _editingTitle;

    private static bool CanEditTitle(Item item) => item.Source != Source.Calendar;

    private void BeginTitleEdit()
    {
        if (SelectedItem() is not { } item || !CanEditTitle(item)) return;
        _editingTitle = item.Id;
        TitleEditor.Text = item.Title;
        DetailTitle.Visibility = Visibility.Collapsed;
        TitleEditor.Visibility = Visibility.Visible;
        TitleEditor.Focus();
        TitleEditor.SelectAll();
    }

    private void EndTitleEdit(bool save = false)
    {
        if (_editingTitle is not { } id) return;
        _editingTitle = null;
        TitleEditor.Visibility = Visibility.Collapsed;
        DetailTitle.Visibility = Visibility.Visible;
        if (save) _host.SetTitle(id, TitleEditor.Text);
    }

    private void OnTitleDoubleClick(object sender, MouseButtonEventArgs e) => BeginTitleEdit();

    private void OnTitleEditorKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EndTitleEdit(save: true); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndTitleEdit(); e.Handled = true; }
    }

    /// <summary>Clicking away saves, as in most rename boxes.</summary>
    private void OnTitleEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndTitleEdit(save: true);

    private void OnKey(object sender, KeyEventArgs e)
    {
        // Typing in the title editor, or copying in the detail pane — Ctrl+C there must copy, not
        // mark done. The detail pane's read-only text doesn't count: clicking a calendar block can
        // leave keyboard focus there, and C then did nothing (2026-10-28).
        if (e.OriginalSource is TextBoxBase { IsReadOnly: false } || Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            return;

        var selected = SelectedItem();
        switch (e.Key)
        {
            case Key.Escape: Hide(); break;
            case Key.F5: _host.RefreshNow(); break;
            case Key.Left: ChangeDay(-1); break;
            case Key.Right: ChangeDay(+1); break;
            case Key.Up when _area == Area.Calendar: StepCalendar(-1); break;
            case Key.Down when _area == Area.Calendar: StepCalendar(+1); break;
            case Key.P when _area == Area.Intake && IntakeList.SelectedItems.Count > 1:
                _host.PlanGroup(SelectedIntakeIds(), this);
                break;
            case Key.P when selected is not null && CanPlan(selected): _host.Plan(selected.Id, this); break;
            case Key.Delete when selected is not null: _host.SetStage(selected.Id, Stage.Dismissed); break;
            case Key.C when selected is not null: _host.SetStage(selected.Id, Stage.Done); break;
            case Key.S when selected is not null && selected.IsOpen && selected.Source != Source.Calendar:
                _host.Snooze(selected.Id);
                if (_area != Area.Calendar) Advance(ListFor(_area));
                break;
            case Key.Enter when selected is not null: AgentHost.OpenLink(selected.SourceRef); break;
            case Key.F2 when selected is not null && CanEditTitle(selected): BeginTitleEdit(); break;
            case Key.W when selected is not null && selected.IsOpen && selected.Source != Source.Calendar:
                if (selected.IsWorking) _host.StopWork(selected.Id); else _host.StartWork(selected.Id);
                break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Up and down through the day's blocks.</summary>
    private void StepCalendar(int step)
    {
        var blocks = SelectableBlocks().ToList();
        if (blocks.Count == 0) return;
        var i = blocks.FindIndex(b => b.Item!.Id == _selected);
        var next = blocks[Math.Clamp(i < 0 ? 0 : i + step, 0, blocks.Count - 1)];
        Select(next.Item, Area.Calendar);
        CalScroll.ScrollToVerticalOffset(Math.Max(0, Y(next.StartUtc) - HourPx));
    }

    /// <summary>The Intake rows selected with Ctrl or Shift, in list order.</summary>
    private IReadOnlyList<Guid> SelectedIntakeIds() =>
        IntakeList.Items.OfType<ItemView>().Where(v => IntakeList.SelectedItems.Contains(v)).Select(v => v.Id).ToList();

    private static bool CanPlan(Item item) =>
        item.Stage == Stage.Intake || (item.Stage == Stage.Planned && item.Source != Source.Calendar);

    // ── Right-click menu ────────────────────────────────────────────────────────────────────

    private void OnListContextMenu(object sender, ContextMenuEventArgs e)
    {
        var list = (ListBox)sender;
        if (ContainerAt(e.OriginalSource) is not { DataContext: ItemView view } container)
        {
            e.Handled = true;
            return;
        }
        // Right-clicking inside a Ctrl/Shift selection keeps it; anywhere else selects just that row.
        var several = container.IsSelected && list.SelectedItems.Count > 1;
        if (!several)
        {
            list.SelectedItems.Clear();
            container.IsSelected = true;
        }
        Select(view.Item, ListArea(list));
        BuildMenu(list.ContextMenu!, view.Item, () => Advance(list));
        if (several && list == IntakeList)
        {
            var ids = SelectedIntakeIds();
            list.ContextMenu!.Items.Insert(0, Entry($"Plan {ids.Count} together…", "P", () => _host.PlanGroup(ids, this)));
            list.ContextMenu.Items.Insert(1, new Separator());
        }
    }

    /// <summary>
    /// The actions that apply to one item, the same wherever it was right-clicked. Each entry
    /// shows its key, so the menu also teaches the shortcuts.
    /// </summary>
    private void BuildMenu(ContextMenu menu, Item item, Action afterSnooze)
    {
        var view = new ItemView(item);
        menu.Items.Clear();

        if (CanPlan(item))
            menu.Items.Add(Entry(item.Stage == Stage.Intake ? "Plan…" : "Change plan…", "P", () => _host.Plan(item.Id, this)));
        if (item.Source != Source.Calendar && item.Stage == Stage.Planned && item.PlannedStartUtc is { } planned)
        {
            if (!item.PromoteToCalendar)
                menu.Items.Add(Entry("Add to Outlook calendar", "", () => _host.SetPromotion(item.Id, true)));
            else
                menu.Items.Add(Entry("Remove from Outlook calendar…", "", () =>
                {
                    var ask = $"Delete the Outlook event \"{item.Title}\" at {Display.Time(planned)}?\n\n" +
                              "The task stays planned here at the same time.";
                    if (MessageBox.Show(this, ask, "rNotice", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                        _host.SetPromotion(item.Id, false);
                }));
        }
        if (CanEditTitle(item))
            menu.Items.Add(Entry("Edit title", "F2", () => { Select(item, _area); BeginTitleEdit(); }));
        if (item.Source != Source.Calendar && item.IsOpen)
            menu.Items.Add(item.IsWorking
                ? Entry("Stop working (not done)", "W", () => _host.StopWork(item.Id))
                : Entry("Working on it", "W", () => _host.StartWork(item.Id)));
        if (item.Stage != Stage.Done)
            menu.Items.Add(Entry("Mark done", "C", () => _host.SetStage(item.Id, Stage.Done)));
        else
            menu.Items.Add(Entry("Move back to Planned", "", () => _host.SetStage(item.Id, Stage.Planned)));
        if (item.IsOpen && view.CanSnooze)
            menu.Items.Add(Entry($"Snooze {_host.SnoozeMinutes} min", "S", () => { _host.Snooze(item.Id); afterSnooze(); }));
        menu.Items.Add(Entry("Dismiss", "Del", () => _host.SetStage(item.Id, Stage.Dismissed)));

        if (item.Source == Source.Mail && Senders.AddressOf(item.From) is { } address)
        {
            var domain = Senders.DomainEntry(address);
            var ignore = new MenuItem { Header = "Dismiss and ignore sender" };
            ignore.Items.Add(Entry(address, "", () => _host.IgnoreSender(address)));
            if (domain != _host.MyDomain)
                ignore.Items.Add(Entry($"Everyone at {domain}", "", () => _host.IgnoreSender(domain)));
            ignore.Items.Add(new Separator());
            ignore.Items.Add(Entry("Edit ignored senders…", "", _host.OpenIgnoredSenders));
            menu.Items.Add(ignore);

            // Reporting needs Mail.ReadWrite and a beta-only API, so Outlook does it.
            if (view.HasSource)
            {
                var link = item.SourceRef;
                var spam = new MenuItem { Header = "Ignore and report spam" };
                spam.Items.Add(Entry($"{address} — then report in Outlook", "", () => { _host.IgnoreSender(address); AgentHost.OpenLink(link); }));
                if (domain != _host.MyDomain)
                    spam.Items.Add(Entry($"Everyone at {domain} — then report in Outlook", "", () => { _host.IgnoreSender(domain); AgentHost.OpenLink(link); }));
                menu.Items.Add(spam);
            }
        }

        if (view.HasJoin || view.HasSource || view.HasCaseLink) menu.Items.Add(new Separator());
        if (view.HasCaseLink)
            menu.Items.Add(Entry($"Open case {view.CaseNumber} in Salesforce", "", () => AgentHost.OpenLink(item.CaseUrl)));
        if (view.HasJoin)
            menu.Items.Add(Entry("Join meeting", "", () => _host.Join(item.Id)));
        if (view.HasSource)
            menu.Items.Add(Entry(view.OpenLabel, "Enter", () => AgentHost.OpenLink(item.SourceRef)));
    }

    private static MenuItem Entry(string header, string key, Action action)
    {
        var entry = new MenuItem { Header = header, InputGestureText = key };
        entry.Click += (_, _) => action();
        return entry;
    }

    // ── Drag and drop ───────────────────────────────────────────────────────────────────────
    //
    // Onto a time in the calendar: plan it there (anything but a meeting). Onto the Done tab:
    // finish it. Nothing else is a drop — the cursor says so before you let go.

    private Point? _dragStart;
    private Item? _dragItem;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = (ContainerAt(e.OriginalSource)?.DataContext as ItemView)?.Item;
        _dragStart = _dragItem is null ? null : e.GetPosition(this);
    }

    private void OnMouseMove(object sender, MouseEventArgs e) => MaybeStartDrag((DependencyObject)sender, e);

    private void MaybeStartDrag(DependencyObject source, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || _dragItem is null) return;
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _dragItem;
        _dragStart = null;
        _dragItem = null;
        DragDrop.DoDragDrop(source, new DataObject(typeof(Item), item), DragDropEffects.Move);
        HideGhost();
    }

    private static Item? Dragged(DragEventArgs e) => e.Data.GetData(typeof(Item)) as Item;

    private void OnCalendarDragOver(object sender, DragEventArgs e)
    {
        var item = Dragged(e);
        var valid = item is not null && item.Source != Source.Calendar && item.Stage != Stage.Dismissed;
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
        if (valid) ShowGhost(TimeAt(e.GetPosition(CalCanvas).Y));
        else HideGhost();
        e.Handled = true;
    }

    private void OnCalendarDragLeave(object sender, DragEventArgs e) => HideGhost();

    private void OnCalendarDrop(object sender, DragEventArgs e)
    {
        HideGhost();
        if (Dragged(e) is not { } item || item.Source == Source.Calendar) return;
        var start = TimeAt(e.GetPosition(CalCanvas).Y);
        // After the drag, not inside it: the Plan dialog may open, and a modal dialog mid-drop
        // would hold the drag loop open.
        Dispatcher.BeginInvoke(() => _host.PlanAt(item.Id, start, this));
        e.Handled = true;
    }

    /// <summary>Where the item will land: a translucent block at the snapped time.</summary>
    private void ShowGhost(DateTimeOffset start)
    {
        if (_ghost is null)
        {
            _ghost = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x55, 0x2B, 0x6C, 0xB0)), Stroke = Hex(0x2B6CB0),
                StrokeDashArray = [3, 2], RadiusX = 3, RadiusY = 3, IsHitTestVisible = false
            };
            CalCanvas.Children.Add(_ghost);
        }
        _ghost.Width = Math.Max(10, CalCanvas.ActualWidth - 4);
        _ghost.Height = HourPx / 2;
        Canvas.SetLeft(_ghost, 2);
        Canvas.SetTop(_ghost, Y(start));
    }

    private void HideGhost()
    {
        if (_ghost is null) return;
        CalCanvas.Children.Remove(_ghost);
        _ghost = null;
    }

    private void OnDoneDragOver(object sender, DragEventArgs e)
    {
        var valid = Dragged(e) is { Stage: not Stage.Done };
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
        DoneHeader.Foreground = valid ? Hex(0x2B6CB0) : Brushes.Black;
        e.Handled = true;
    }

    private void OnDoneDragLeave(object sender, DragEventArgs e) => DoneHeader.ClearValue(TextBlock.ForegroundProperty);

    private void OnDoneDrop(object sender, DragEventArgs e)
    {
        DoneHeader.ClearValue(TextBlock.ForegroundProperty);
        if (Dragged(e) is not { Stage: not Stage.Done } item) return;
        Dispatcher.BeginInvoke(() => _host.SetStage(item.Id, Stage.Done));
        e.Handled = true;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private static ListBoxItem? ContainerAt(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem item) return item;
        }
        return null;
    }
}
