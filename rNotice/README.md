# rNotice

A Windows desktop assistant that watches four places work arrives — your Outlook inbox, your
calendar, named Teams chats and a task workbook in OneDrive — and runs everything through one
flow: **intake → plan → complete**.

Full design in [`SPEC.md`](SPEC.md). This README covers installing and running what exists.

---

## Build 0.1 — the permission probe

**This build does one thing: it tells you which Microsoft Graph permissions actually work.**

Five delegated permissions were requested from IT. The registration exists, but there is no way
to know from outside which were consented to — and every later build step depends on the answer.
So the first thing rNotice does is ask.

It is **read-only**. It writes nothing to your mailbox, your calendar or your OneDrive. It opens
no windows, installs no service and starts nothing at logon.

Expected output:

```
  rNotice probe 0.1.0
  Tenant    bravurasecurity.com
  Client ID 2923549f-f444-48f7-8084-c8b7799db2e0

  Read-only. This build writes nothing to your mailbox, calendar or OneDrive.

    Check           Permission            Result
    ─────────────────────────────────────────────────────────
  ✓ Sign-in         —                     robert.sinclair@… (silent)
  ✓ Profile         User.Read             Robert Sinclair
  ✓ Calendar        Calendars.ReadWrite   12 events in the next 7 days
  ✗ Mail            Mail.Read             Not consented. An administrator has not granted this permission.
  ✓ Teams chats     Chat.Read             5 chats visible
  ✓ Working hours   MailboxSettings.Read  3 item(s)
  ✓ OneDrive folder Files.ReadWrite       0 items in /efficiency

  6 of 7 checks passed.
```

**A red row is a result, not a bug.** It means that permission was not consented to. Send the
table to IT — it is a far more specific ask than the original request.

---

## Requirements

| | |
| --- | --- |
| OS | Windows 10 version 1809 or later, or Windows Server 2019+ |
| Runtime | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) — or install with `-SelfContained` and skip it |
| To build | .NET 8 SDK |
| Account | Signed in to Windows with your Bravura Security account |

Windows 10 1809 is the floor because sign-in goes through the Windows broker (WAM). That is
deliberate: it means rNotice authenticates the same way Outlook does, inheriting Conditional
Access, device compliance and Windows Hello rather than sidestepping them.

## Install

Installs **for all users**, into `%ProgramFiles%\rNotice`. From an **elevated** PowerShell prompt:

```powershell
cd rNotice\install
.\Install.ps1
```

Add `-SelfContained` to bundle the .NET runtime, and `-Launch` to run the probe straight after.

**Installing is the only step that needs administrator rights.** rNotice itself runs unelevated,
as you. It has no service, no driver and no scheduled task.

### Machine-wide binaries, per-user state

The program files are shared. Everything else is not:

| What | Where | Scope |
| --- | --- | --- |
| Binaries | `%ProgramFiles%\rNotice` | All users |
| Start Menu shortcut | All Users Start Menu | All users |
| Sign-in / token cache | Windows broker, per Windows account | Per user |
| Settings, logs, local store | `%LOCALAPPDATA%\rNotice` | Per user |

So every user who runs rNotice signs in as themselves and gets their own data. Nobody sees
anybody else's.

> One consequence worth knowing: the app registration is currently restricted to a single
> assigned user. Another person on this machine can install and launch rNotice, and will then be
> refused at sign-in with *"your account is not assigned to this application"*. That is the
> restriction working, not a fault.

## First run

Run it **as yourself**, not from the elevated prompt you installed from — otherwise the broker
caches the administrator account instead of yours.

- Start Menu → **rNotice**, or
- `& "$env:ProgramFiles\rNotice\rNotice.Probe.exe"`

The first run may show a Windows sign-in prompt. After that the broker signs you in silently and
no prompt appears again.

Exit codes: `0` all checks passed, `1` one or more failed, `2` cancelled.

### Pointing it somewhere else

No rebuild needed:

| Variable | Default |
| --- | --- |
| `RNOTICE_CLIENT_ID` | `2923549f-f444-48f7-8084-c8b7799db2e0` |
| `RNOTICE_TENANT` | `bravurasecurity.com` |
| `RNOTICE_WORKBOOK_FOLDER` | `efficiency` |

The client ID is not a secret. A public client registration has no secret and no certificate, and
the client ID ships inside the binary by design.

## Upgrade

**Re-run `Install.ps1`.** It stops anything running, replaces the files in place and updates the
version shown in Add or Remove Programs. There is no separate upgrade path and no need to
uninstall first.

Your settings, logs and sign-in state are in your own profile and are never touched by an
upgrade. Other users' data is likewise untouched.

The version comes from `<Version>` in `src/rNotice.Probe/rNotice.Probe.csproj` — bump it there
and the installer picks it up. Nothing else needs editing.

## Uninstall

**Settings → Apps → Installed apps → rNotice → Uninstall.** Or from an elevated prompt:

```powershell
& "$env:ProgramFiles\rNotice\Uninstall.ps1"
```

Add `-Purge` to also delete your own `%LOCALAPPDATA%\rNotice`. Without it, your settings and logs
are left in place and other users keep theirs — removing other people's data from a machine-wide
uninstall would be the wrong default.

---

## Troubleshooting

**"Not consented. An administrator has not granted this permission."**
That permission was not approved. Expected for some rows. Send the probe output to IT; the
staged request is in [`IT-CONFIGURATION.md`](IT-CONFIGURATION.md).

**"Redirect URI mismatch."**
The registration is missing `ms-appx-web://Microsoft.AAD.BrokerPlugin/<client-id>`, or it has a
typo. Redirect URIs are matched by exact string comparison, so a single changed character breaks
it. See step 3 of [`IT-CONFIGURATION.md`](IT-CONFIGURATION.md).

**"Sign-in blocked: your account is not assigned to this application."**
The app has *Assignment required* on and you are not in its Users and groups list. Deliberate —
ask IT to add you.

**"The Windows broker could not be reached."**
Windows 10 1809+ is required, and the broker is unavailable over some remote sessions. Try from
a local sign-in.

**Signed in as the wrong account.**
You launched it from an elevated prompt. Close it, sign out of the account in Windows Settings →
Accounts → Email & accounts if it stuck, and run the probe as yourself.

**`dotnet publish` fails to restore `Microsoft.Identity.Client`.**
The package references float on `4.*` for this spike. If your environment needs exact versions,
run `dotnet add package Microsoft.Identity.Client` and the same for
`Microsoft.Identity.Client.Broker`, then pin both.

---

## What this build is not

No tray icon, no alert window, no takeover, no queue, no MCP server, no Claude skill. None of
that exists yet.

The probe ran on 2026-10-21: all five permissions usable.

## rNotice.Core (build step 2)

A library, not something you install. Item model, intake rules for all four sources, the
scheduler with busy and quiet-hours suppression, and the SQLite store with DPAPI-encrypted
content columns. No network, no UI, no model calls.

```powershell
dotnet test tests\rNotice.Tests
```

The workbook write path is proven: the documented `rows/{index}` route returns `ApiNotFound`, and
`rows/itemAt(index=)` works (details in `SPEC.md`, *Writes*). Reply matching on
`conversationId` caught 24 of 24 real replies with no false alarms, once two fixes were added
(details in `SPEC.md`, *Classification*). One risky path is still unproven: whether Teams
defeats `WDA_EXCLUDEFROMCAPTURE`.
