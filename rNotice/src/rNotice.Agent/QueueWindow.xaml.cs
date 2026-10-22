using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using rNotice.Core.Items;

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
        IsVisibleChanged += (_, _) => { if (IsVisible) Reload(); };
        PreviewKeyDown += OnKey;
        Reload();
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

        ShowDetail((_active.SelectedItem as ItemView)?.Item);
    }

    /// <summary>
    /// Intake and Planned are grouped under day headers — Today, Tomorrow, Fri 24 Oct. Done is a
    /// flat list, most recent first.
    /// </summary>
    private void Fill(ListBox list, TextBlock header, string name, IEnumerable<Item> items, bool byDay)
    {
        var selected = (list.SelectedItem as ItemView)?.Id;
        var views = items.Select(i => new ItemView(i)).ToList();

        var view = new ListCollectionView(views);
        if (byDay)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ItemView.Day)));
            if (list.GroupStyle.Count == 0) list.GroupStyle.Add((GroupStyle)FindResource("ByDay"));
        }

        list.ItemsSource = view;
        list.SelectedItem = views.FirstOrDefault(v => v.Id == selected);
        header.Text = $"{name} ({views.Count})";
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowDetail(((sender as ListBox)?.SelectedItem as ItemView)?.Item);

    private void OnListFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _active = (ListBox)sender;
        ShowDetail((_active.SelectedItem as ItemView)?.Item);
    }

    private void ShowDetail(Item? item)
    {
        Detail.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item is null) return;

        var view = new ItemView(item);
        DetailTitle.Text = view.Title;
        DetailMeta.Text = view.Meta;
        DetailReason.Text = view.Reason;
        DetailSnippet.Text = view.Snippet ?? "";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var selected = (_active.SelectedItem as ItemView)?.Item;

        switch (e.Key)
        {
            case Key.Escape: Hide(); break;
            case Key.F5: _host.RefreshNow(); break;
            case Key.Left: Move(-1); break;
            case Key.Right: Move(+1); break;
            case Key.P when selected is not null: _host.SetStage(selected.Id, Stage.Planned); break;
            case Key.D when selected is not null: _host.SetStage(selected.Id, Stage.Dismissed); break;
            case Key.C when selected is not null: _host.SetStage(selected.Id, Stage.Done); break;
            case Key.S when selected is not null: _host.Snooze(selected.Id); break;
            case Key.Enter when selected is not null: AgentHost.OpenLink(selected.SourceRef); break;
            default: return;
        }
        e.Handled = true;
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
