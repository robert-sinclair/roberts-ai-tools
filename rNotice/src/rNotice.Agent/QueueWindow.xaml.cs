using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using rNotice.Core.Items;
using rNotice.Core.Rules;

namespace rNotice.Agent;

/// <summary>
/// The main surface, where intake → plan → complete happens. Three columns by stage, most
/// urgent first, driven from the keyboard.
///
/// Closing hides it; the agent keeps running in the tray.
/// </summary>
public partial class QueueWindow : Window
{
    private readonly AgentHost _host;
    private readonly ListBox[] _lists;
    private ListBox _active;

    internal QueueWindow(AgentHost host)
    {
        _host = host;
        InitializeComponent();
        _lists = [IntakeList, PlannedList, DoneList];
        _active = IntakeList;

        SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Reload(); _countdown.Start(); }
            else _countdown.Stop();
        };
        _countdown.Tick += (_, _) => ShowNextRefresh();
        PreviewKeyDown += OnKey;
        Reload();
    }

    // Ticks only while the window is open; the polls themselves run whether it is or not.
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };

    private void ShowNextRefresh()
    {
        var left = _host.NextRefreshUtc - DateTimeOffset.UtcNow;
        NextRefreshText.Text = _host.Refreshing || left <= TimeSpan.Zero
            ? "Refreshing…"
            : left.TotalSeconds < 60
                ? $"Next refresh in {(int)Math.Ceiling(left.TotalSeconds)} s"
                : $"Next refresh {Display.Time(_host.NextRefreshUtc)}";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    public void Reload()
    {
        if (!IsVisible) return;

        var now = DateTimeOffset.UtcNow;
        var items = _host.Items();
        Fill(IntakeList, IntakeHeader, "Intake",
            QueueOrder.SortByDay(items.Where(i => i.Stage == Stage.Intake), now, TimeZoneInfo.Local), byDay: true);
        Fill(PlannedList, PlannedHeader, "Planned",
            QueueOrder.SortByDay(items.Where(i => i.Stage == Stage.Planned), now, TimeZoneInfo.Local), byDay: true);
        Fill(DoneList, DoneHeader, "Done",
            items.Where(i => i.Stage == Stage.Done).OrderByDescending(i => i.DueUtc ?? i.ReceivedUtc).Take(50), byDay: false);

        SignedInText.Text = _host.NeedsSignIn ? "⚠ Sign-in needed (tray → Sign in)" : $"Signed in as {_host.SignedInAs ?? "…"}";
        SourcesText.Text = string.Join("      ", _host.Statuses.Select(s =>
            $"{s.Name}{(s.Name == "Workbook" ? $" {_host.WorkbookPath}" : "")}: {(s.Failing ? "⚠ " : "")}{s.Summary}" +
            (s.LastOkUtc is { } t ? $" · checked {Display.Time(t)}" : "")));
        ShowNextRefresh();

        ShowDetail((_active.SelectedItem as ItemView)?.Item);
    }

    /// <summary>
    /// Intake and Planned are grouped under day headers — Today, Tomorrow, Fri 24 Oct. Done is a
    /// flat list, most recent first.
    /// </summary>
    private void Fill(ListBox list, TextBlock header, string name, IEnumerable<Item> items, bool byDay)
    {
        var selected = (list.SelectedItem as ItemView)?.Id;
        var selectedIndex = list.SelectedIndex;
        var views = items.Select(i => new ItemView(i)).ToList();

        // A refresh every minute rebuilds the list. Put the scroll back where it was, after the
        // new items have been laid out, or the queue jumps to the top once a minute.
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

        list.ItemsSource = view;
        header.Text = $"{name} ({views.Count})";

        if (views.FirstOrDefault(v => v.Id == selected) is { } still)
        {
            list.SelectedItem = still;
        }
        else if (selected is not null && selectedIndex >= 0 && list.Items.Count > 0)
        {
            // The selected item left this list — dismissed, done, planned, or gone from its
            // source. Select whatever took its place, so Delete, C or P can go straight on to the
            // next one without reaching for the mouse.
            list.SelectedIndex = Math.Min(selectedIndex, list.Items.Count - 1);
            if (list == _active && IsActive) FocusSelected(list);
        }
    }

    /// <summary>Moves the selection one down in the list, for actions that leave the item in place.</summary>
    private void Advance(ListBox list)
    {
        if (list.SelectedIndex < 0 || list.SelectedIndex >= list.Items.Count - 1) return;
        list.SelectedIndex++;
        if (IsActive) FocusSelected(list);
    }

    /// <summary>
    /// Puts keyboard focus on the selected row, after layout — the row's container doesn't exist
    /// until the rebuilt list has been laid out.
    /// </summary>
    private void FocusSelected(ListBox list) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (list.SelectedItem is null) return;
            list.ScrollIntoView(list.SelectedItem);
            if (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListBoxItem row) row.Focus();
        });

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowDetail(((sender as ListBox)?.SelectedItem as ItemView)?.Item);

    private void OnListFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _active = (ListBox)sender;
        ShowDetail((_active.SelectedItem as ItemView)?.Item);
    }

    /// <summary>
    /// The pane under the lists. It stays put — a placeholder when nothing's selected — so the
    /// lists don't jump as the selection changes, and it scrolls when there's more than fits.
    /// </summary>
    private void ShowDetail(Item? item)
    {
        DetailPlaceholder.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        DetailBody.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item is null) return;

        var view = new ItemView(item);
        DetailTitle.Text = view.Title;
        DetailMeta.Text = view.Meta;
        DetailReason.Text = view.Reason;
        DetailSnippet.Text = view.Snippet ?? "";
        DetailSnippet.Visibility = view.HasSnippet ? Visibility.Visible : Visibility.Collapsed;

        // rNotice keeps an excerpt, never the full message. Say where the rest is.
        DetailOpenHint.Text = view.HasSource
            ? item.Source == Source.Mail
                ? "This is the start of the message. Enter or right-click → Open in Outlook for all of it."
                : $"Enter or right-click → {view.OpenLabel}."
            : "";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        // Typing or copying in the detail pane's text — Ctrl+C there must copy, not mark done.
        if (e.OriginalSource is TextBox || Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            return;

        var selected = (_active.SelectedItem as ItemView)?.Item;

        switch (e.Key)
        {
            case Key.Escape: Hide(); break;
            case Key.F5: _host.RefreshNow(); break;
            case Key.Left: Move(-1); break;
            case Key.Right: Move(+1); break;
            case Key.P when selected is not null && CanPlan(selected): _host.Plan(selected.Id, this); break;
            case Key.Delete when selected is not null: _host.SetStage(selected.Id, Stage.Dismissed); break;
            case Key.C when selected is not null: _host.SetStage(selected.Id, Stage.Done); break;
            case Key.S when selected is not null && selected.IsOpen && selected.Source != Source.Calendar:
                _host.Snooze(selected.Id);
                Advance(_active);   // a snoozed item stays in the list, so step past it
                break;
            case Key.Enter when selected is not null: AgentHost.OpenLink(selected.SourceRef); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Intake items, and planned non-meetings (to change the time). Meetings already have one.</summary>
    private static bool CanPlan(Item item) =>
        item.Stage == Stage.Intake || (item.Stage == Stage.Planned && item.Source != Source.Calendar);

    // ── Right-click menu ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Built fresh for whichever item is under the pointer, offering only what applies to it.
    /// Each entry shows its key, so the menu also teaches the shortcuts.
    /// </summary>
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var list = (ListBox)sender;
        if (ContainerAt(e.OriginalSource) is not { DataContext: ItemView view } container)
        {
            e.Handled = true;   // right-click on a header or empty space: no menu
            return;
        }

        container.IsSelected = true;
        _active = list;
        var item = view.Item;
        var menu = list.ContextMenu!;
        menu.Items.Clear();

        if (CanPlan(item))
            menu.Items.Add(Entry(item.Stage == Stage.Intake ? "Plan…" : "Change plan…", "P", () => _host.Plan(item.Id, this)));
        if (item.Stage != Stage.Done)
            menu.Items.Add(Entry("Mark done", "C", () => _host.SetStage(item.Id, Stage.Done)));
        else
            // Undo a Mark done. A meeting that has moved since will alert at its new time.
            menu.Items.Add(Entry("Move back to Planned", "", () => _host.SetStage(item.Id, Stage.Planned)));
        if (item.IsOpen && view.CanSnooze)
            menu.Items.Add(Entry($"Snooze {_host.SnoozeMinutes} min", "S", () => { _host.Snooze(item.Id); Advance(list); }));
        menu.Items.Add(Entry("Dismiss", "Del", () => _host.SetStage(item.Id, Stage.Dismissed)));

        // Mail only: dismiss this and stop future mail from the sender, or the whole domain —
        // except your own, where that would silence your colleagues.
        if (item.Source == Source.Mail && Senders.AddressOf(item.From) is { } address)
        {
            var ignore = new MenuItem { Header = "Dismiss and ignore sender" };
            ignore.Items.Add(Entry(address, "", () => _host.IgnoreSender(address)));
            var domain = Senders.DomainEntry(address);
            if (domain != _host.MyDomain)
                ignore.Items.Add(Entry($"Everyone at {domain}", "", () => _host.IgnoreSender(domain)));
            ignore.Items.Add(new Separator());
            ignore.Items.Add(Entry("Edit ignored senders…", "", _host.OpenIgnoredSenders));
            menu.Items.Add(ignore);

            // Reporting needs Mail.ReadWrite and a beta-only API, so rNotice doesn't do it
            // itself: it ignores and dismisses, then opens the message in Outlook, where Report
            // is one click. Nothing in the mailbox changes without you.
            if (view.HasSource)
            {
                var link = item.SourceRef;
                var spam = new MenuItem { Header = "Ignore and report spam" };
                spam.Items.Add(Entry($"{address} — then report in Outlook", "", () =>
                {
                    _host.IgnoreSender(address);
                    AgentHost.OpenLink(link);
                }));
                if (domain != _host.MyDomain)
                    spam.Items.Add(Entry($"Everyone at {domain} — then report in Outlook", "", () =>
                    {
                        _host.IgnoreSender(domain);
                        AgentHost.OpenLink(link);
                    }));
                menu.Items.Add(spam);
            }
        }

        if (view.HasJoin || view.HasSource) menu.Items.Add(new Separator());
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
    // Onto Done: complete. From Intake onto Planned: the Plan dialog. From Done onto Planned:
    // back to Planned, as an undo. Nothing else is a valid
    // drop — the cursor says so before you let go.

    private Point? _dragStart;
    private ItemView? _dragItem;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = ContainerAt(e.OriginalSource)?.DataContext as ItemView;
        _dragStart = _dragItem is null ? null : e.GetPosition(this);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || _dragItem is null) return;

        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _dragItem;
        _dragStart = null;
        _dragItem = null;
        DragDrop.DoDragDrop((ListBox)sender, new DataObject(typeof(ItemView), item), DragDropEffects.Move);
    }

    private Stage TargetStage(ListBox list) =>
        list == DoneList ? Stage.Done : list == PlannedList ? Stage.Planned : Stage.Intake;

    private bool IsValidDrop(ListBox target, DragEventArgs e) =>
        e.Data.GetData(typeof(ItemView)) is ItemView { Item: var item } && TargetStage(target) switch
        {
            Stage.Done => item.Stage != Stage.Done,
            Stage.Planned => item.Stage is Stage.Intake or Stage.Done,
            _ => false
        };

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var target = (ListBox)sender;
        var valid = IsValidDrop(target, e);
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
        Highlight(target, valid);
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => Highlight((ListBox)sender, false);

    private void OnDrop(object sender, DragEventArgs e)
    {
        var target = (ListBox)sender;
        Highlight(target, false);
        if (!IsValidDrop(target, e) || e.Data.GetData(typeof(ItemView)) is not ItemView view) return;

        // After the drag finishes, not inside it: a modal dialog opened mid-drop would hold the
        // drag loop open until it closed.
        var toDone = TargetStage(target) == Stage.Done;
        var fromDone = view.Item.Stage == Stage.Done;
        Dispatcher.BeginInvoke(() =>
        {
            if (toDone) _host.SetStage(view.Id, Stage.Done);
            else if (fromDone) _host.SetStage(view.Id, Stage.Planned);   // undo a Mark done
            else _host.Plan(view.Id, this);
        });
        e.Handled = true;
    }

    private static void Highlight(ListBox list, bool on)
    {
        if (on)
        {
            list.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB0));
            list.BorderThickness = new Thickness(2);
        }
        else
        {
            list.ClearValue(BorderBrushProperty);
            list.ClearValue(BorderThicknessProperty);
        }
    }

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

    /// <summary>The list item a mouse event happened on, if any.</summary>
    private static ListBoxItem? ContainerAt(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem item) return item;
        }
        return null;
    }

    private void Move(int step)
    {
        var index = Array.IndexOf(_lists, _active);
        var next = _lists[Math.Clamp(index + step, 0, _lists.Length - 1)];
        if (next.SelectedIndex < 0 && next.Items.Count > 0) next.SelectedIndex = 0;
        next.Focus();
        if (next.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, next.SelectedIndex)) is ListBoxItem container)
            container.Focus();
    }
}
