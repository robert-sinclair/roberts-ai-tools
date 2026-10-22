using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using rNotice.Core.Rules;

namespace rNotice.Agent;

/// <summary>
/// Edit the ignore list: undo an accidental ignore, or start hearing from someone again whose
/// role changed. Nothing is saved until Save; Cancel leaves the list as it was.
/// </summary>
public partial class IgnoredSendersWindow : Window
{
    private readonly AgentHost _host;
    private readonly ObservableCollection<string> _entries;

    internal IgnoredSendersWindow(AgentHost host)
    {
        _host = host;
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        _entries = new ObservableCollection<string>(host.IgnoredSenders.OrderBy(e => e.TrimStart('@'), StringComparer.OrdinalIgnoreCase));
        Entries.ItemsSource = _entries;
        Loaded += (_, _) => NewEntry.Focus();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (Senders.Normalise(NewEntry.Text) is not { } entry)
        {
            ShowError("Type an address (name@example.com) or a domain (@example.com).");
            return;
        }
        if (entry == _host.MyDomain)
        {
            ShowError($"That would ignore everyone at {entry}, including your own colleagues.");
            return;
        }
        if (!_entries.Contains(entry, StringComparer.OrdinalIgnoreCase)) _entries.Add(entry);
        Entries.SelectedItem = entry;
        NewEntry.Clear();
        Error.Visibility = Visibility.Collapsed;
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        foreach (var entry in Entries.SelectedItems.Cast<string>().ToList()) _entries.Remove(entry);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _host.SetIgnoredSenders(_entries.ToList());
        Close();
    }

    private void OnNewEntryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnAdd(sender, e);
        e.Handled = true;
    }

    private void OnListKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Delete or Key.Back)) return;
        OnRemove(sender, e);
        e.Handled = true;
    }

    private void ShowError(string message)
    {
        Error.Text = message;
        Error.Visibility = Visibility.Visible;
    }
}
