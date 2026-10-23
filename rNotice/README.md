# rNotice

A Windows desktop assistant that watches four places work arrives — your Outlook inbox, your
calendar, named Teams chats and a task workbook in OneDrive — and runs everything through one
flow: **intake → plan → complete**.

Full design in [`SPEC.md`](SPEC.md). This README covers installing and running what exists.

---

## Build 0.4 — the agent: calendar, workbook and mail

`rNotice.exe` lives in the notification area and watches three sources: your Outlook calendar,
the `Actions` table in `/efficiency/rNotice.xlsx`, and your Outlook inbox. Rules decide
priority; there is no model call. It doesn't read Teams yet.

**Mail is read-only.** rNotice reads your inbox and Sent Items. It never sends, replies,
moves, flags or marks anything read, and it holds no scope that could. What reaches the queue:

| Mail | Becomes |
| --- | --- |
| A reply on a thread you've sent in | P1, a toast in the corner |
| From someone on `rules.vipSenders` | P1, an alert |
| Sent to you alone on the To line | One priority step up |
| Anything else | P3, quietly in Intake |
| Sent by you, meeting invites and responses, `rules.ignoreSenders` | Nothing |

**Read in Outlook is noted, never acted on.** Unread mail has a bold title, read mail a normal
one, and the item says *(read in Outlook)* or *(unread)*. Reading a message doesn't close its
item — you do that.

**The detail pane** under the lists shows the selected item: title, when, who, why it got its
priority, and the start of the message. Drag the bar above it to make it taller; it scrolls when
there's more than fits, and the text can be selected and copied. rNotice keeps only the first
couple of hundred characters of a message, so Enter opens the rest in Outlook.

On first run it reads the last 3 days (`mailBackfillDays`) and puts them in the queue without
toasting. Only mail that arrives while it's running interrupts you. A message you delete or
move out of the inbox drops out of Intake.

**Reporting spam.** Right-click a mail item → **Ignore and report spam** → the address, or
everyone at their domain. rNotice ignores and dismisses as above, then opens the message in
Outlook so you click **Report** there. rNotice doesn't report it itself: Graph's report API is
beta-only and needs `Mail.ReadWrite`, a permission that could change or delete any of your mail.

**Ignoring a sender.** Right-click a mail item → **Dismiss and ignore sender** → the address,
or everyone at their domain (not offered for your own domain). That dismisses every Intake item
from them and skips their future mail. **Tray → Ignored senders…** lists them: add, remove,
Save. Taking someone off re-reads the last few days of mail, so their recent messages come back
into the queue quietly. Items dismissed by the ignore stay dismissed. The list is
`rules.ignoreSenders` in `settings.json`; rNotice won't save over that file if it couldn't read
it.

**What it writes:** one thing, once. If `/efficiency/rNotice.xlsx` doesn't exist, it creates it
with the `Actions` table and the date columns formatted as text. It never writes a row, never
touches your calendar, and never acknowledges anything back to the workbook — that's step 4.

| | |
| --- | --- |
| Tray icon | Blue: fine. Orange: a P1 or P2 needs you now. Grey `!`: sign-in needed, or a source is failing. |
| Queue | Double-click the tray icon, or **Ctrl+Alt+Shift+N**. Three columns: Intake (workbook rows waiting on a decision), Planned (meetings land here, already scheduled), Done. Intake and Planned are grouped by day — Today, Tomorrow, then dates. A meeting's badge shows its start time; everything else shows its priority. Who you're signed in as and when each source was last checked are in the footer. |
| Keys | **P** plan · **Delete** dismiss · **C** mark done · **S** snooze · **Enter** open in Outlook/Excel · **←/→** column · **F5** refresh · **Esc** hide. After P, Delete, C or S the next item is selected, so you can work down a column without the mouse. |
| Mouse | Right-click an item for the same actions, plus Join for meetings. Drag onto **Done** to complete. Drag from **Intake** onto **Planned** to plan. |
| Planning | P, Plan… in the menu, a drag into Planned and the alert's Plan button all open one dialog. **Left:** pick how long (15 min to 2 h, default 30) and it lists the next open slots in your work hours — on the quarter hour, clear of meetings and other plans, finishing by the end of the day, one per free gap. **Right:** the day, quarter hour by quarter hour: meetings and other plans filled in and named, the past greyed out, your current plan dashed if you're re-planning. ◀ ▶ move between working days. Click a suggestion to see it there, or pick your own: **click a free quarter hour, drag for longer, Shift+click to extend** — a selection stops at anything booked. Enter plans what's selected (the first suggestion, when it opens). A time sets a reminder from rNotice; nothing goes on your calendar yet. |
| Work hours | `workHours` in `settings.json`: 08:00–16:00, Monday to Friday, in *Canada Central Standard Time* (UTC−6 all year). Planning never suggests a slot outside them. |
| Alert window | Topmost, doesn't steal focus, flashes the taskbar. For a workbook item: Plan…, Mark done, Snooze, Open in Excel. |
| Meetings | Every meeting not on `meetingBlocklist` alerts 60 seconds before it starts, in the large form: 60% of the monitor's width, centred, the title in big type and a countdown ticking every second. Buttons: **Join**, **Open in Outlook**, **Mark done**. No snooze — at T−1 it doesn't help. The full-screen takeover is step 4. **Tray → Preview meeting alert** shows the next meeting's alert now, without using it up. |
| Autostart | First run of the installed copy adds you to your own startup apps. Untick **Start with Windows** in the tray menu to stop it. |

