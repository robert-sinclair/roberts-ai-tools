using System.ComponentModel;
using System.Diagnostics;
using rMic.Core;
using rMic.Core.Ipc;

namespace rMic.Tray;

public sealed class TrayContext : ApplicationContext
{
    private const int PollIntervalMs = 2500;
    private const int StatusTimeoutMs = 3000;

    // Tearing down and rebuilding a device stack is not instant, and "all off" does it once per
    // microphone, so give a command real room before giving up on it.
    private const int CommandTimeoutMs = 30000;

    private readonly TrayIconSet _icons = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _poll;
    private readonly TraySettings _settings = TraySettings.Load();

    private readonly ToolStripMenuItem _stateItem;
    private readonly ToolStripMenuItem _allOffItem;
    private readonly ToolStripMenuItem _allOnItem;
    private readonly ToolStripMenuItem _devicesHeader;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _notificationsItem;
    private readonly ToolStripMenuItem _hotKeyItem;

    /// <summary>The per-microphone entries, kept in sync with the service's device list.</summary>
    private readonly List<ToolStripMenuItem> _deviceItems = new();

    // Sentinels chosen so they cannot collide with a real join of instance IDs.
    private const string UninitialisedKey = "<uninitialised>";
    private const string UnreachableKey = "<unreachable>";

    private string _deviceKey = UninitialisedKey;

    private HotKeyWindow? _hotKey;
    private ControlResponse? _last;

    /// <summary>Guards against overlapping pipe calls.</summary>
    private bool _busy;

    /// <summary>Greys the menu out for real commands only, not for background polling.</summary>
    private bool _commandInFlight;

    private bool _exiting;

