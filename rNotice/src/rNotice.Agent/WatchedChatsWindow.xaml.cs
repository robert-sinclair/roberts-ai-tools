using System.Windows;
using System.Windows.Controls;
using rNotice.Core.Graph;
using rNotice.Core.Sources;

namespace rNotice.Agent;

/// <summary>
/// Pick the group and meeting chats rNotice watches for @mentions, and whether it watches every
/// one-on-one chat. Most recent first. Nothing is saved until Save.
/// </summary>
public partial class WatchedChatsWindow : Window
{
    private readonly AgentHost _host;
    private List<Row> _rows = [];

    internal WatchedChatsWindow(AgentHost host)
    {
        _host = host;
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        OneOnOne.IsChecked = host.WatchOneOnOneChats;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var chats = await _host.ListChatsAsync();
            var watched = _host.WatchedChatIds.ToHashSet();
            _rows = chats
                .Where(c => c.Type != ChatType.OneOnOne)
                .OrderByDescending(c => watched.Contains(c.Id))
                .ThenByDescending(c => c.LastMessageUtc ?? DateTimeOffset.MinValue)
                .Select(c => new Row(c) { Watched = watched.Contains(c.Id) })
                .ToList();
            Chats.ItemsSource = _rows;
            Status.Text = $"{_rows.Count} group and meeting chats. Ticked ones are listed first.";
            SaveButton.IsEnabled = true;
            Filter.Focus();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't list Teams chats", ex);
            Status.Text = "Couldn't read your chats. Check you're signed in, then try again.";
        }
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var text = Filter.Text.Trim();
        Chats.ItemsSource = text.Length == 0
            ? _rows
            : _rows.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Keep any picked chat the listing didn't return (left, or hidden), rather than drop it silently.
        var listed = _rows.Select(r => r.Id).ToHashSet();
        var ids = _rows.Where(r => r.Watched).Select(r => r.Id)
            .Concat(_host.WatchedChatIds.Where(id => !listed.Contains(id)))
            .ToList();
        _host.SetWatchedChats(ids, OneOnOne.IsChecked == true);
        Close();
    }

    private sealed class Row(ChatInfo chat)
    {
        public string Id { get; } = chat.Id;
        public string Name { get; } = chat.Name;
        public string Kind { get; } = chat.Type == ChatType.Meeting ? " · meeting chat" : "";
        public string When { get; } = chat.LastMessageUtc is { } t ? Display.Time(t) : "";
        public bool Watched { get; set; }
    }
}