Times are shown local first, UTC beside them.

**Each item's second line starts with who it's from**, then when, then the rest:
*Email from: Andreea Oncescu - Received 13:32 · 19:32 UTC - andreea.oncescu@bravurasecurity.com - read in Outlook*.
Colleagues are named first with their address at the end; anyone outside Bravura shows
*Name &lt;address&gt;* together, since the domain is often what tells you who they are. Meetings say
*Meeting from: organiser*, or just *Meeting* when you organised it. Chats will follow the same
shape (*Chat from: …*).

**Queue order.** In each column, items that need you now come first, by priority. Upcoming ones
follow, soonest first. Every meeting is P1, so ordering by priority alone would bury today's
meetings under next week's.

**Settings** are in `%LOCALAPPDATA%\rNotice\settings.json`, written with defaults on first run.
Edit, then restart rNotice from the tray. The useful ones: `rules.meetingBlocklist` (subject
text, or `/regex/`), `rules.horizonDays`, `quietHours` (`{"start":"22:00","end":"07:00"}`),
`snoozeMinutes`, `refreshSeconds` (how often the calendar and workbook are checked; default 60,
minimum 30).

**Auto refresh.** Both sources are checked every minute. The queue footer counts down to the next
check, shows *Refreshing…* while one runs, and keeps your scroll position when the lists update.
F5 checks straight away. After repeated failures a source backs off, up to 30 minutes, and the
footer shows the time of the next try instead of a countdown.

Settings files written by 0.3.0–0.3.2 still have `calendarPollSeconds` and `workbookPollSeconds`.
Both are now ignored; you can delete them.

**Logs** are in `%LOCALAPPDATA%\rNotice\logs`, one file a day. Titles go in; snippets never do.

**The local store** is `%LOCALAPPDATA%\rNotice\rnotice.db`. Titles, senders, snippets and links
are encrypted with DPAPI under your Windows account.

**rNotice's windows are excluded from screen capture** (`WDA_EXCLUDEFROMCAPTURE`) by default,
so screenshots, recordings and shares show nothing where they are. To take a screenshot of
rNotice, untick **Tray → Hide from screen capture**; tick it again afterwards. It applies to every
open rNotice window at once and is remembered (`hideFromCapture` in `settings.json`). Windows'
own screenshot tools honour it; whether Teams does is not yet checked — don't rely on it in a
share until it is.

---

## Build 0.1 — the permission probe

Still installed as **Start Menu → rNotice Probe**.

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
cd rNotice
powershell -NoProfile -ExecutionPolicy Bypass -File .\install\Install.ps1 -Launch
```

`-ExecutionPolicy Bypass` is there because Windows' default policy on desktops (`Restricted`)
blocks every `.ps1`. It applies to that one process only and leaves the machine's policy alone.
Don't change the machine policy to get round it. If Group Policy sets one, the bypass won't
override it — check with `Get-ExecutionPolicy -List`.

Add `-SelfContained` to bundle the .NET runtime. `-Launch` starts rNotice as you, straight after.

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
- `& "$env:ProgramFiles\rNotice\rNotice.exe"`

Launching it again while it's running opens the queue window instead of starting a second copy.

The first run may show a Windows sign-in prompt. After that the broker signs you in silently and
no prompt appears again.

The probe (Start Menu → **rNotice Probe**) exits `0` if all checks passed, `1` if any failed,
`2` if cancelled.

### Pointing it somewhere else

No rebuild needed:

| Variable | Default |
| --- | --- |
| `RNOTICE_CLIENT_ID` | `2923549f-f444-48f7-8084-c8b7799db2e0` |
| `RNOTICE_TENANT` | `bravurasecurity.com` |
| `RNOTICE_WORKBOOK_FOLDER` | `efficiency` — probe only. The agent reads `workbookFolder` from `settings.json`. |

The client ID is not a secret. A public client registration has no secret and no certificate, and
the client ID ships inside the binary by design.

## Upgrade

**Re-run `Install.ps1`**, the same way as above. It stops anything running, replaces the files in place and updates the
version shown in Add or Remove Programs. There is no separate upgrade path and no need to
uninstall first.

Your settings, logs and sign-in state are in your own profile and are never touched by an
upgrade. Other users' data is likewise untouched.

The version comes from `<Version>` in `src/rNotice.Agent/rNotice.Agent.csproj` — bump it there
and the installer picks it up. Nothing else needs editing.

## Uninstall

**Settings → Apps → Installed apps → rNotice → Uninstall.** Or from an elevated prompt:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:ProgramFiles\rNotice\Uninstall.ps1"
```

Uninstalling from Settings needs no bypass; the entry the installer registers already passes it.

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

No Teams, no model classification, no full-screen takeover, no acknowledgement
write-back, no promotion, no MCP server, no Claude skill. Steps 4 onward in `SPEC.md`.

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
