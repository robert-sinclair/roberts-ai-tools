# rMic

A Windows tray icon that switches microphones off at the **driver level** — the same operation as
Device Manager → *Audio inputs and outputs* → right-click → **Disable** — and puts them back with
one click.

Every microphone on the machine is listed in the right-click menu. Click one to switch that one,
or use *all off* / *all on*. A disabled capture endpoint is gone from the OS: applications do not
see a muted microphone they could unmute, they see no microphone at all.

```
Settings ▸ Personalization ▸ Taskbar ▸ Other system tray icons ▸ rMic ▸ On
```

## What this is and is not

**It is** the strongest microphone cut-off reachable from software: the device is torn down and
marked disabled in the registry, the state survives reboots, and a background service re-asserts
it if a held device is re-enabled behind your back — or, after *turn all off*, when a new
microphone is plugged in.

**It is not** a physical disconnection. No software can cut power to a microphone that has no
hardware kill switch. If your threat model requires a guarantee that no electrical path exists,
you need hardware that provides one — a laptop with a mic-mute switch, or an external mic you can
unplug. rMic removes the microphone from the operating system; it does not remove it from the
motherboard.

What it does buy you over the Windows mute or privacy toggles: a muted endpoint is still a live
endpoint, and any application with microphone permission can unmute it. A disabled device cannot
be captured from at all, and nothing short of an elevated process can bring it back.

## How it works

Two pieces, because the privileged work and the UI have very different security requirements:

| Component | Runs as | Does |
| --- | --- | --- |
| `rMic.Service` | LocalSystem, auto-start | Enumerates audio capture devices via SetupAPI, enables/disables them (`DIF_PROPERTYCHANGE`), persists the desired state, re-asserts it on a timer |
| `rMic.Tray` | Your account, unelevated | The tray icon and menu. Owns no privileges; asks the service over a named pipe |

Changing device state requires administrator rights. Rather than putting a UAC prompt in front of
every toggle — or leaving a permanently elevated process in your tray — the privileged half is a
service, and the tray talks to it over `\\.\pipe\rMic.Control`. You elevate once, at install
time.

The pipe carries an explicit ACL: full control for LocalSystem and Administrators, read/write for
`INTERACTIVE`. That last one is deliberate and worth understanding — **anyone logged on
interactively to this machine can toggle the microphone**, including over RDP, without elevating.
That is the point of a one-click switch, but if you share the machine and would rather only
administrators could turn the microphone back *on*, narrow that rule in
`src/rMic.Service/ControlServer.cs`.

### Which devices get switched

Windows audio endpoints have instance IDs of the form:

```
SWD\MMDEVAPI\{0.0.1.00000000}.{e1f3a2b4-...}
                    ↑
                    0 = render (speakers), 1 = capture (microphones)
```

rMic matches `{0.0.1.` and leaves everything else alone, so your speakers and headphones are
untouched. This covers built-in mic arrays, USB microphones, webcam mics and Bluetooth headsets
uniformly, because they all surface as capture endpoints.

Endpoints are targeted rather than the parent codec device (e.g. "Realtek(R) Audio") on purpose:
disabling the codec would take your speakers down with it.