    public TrayContext()
    {
        _stateItem = new ToolStripMenuItem("Checking service…") { Enabled = false };

        _allOffItem = new ToolStripMenuItem("Turn all microphones off", null,
            async (_, _) => await SendAsync(ControlCommand.DisableAll))
        {
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
            Enabled = false
        };

        _allOnItem = new ToolStripMenuItem("Turn all microphones on", null,
            async (_, _) => await SendAsync(ControlCommand.EnableAll))
        {
            Enabled = false
        };

        _devicesHeader = new ToolStripMenuItem("Microphones — click one to switch it") { Enabled = false };

        _startupItem = new ToolStripMenuItem("Start with Windows", null, OnStartupClicked)
        {
            CheckOnClick = true,
            Checked = AutoStart.IsEnabled()
        };
        _notificationsItem = new ToolStripMenuItem("Show notifications", null, OnNotificationsClicked)
        {
            CheckOnClick = true,
            Checked = _settings.ShowNotifications
        };
        _hotKeyItem = new ToolStripMenuItem($"Global hotkey ({HotKeyWindow.Description})", null, OnHotKeyClicked)
        {
            CheckOnClick = true,
            Checked = _settings.HotKeyEnabled
        };

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _stateItem,
            _allOffItem,
            _allOnItem,
            new ToolStripSeparator(),
            _devicesHeader,
            // Per-microphone entries are inserted here.
            new ToolStripSeparator(),
            _startupItem,
            _notificationsItem,
            _hotKeyItem,
            new ToolStripMenuItem("Open log folder", null, (_, _) => OpenLogFolder()),
            BuildServiceMenu(),
            new ToolStripSeparator(),
            new ToolStripMenuItem("About rMic", null, (_, _) => ShowAbout()),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitTray())
        });

        _notifyIcon = new NotifyIcon
        {
            Icon = _icons[MicIconState.Unknown],
            Text = "rMic",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.MouseDoubleClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                await SendAsync(ControlCommand.ToggleAll);
        };
        _notifyIcon.BalloonTipClicked += (_, _) => _notifyIcon.ContextMenuStrip?.Show(Cursor.Position);

        ApplyHotKeySetting();

        // Fire almost immediately, then settle into the normal cadence. Driving the first read
        // off the timer keeps every status update on the UI thread, where the icon lives.
        _poll = new System.Windows.Forms.Timer { Interval = 100 };
        _poll.Tick += async (_, _) =>
        {
            if (_poll.Interval != PollIntervalMs)
                _poll.Interval = PollIntervalMs;
            await RefreshAsync();
        };
        _poll.Start();
    }

    private static ToolStripMenuItem BuildServiceMenu()
    {
        var item = new ToolStripMenuItem("Service");
        item.DropDownItems.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem("Start service (admin)", null, (_, _) => RunServiceControl("start")),
            new ToolStripMenuItem("Restart service (admin)", null, (_, _) => RunServiceControl("restart")),
            new ToolStripMenuItem("Stop service (admin)", null, (_, _) => RunServiceControl("stop"))
        });
        return item;
    }

    private async Task RefreshAsync()
    {
        if (_busy || _exiting)
            return;

        _busy = true;
        try
        {
            try
            {
                var response = await ControlClient.SendAsync(
                    ControlCommand.Status, timeoutMs: StatusTimeoutMs);
                Update(response, response.Ok ? null : response.Error);
            }
            catch (Exception ex)
            {
                Update(null, ex.Message);
            }
        }
        finally
        {
            _busy = false;
            ApplyEnabledStates();
        }
    }

    /// <summary>Runs one command against the service and folds the result back into the menu.</summary>
    private async Task SendAsync(ControlCommand command, string? instanceId = null)
    {
        if (_busy || _exiting)
            return;

        _busy = true;
        _commandInFlight = true;
        ApplyEnabledStates();
        try
        {
            var response = await ControlClient.SendAsync(command, instanceId, CommandTimeoutMs);
            Update(response, response.Ok ? null : response.Error);

            if (!response.Ok)
                ShowError("rMic could not switch every capture device.\r\n\r\n" + response.Error);
            else if (_settings.ShowNotifications)
                _notifyIcon.ShowBalloonTip(2000, "rMic", Describe(instanceId, response), ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Update(null, ex.Message);
            ShowError(
                "rMic could not reach the rMic service.\r\n\r\n" +
                $"{ex.Message}\r\n\r\n" +
                "Use Service → Start service (admin) from the tray menu.");
        }
        finally
        {
            _busy = false;
            _commandInFlight = false;
            ApplyEnabledStates();
        }
    }

    private static string Describe(string? instanceId, ControlResponse response)
    {
        if (instanceId is not null)
        {
            var device = response.Devices.FirstOrDefault(d =>
                string.Equals(d.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

            if (device is not null)
                return $"{device.Name}: {(device.Enabled ? "enabled" : "disabled")}.";
        }

        if (response.Devices.Count == 0)
            return "No microphones detected.";

        return response.AllDisabled
            ? "All microphones disabled at the driver level."
            : response.AllEnabled
                ? "All microphones enabled."
                : $"{response.DisabledCount} of {response.Devices.Count} microphones disabled.";
    }

    private void Update(ControlResponse? response, string? error)
    {
        _last = response;

        if (response is null)
        {
            _notifyIcon.Icon = _icons[MicIconState.Unknown];
            _notifyIcon.Text = Truncate("rMic — service unavailable");
            _stateItem.Text = "Service not reachable";

            // Swap in the placeholder once, not on every poll, and never while the menu is up.
            if (_deviceKey != UnreachableKey && !_menu.Visible)
            {
                ClearDeviceItems();
                var placeholder = NewDisabledItem(error ?? "No status");
                _deviceItems.Add(placeholder);
                _menu.Items.Insert(_menu.Items.IndexOf(_devicesHeader) + 1, placeholder);
                _deviceKey = UnreachableKey;
            }

            ApplyEnabledStates();
            return;
        }

        var state = response.Devices.Count == 0
            ? MicIconState.Unknown
            : response.AllEnabled
                ? MicIconState.On
                : response.AllDisabled
                    ? MicIconState.Off
                    : MicIconState.Mixed;

        _notifyIcon.Icon = _icons[state];
        _notifyIcon.Text = Truncate("rMic — " + Summary(response, state));
        _stateItem.Text = Capitalise(Summary(response, state)) +
                          (state == MicIconState.Off && response.AllOffMode && response.Enforcing
                              ? " (enforced)"
                              : string.Empty);

        SyncDeviceItems(response.Devices);

        if (!string.IsNullOrEmpty(error))
            _stateItem.Text += " — last command reported errors";

        ApplyEnabledStates();
    }

    private static string Summary(ControlResponse response, MicIconState state)
    {
        var count = response.Devices.Count;
        if (count == 0)
            return "no microphones detected";

        var noun = count == 1 ? "microphone" : "microphones";

        return state switch
        {
            MicIconState.On => count == 1 ? "microphone enabled" : "all microphones enabled",
            MicIconState.Off => count == 1 ? "microphone disabled" : "all microphones disabled",
            _ => $"{response.DisabledCount} of {count} {noun} disabled"
        };
    }

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];

    /// <summary>
    /// Keeps the per-microphone entries matching the service's list, rebuilding only when the
    /// set of devices actually changes so the menu is not disturbed on every poll.
    /// </summary>
    private void SyncDeviceItems(List<DeviceInfo> devices)
    {
        var key = string.Join("|", devices.Select(d => d.InstanceId));

        if (key != _deviceKey)
        {
            // Adding or removing entries while the menu is on screen would move items under the
            // pointer mid-click. A later poll picks the change up.
            if (_menu.Visible)
                return;

            ClearDeviceItems();
            var index = _menu.Items.IndexOf(_devicesHeader) + 1;

            if (devices.Count == 0)
            {
                _deviceItems.Add(NewDisabledItem("(no microphones detected)"));
            }
            else
            {
                foreach (var device in devices)
                {
                    var item = new ToolStripMenuItem(device.Name) { Tag = device.InstanceId };
                    item.Click += async (sender, _) =>
                    {
                        if (sender is ToolStripMenuItem { Tag: string id })
                            await SendAsync(ControlCommand.ToggleDevice, id);
                    };
                    _deviceItems.Add(item);
                }
            }

            foreach (var item in _deviceItems)
                _menu.Items.Insert(index++, item);

            _deviceKey = key;
        }

        for (var i = 0; i < _deviceItems.Count && i < devices.Count; i++)
        {
            var item = _deviceItems[i];
            var device = devices[i];

            // A tick means the microphone is live. Set from the service's answer rather than
            // from the click, so the menu never claims a change that did not happen.
            item.Checked = device.Enabled;
            item.ToolTipText =
                (device.Enabled ? "Enabled — click to disable it" : "Disabled — click to enable it")
                + Environment.NewLine + device.InstanceId;
        }
    }

    private void ClearDeviceItems()
    {
        foreach (var item in _deviceItems)
        {
            _menu.Items.Remove(item);
            item.Dispose();
        }
        _deviceItems.Clear();
    }

    private static ToolStripMenuItem NewDisabledItem(string text) =>
        new(text) { Enabled = false };

    private void ApplyEnabledStates()
    {
        var response = _last;

        _allOffItem.Enabled = !_commandInFlight && response is { AnyEnabled: true };
        _allOnItem.Enabled = !_commandInFlight && response is { AnyDisabled: true };

        foreach (var item in _deviceItems)
            item.Enabled = !_commandInFlight && item.Tag is string;
    }

    private static string Truncate(string text) =>
        // NotifyIcon.Text is capped at 63 characters.
        text.Length <= 63 ? text : text[..63];

    private void OnStartupClicked(object? sender, EventArgs e)
    {
        if (!AutoStart.Set(_startupItem.Checked))
        {
            _startupItem.Checked = !_startupItem.Checked;
            ShowError("rMic could not update the Windows startup entry.");
        }
    }

    private void OnNotificationsClicked(object? sender, EventArgs e)
    {
        _settings.ShowNotifications = _notificationsItem.Checked;
        _settings.Save();
    }

    private void OnHotKeyClicked(object? sender, EventArgs e)
    {
        _settings.HotKeyEnabled = _hotKeyItem.Checked;
        _settings.Save();
        ApplyHotKeySetting();
    }

    private void ApplyHotKeySetting()
    {
        if (!_settings.HotKeyEnabled)
        {
            _hotKey?.Dispose();
            _hotKey = null;
            return;
        }

        _hotKey ??= new HotKeyWindow(() => _ = SendAsync(ControlCommand.ToggleAll));

        if (!_hotKey.Register())
        {
            _hotKeyItem.Checked = false;
            _settings.HotKeyEnabled = false;
            _settings.Save();
            _hotKey.Dispose();
            _hotKey = null;
            ShowError($"Another application already owns {HotKeyWindow.Description}, so the global hotkey is off.");
        }
    }

    private static void OpenLogFolder()
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.DataDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError($"Could not open {AppPaths.DataDirectory}.\r\n\r\n{ex.Message}");
        }
    }

    private static void RunServiceControl(string action)
    {
        // sc.exe has no "restart", so drive it as a stop followed by a start.
        var command = action == "restart"
            ? $"/c sc.exe stop {Protocol.ServiceName} & sc.exe start {Protocol.ServiceName}"
            : $"/c sc.exe {action} {Protocol.ServiceName}";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = command,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Win32Exception)
        {
            // The user dismissed the UAC prompt.
        }
        catch (Exception ex)
        {
            ShowError($"Could not {action} the {Protocol.ServiceName} service.\r\n\r\n{ex.Message}");
        }
    }

    private void ShowAbout()
    {
        var version = typeof(TrayContext).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var serviceVersion = _last?.ServiceVersion is { Length: > 0 } v ? v : "not connected";

        MessageBox.Show(
            $"""
             rMic {version}   (service: {serviceVersion})

             Switching a microphone off disables that audio capture device at the driver
             level, exactly as Device Manager would. Applications cannot capture from a
             device that is not there, and they cannot undo it the way they can undo a mute.

             Click any microphone in the menu to switch just that one, or use the
             all on / all off entries. Turning them all off also covers microphones
             plugged in afterwards, until you switch one back on.

             This is not a physical disconnection. Only a hardware kill switch can
             guarantee that, and rMic cannot substitute for one.

             The state persists across reboots and survives closing this tray icon.
             Switch your microphones back on before exiting if that is not what you want.

             Hotkey: {HotKeyWindow.Description} (all on / all off)
             Log: {AppPaths.LogFile}
             """,
            "About rMic",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ExitTray()
    {
        if (_last is { AnyDisabled: true } state)
        {
            var which = state.AllDisabled
                ? "Every microphone is currently disabled"
                : $"{state.DisabledCount} of {state.Devices.Count} microphones are currently disabled";

            var choice = MessageBox.Show(
                $"{which}, and they will stay disabled after you exit, because the rMic " +
                "service holds that state.\r\n\r\nExit anyway?",
                "rMic",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (choice != DialogResult.Yes)
                return;
        }

        _exiting = true;
        ExitThread();
    }

    private static void ShowError(string message) =>
        MessageBox.Show(message, "rMic", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Stop();
            _poll.Dispose();
            _hotKey?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _icons.Dispose();
        }
        base.Dispose(disposing);
    }
}
