using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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

        var items = _host.Items();
        Fill(IntakeList, IntakeHeader, "Intake", items.Where(i => i.Stage == Stage.Intake));
        Fill(PlannedList, PlannedHeader, "Planned", items.Where(i => i.Stage == Stage.Planned));
        Fill(DoneList, DoneHeader, "Done", items.Where(i => i.Stage == Stage.Done)
            .OrderByDescending(i => i.DueUtc ?? i.ReceivedUtc).Take(50), sort: false);

        var parts = _host.Statuses.Select(s =>
            $"{s.Name}: {(s.Failing ? "⚠ " : "")}{s.Summary}" +
            (s.LastOkUtc is { } t ? $" (last ok {Display.Time(t)})" : ""));
        var who = _host.NeedsSignIn ? "Sign-in needed (tray → Sign in)" : $"Signed in as {_host.SignedInAs ?? "…"}";
        StatusText.Text = $"{who}   ·   {string.Join("   ·   ", parts)}   ·   Workbook {_host.WorkbookPath}";

        ShowDetail((_active.SelectedItem as ItemView)?.Item);
    }

    private static void Fill(ListBox list, TextBlock header, string name, IEnumerable<Item> items, bool sort = true)
    {
        var selected = (list.SelectedItem as ItemView)?.Id;
        var ordered = sort ? QueueOrder.Sort(items, DateTimeOffset.UtcNow) : items;
        var views = ordered.Select(i => new ItemView(i)).ToList();

        list.ItemsSource = views;
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