That choice has one honest limit. Disabling the endpoint removes the microphone from the Windows
audio engine, which is what every normal application — Teams, Zoom, browsers, anything using
WASAPI or the older APIs — goes through. It does not tear down the underlying kernel streaming
filter on the parent device, so software deliberately written to bind to the capture pin at the
driver level could still reach the hardware. That is not something ordinary applications do, but
if it is in your threat model, add the parent device to `ExtraDeviceClassGuids` (see
[Configuration](#configuration)) and accept losing audio output along with input.

## Install

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build, and the .NET
8 **Desktop** Runtime to run. Use `-SelfContained` to bundle the runtime and depend on neither.

From an **elevated** PowerShell prompt:

```powershell
git clone https://github.com/robert-sinclair/roberts-ai-tools
cd roberts-ai-tools
.\install\Install.ps1
```

That publishes both projects, installs to `%ProgramFiles%\rMic`, registers and starts the
service, adds the tray to your startup items, and launches it.

New tray icons land in the overflow menu. To pin it to the taskbar itself, flip it **On** under
*Settings → Personalization → Taskbar → Other system tray icons*.

```powershell
.\install\Install.ps1 -SelfContained   # no runtime prerequisite
.\install\Install.ps1 -NoAutoStart     # do not add to Windows startup
.\install\Uninstall.ps1                # re-enables devices, then removes everything
.\install\Uninstall.ps1 -Purge         # also delete config, state and logs
```

Uninstall re-enables **every** microphone before removing anything — including any that were
already disabled before rMic was installed. Removing the component that holds the switch must
never leave you with a dead microphone.

## Using it

Right-click the tray icon and every microphone on the machine is listed. **Click one to switch
just that one** — a tick means it is live. Above the list are *Turn all microphones off* and
*Turn all microphones on*, so one, two, all or none is a single click either way.

```
All microphones enabled
Turn all microphones off          ← bold, the primary action
Turn all microphones on
──────────────────────────
Microphones — click one to switch it
  ✓ Microphone Array (Realtek Audio)
    USB Condenser Mic                 ← no tick: disabled at the driver level
  ✓ Headset (Bluetooth)
──────────────────────────
Start with Windows · Show notifications · Global hotkey
Open log folder · Service · About · Exit
```

- **Left double-click** the icon: all off if anything is still live, otherwise all on.
- **Ctrl+Alt+Shift+M** does the same from anywhere. Turn it off in the menu if it collides.

Icon states: **green** all live, **amber** some disabled, **red** all disabled, **grey** service
not reachable. Grey deliberately carries no strike-through — a struck-through icon would assert
the microphone is off, which is the one thing that is not known in that state.

The ticks are set from the service's answer, never optimistically from your click, so the menu
cannot claim a change that did not happen.

### "All off" also covers what you plug in next

Turning everything off sets a standing instruction, so a USB mic or headset connected afterwards
is disabled as it appears (checked every 5 seconds). Turning any *single* microphone back on
clears that instruction — at that point you have expressed something more specific than "nothing
may listen", and rMic stops blanket-disabling new arrivals. Devices switched off individually
stay off regardless; they are held by name.

State is per device and persistent. It survives reboots, and it survives exiting the tray icon —
the service holds it, not the UI. The tray warns you on exit if anything is still disabled.

## Command line

`rMic.Service.exe` doubles as a diagnostic tool. It routes through the running service when
there is one, so state never drifts, and only touches devices directly when the service is
stopped (which then requires elevation).

```powershell
$rmic = "$env:ProgramFiles\rMic\rMic.Service.exe"

& $rmic --list                      # devices rMic will switch, with their instance IDs
& $rmic --list-all                  # everything, for writing config patterns
& $rmic --status                    # state of every capture device
& $rmic --disable-all
& $rmic --enable-all
& $rmic --console                   # run in the foreground

# One device at a time. Instance IDs come from --list and contain backslashes, so quote them.
& $rmic --disable-device 'SWD\MMDEVAPI\{0.0.1.00000000}.{a1b2c3d4-...}'
& $rmic --enable-device  'SWD\MMDEVAPI\{0.0.1.00000000}.{a1b2c3d4-...}'
& $rmic --toggle-device  'SWD\MMDEVAPI\{0.0.1.00000000}.{a1b2c3d4-...}'
```

`--disable` and `--enable` are accepted as aliases for the `-all` forms.

## Configuration

`%ProgramData%\rMic\config.json`, written with defaults on first run:

| Key | Default | Meaning |
| --- | --- | --- |
| `CaptureInstanceIdPatterns` | `^SWD\\MMDEVAPI\\\{0\.0\.1\.` | Regexes; a device is in scope if any matches |
| `ExcludeInstanceIdPatterns` | `[]` | Regexes for devices to leave alone |
| `ExtraDeviceClassGuids` | `[]` | Extra setup classes to scan beyond AudioEndpoint |
| `EnforceIntervalSeconds` | `5` | How often to re-assert the off state |
| `EnforceWhenDisabled` | `true` | `false` makes "off" a one-shot action instead of a held state |

To also disable a USB microphone's parent device node, add the MEDIA class
`{4d36e96c-e325-11ce-bfc1-08002be10318}` to `ExtraDeviceClassGuids` and a matching include
pattern (get the instance ID from `--list-all`). Restart the service afterwards.

State lives in `state.json` alongside it: `DisabledDevices` is the list of instance IDs being
held off, and `AllOffMode` records whether new arrivals should be caught too. Devices not in that
list are never touched, so a microphone you disabled yourself in Device Manager stays disabled
until you tick it in the menu or run `--enable-all`.

## Troubleshooting

**Nothing happens / "no capture devices matched".** Run `--list`. If it is empty, your endpoints
do not match the default pattern; run `--list-all` and add one that does.

**A device fails to switch.** Some virtual audio drivers refuse property changes. The tray reports
which device and why, and `%ProgramData%\rMic\service.log` has the detail. Everything else
still switches.

**The tray shows grey.** The service is not running. Use *Service → Start service (admin)* in the
tray menu, or `Start-Service rMic` from an elevated prompt.

**A microphone is disabled and rMic is gone.** Device Manager → *Audio inputs and outputs* →
right-click the device → **Enable**. Or reinstall and switch it on. `Uninstall.ps1` re-enables
everything for you, which is why it should be preferred over deleting the folder.

## Layout

```
src/rMic.Core/       Device control (SetupAPI interop), config, state, IPC contracts
src/rMic.Service/    LocalSystem service, named-pipe server, CLI
src/rMic.Tray/       WinForms tray client
install/                Install.ps1, Uninstall.ps1
```
