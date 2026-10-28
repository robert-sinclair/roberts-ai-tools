# rNotice — spec

Status: **draft for review.** Nothing is built yet. Name settled: **rNotice**.

## What it is

A Windows desktop assistant that watches four places work arrives — your mailbox, your Outlook
calendar, named Teams chats, and a task spreadsheet in OneDrive/SharePoint — and runs everything
it finds through one flow: **intake → plan → complete**.

It decides how loudly to tell you. A meeting starting in one minute takes over the screen. A
reply landing on a thread you're waiting on is top of the queue but only a toast. A Teams
message asking you a question is high priority and gets a normal alert. Most things just join
the list.

Plus an MCP server and a Claude Skill, so from a chat you can work the same queue: what's on,
what's urgent, what's waiting on me, plan this, close that.

### Decisions already made

| Question | Answer |
| --- | --- |
| Sources | Mailbox, Outlook calendar, named Teams chats and @mentions, the workbook |
| Flow | Intake → Plan → Complete, in this app |
| System of record | **The app's local store.** The workbook is a source, not the master. |
| Stack | C# / .NET on Windows |
| Privilege | Runs with user rights only. No service, no elevation at runtime. |
| Install | **Machine-wide** to `%ProgramFiles%\rNotice`, registered in Add or Remove Programs. Needs admin once, to install. |
| Calendar writes | Opt-in per task. A plan reaches your calendar only when you plan it with "Put it on my Outlook calendar" ticked (the default) or add it later. Nothing you didn't plan, and never a meeting. |
| Workbook writes | Acknowledgement columns, and `CalendarEventId` on promotion. No row creation. |
| Drift | Row and event disagree → flag it, change nothing. **Exception, as built in step 8:** an rNotice event you move or delete in Outlook is followed, with a note on the task. |

### Explicitly out of scope for v1

- **Replying.** It reads your mail and Teams. It never sends, replies, forwards or posts.
- Creating or deleting workbook rows.
- Sending meeting invites. Events are yours alone, no attendees, ever.
- Shared, team or client calendars and mailboxes. Yours only.
- Teams channels. Named chats only — channel @mentions are out of v1 by decision.
- Resolving row/event drift automatically. The app reports it; you fix it.
- Machine control tools (deferred — see *Room to grow*).
- Multi-user. One signed-in user, one workstation.

---

## The model

### Two axes, not one

The earlier draft had a single `Severity`. Your own examples break it:

> *"A reply to an email should be top priority, even if it's immediately cleared."*
> *"A teams message that is a request for information should be high priority but not huge
> alert worthy."*

Top priority, small interruption. High priority, medium interruption. **How much something
matters and how loudly it should interrupt you are different questions**, and conflating them
is why notification systems get ignored. So every item carries both.

**Priority** — where it sits in the queue. `P1` … `P4`. Decides ordering and what the digest
leads with. Nothing else.

**Interrupt** — how it reaches you. Four levels:

| Level | What happens | Reserved for |
| --- | --- | --- |
| `takeover` | Full-screen, dims every monitor, large type, must be dismissed | Meeting starting in ~1 min. Almost nothing else. |
| `alert` | The topmost window — the one already specced | Something needing a decision soon |
| `toast` | Transient corner notification, tray badge, no focus change | "It arrived" — the reply you were waiting for |
| `silent` | Queue and digest only | Everything else |

Your three examples map cleanly: meeting in 1 min is `P1`/`takeover`; the email reply is
`P1`/`toast`; the Teams question is `P2`/`alert`.

**The takeover budget.** `takeover` stops being effective the moment it's common. Config caps it
(`maxTakeoversPerDay`, default 10) and the app logs every one so you can see whether the rules
are earning it. If you find yourself dismissing takeovers without reading them, the rules are
wrong and the log will show which.

### Stages

| Stage | Means |
| --- | --- |
| `Intake` | Seen, classified, not yet decided. The inbox of the app. |
| `Planned` | Real work with a time. Either you decided it, or it's a meeting and was already scheduled. Optionally promoted to the calendar or the workbook. |
| `Done` | Finished. |
| `Dismissed` | Not work. Won't come back. The source is remembered so it isn't re-ingested. |

The app sets the first stage and nothing after it. **Everything enters as Intake except
meetings, which enter as Planned, by decision (2026-10-22).** A meeting is already scheduled,
so there is nothing left to decide about it. In Intake, every meeting in the horizon (28 on the
first real run) crowded out the things that actually needed a decision.

What follows from that:

- **Intake holds only decisions:** workbook rows, and later mail and Teams messages.
- **Meetings still fire.** The scheduler fires Planned items as well as Intake ones, so the T−1
  alert (later the takeover) is unchanged.
- **The tray's "urgent" count is Intake only**, so meetings never turn the icon orange. A
  meeting gets its own alert at T−1; it doesn't need the badge as well.
- **Meetings can't go back to Intake.** The sync moves any calendar item it finds in Intake to
  Planned. Dismiss and Complete still stick.
- **Attendance (2026-10-23).** Graph can say whether you attended — Teams attendance reports
  ([docs](https://learn.microsoft.com/en-us/graph/api/meetingattendancereport-list), needs
  `OnlineMeetingArtifact.Read.All`, generated only after the meeting ends) or your presence
  ([docs](https://learn.microsoft.com/en-us/graph/api/presence-get), needs `Presence.Read`; says
  you're in *a* call, not which). Neither is granted. So rNotice records **Join clicked in
  rNotice** (`JoinedUtc`) and shows a started meeting as *in progress* until then. Join no
  longer marks a meeting Done — a Done meeting that was then moved never alerted at its new
  time — and a Done meeting whose start changes goes back to Planned.
  **`Presence.Read` requested from IT on 2026-10-23** (`IT-REQUEST-PRESENCE.md`) and **granted:
  confirmed live on 2026-10-26** — a silent token carried it and `GET /me/presence` returned 200.
  Building on it is build step 11.
- **No "rebook" action, by decision (2026-10-22).** Moving a meeting happens in Outlook, and
  **Open in Outlook** gets you there from the alert and the queue. Moving it back to Intake was
  considered and dropped: it conflicts with the rule above. A "needs rebooking" flag that kept
  it in Planned but also showed it in Intake was offered as the alternative, and left out for now.

### The item

One local record per thing, whatever it came from:

| Field | Notes |
| --- | --- |
| `Id` | Local GUID |
| `Source` | `workbook` \| `calendar` \| `mail` \| `teams` |
| `SourceId` | Row `Id`, event id, message id, chatMessage id |
| `SourceRef` | Deep link back — Excel row, Outlook event, Outlook message, Teams message link |
| `Title`, `Snippet` | Subject/title and a short excerpt. Never the full body. |
| `From`, `ReceivedUtc` | Who and when |
| `Stage` | As above |
| `Priority`, `Interrupt` | The two axes |
| `ClassifiedBy` | `rule` \| `model` \| `user` |
| `ClassifierReason` | **Why it got that priority, in one line.** Non-negotiable — see below. |
| `DueUtc` | If the source has one |
| `PlannedStartUtc`, `CalendarEventId` | Set on promotion |
| `AckedAtUtc` | When you cleared it |

`ClassifierReason` is required on every item, from rules as much as the model. A priority you
can't interrogate is a priority you'll stop trusting, and the first week of this app will be
spent arguing with its judgement. The reason is what makes that argument productive.

---

## Sources

Four pollers, one queue. Each is independently disableable, and the app is useful with any
subset running.

### Mailbox

`GET /me/mailFolders/inbox/messages/delta`, storing the `deltaLink` between polls. Delta is the
supported way to track changes without re-reading the folder; any query parameters must be sent
on the first request only, since Graph bakes them into the token.
([message: delta](https://learn.microsoft.com/en-us/graph/api/message-delta))

Scope: `Mail.ReadBasic` covers metadata; **`Mail.Read` is needed for bodies**, and bodies are
what classification needs. Taking `Mail.Read` is a real step up in what the app can see — see
*Security*.

**Inbox only, by decision.** One delta cursor, one subscription. Anything your Outlook rules
move out of the Inbox is — by your own earlier decision, encoded in those rules — not something
you need alerting on. Watching filed mail would mean re-litigating every rule you already wrote.

Not every inbox mail is an item. Also skipped: anything you sent, meeting messages (invites,
responses, cancellations — Graph types them `eventMessage*`), and anything matching
`ignoreSenders` (build automation, newsletters, monitoring). What survives goes to
classification.

**As built (0.4.0, 2026-10-22):**

- **First read, or after Graph drops the delta token:** only mail received in the last
  `mailBackfillDays` (3) is read, using delta's one supported filter,
  `receivedDateTime ge` ([message: delta](https://learn.microsoft.com/en-us/graph/api/message-delta)).
  The `deltaLink` is stored, encrypted, and saved only after the round is stored.
- **Backlog doesn't interrupt.** A message more than 15 minutes old when rNotice first sees it
  goes into the queue without a toast or alert. A toast means "this just arrived".
- **Leaving the inbox:** delta reports a deleted *or moved* message as `@removed`. If its item
  is still in Intake, the item goes; if you planned or finished it, it stays.
- **Read/unread changes** come back through delta. They update the item and never fire again.
- **Sent Items** is read each poll for the last `replyLookbackDays` (60), before the inbox, so
  a reply arriving just after you sent is matched.

First real run: 62 messages over 3 days; 9 meeting messages skipped, 53 into Intake, no toasts.

**Ignoring senders from the app (0.4.1).** `ignoreSenders` is editable without touching the
file: *Dismiss and ignore sender* on a mail item (address, or domain except your own), and an
*Ignored senders* editor from the tray. Ignoring dismisses that sender's Intake items. Removing
an entry clears the inbox `deltaLink` so the backfill window is re-read: mail that was skipped
while ignored comes back, quietly, and dismissed items stay dismissed. This makes
`settings.json` app-written at runtime, so a file that fails to load is never overwritten.

**Salesforce case links (0.5.1, 2026-10-23).** Mail whose subject or excerpt names a case
(`Case … 00038001`) gets the case's internal Lightning link, built from the Case record ID
(key prefix `500`) found in a Salesforce link in the message: `my.salesforce.com/500…` or
`…/lightning/r/Case/500…/view`. Salesforce's 15-character IDs are converted to 18 with its
checksum, so the link matches Lightning's. No Salesforce API, no sign-in. When the ID isn't in
the excerpt, rNotice reads that **one message's body once** (`Mail.Read`, already granted),
keeps the ID and drops the body — so "rNotice never stores a full body" still holds. Checked on
real mail: *Case Assignment* carries `<a href="…my.salesforce.com/500N300001Mapmw">00038001</a>`
past the excerpt; *Needs attention* has the portal link in the excerpt. A case email with no
Salesforce link gets nothing; resolving a bare case number needs the Salesforce API or
Claude's Salesforce connector (steps 9–10).

**Reporting spam, by decision (2026-10-22): rNotice hands it to Outlook.** Graph's
`reportMessage` is beta-only and needs `Mail.ReadWrite`
([reportMessage](https://learn.microsoft.com/en-us/graph/api/message-reportmessage?view=graph-rest-beta)),
which would break "rNotice never changes your mail" for one button. *Ignore and report spam*
ignores the sender or domain, dismisses, and opens the message in Outlook, where Report is one
click. No new scope.

**Read state is shown, not acted on, by decision (2026-10-22).** Intake mirrors the inbox with
the model off — 53 items after the first sync. Closing an item when its message is read in
Outlook was offered and turned down: reading isn't handling. Instead each mail item carries
`IsRead` from Graph (`isRead` on the message, and the bare read/unread flips delta reports),
shown as a bold title when unread and *(read in Outlook)* when read. Step 7's model is still
the route to a quieter Intake.

### Calendar

`GET /me/calendarView` over now − 1h to now + `horizonDays`. Exchange expands recurrences
server-side; the `events` endpoint hands back a series master and leaves the expansion to you.
Items carry a `type` of `singleInstance`, `occurrence`, `exception` or `seriesMaster`.

Three things that bite:

- `calendarView` **requires** `startDateTime` and `endDateTime`.
- Default page size is 10. Follow `@odata.nextLink` to exhaustion or you silently miss events —
  and a missed event is a missed suppression or a missed takeover.
- Send `Prefer: outlook.timezone="UTC"` on every call, consistently. Mailbox timezone mismatch
  is a documented source of results that don't line up.
  ([list events vs calendarView](https://learn.microsoft.com/en-us/answers/questions/1194282/which-api-should-used-for-list-events))

#### Which meetings become items

**Decided: all of them, minus a blocklist.** `meetingBlocklist` holds subject patterns —
standups, focus blocks, recurring 1:1s you don't need warning about. Everything else alerts by
default, so a meeting is never silently missed because you forgot to opt it in.

Every meeting that alerts also takes over the screen at T−1. See *Takeover window* for what that
looks like, the arming delay on dismissal, and why the daily cap became a report rather than a
silent stop.

A recurring work event you created yourself is just a meeting by this rule, which is exactly how
recurrence reaches the queue. Only events rNotice itself promoted are excluded, because their
item already owns the alert.

### Teams — named chats only

**As built (0.8.0, step 6, 2026-10-23).** Where this differs from the design below, this wins.

- **This spec was wrong about delta.** There is no per-chat `messages/delta` with delegated
  permissions. The only chat delta, `/users/{id}/chats/getAllMessages/delta`, is
  application-only ([chatMessage: delta](https://learn.microsoft.com/en-us/graph/api/chatmessage-delta)).
  So each watched chat is read with a **watermark**: `GET /me/chats/{id}/messages` with
  `$orderby=lastModifiedDateTime desc&$filter=lastModifiedDateTime gt {watermark}`
  ([list messages in a chat](https://learn.microsoft.com/en-us/graph/api/chat-list-messages)).
  The watermark is the newest `lastModifiedDateTime` seen, stored per chat after the messages
  are saved, so a failed poll re-reads rather than loses anything.
- **One listing per poll.** `GET /me/chats?$expand=members,lastMessagePreview` gives every chat
  with its last message time; only watched chats with something newer than their watermark are
  read. A quiet minute is one or two calls.
- **Which chats.** Tray → **Watched Teams chats…** picks group and meeting chats (ids saved in
  `rules.watchedChatIds`). **Every one-on-one chat is watched** without picking
  (`rules.watchOneOnOneChats`, default true): a 1:1 message is nearly always meant for you.
- **What becomes an item** — decided 2026-10-23, until the classifier (step 7) exists:
  - An @mention of you in a watched chat → `P2`/`alert`, one item per message.
  - A one-on-one message without a mention → the fallback `P3`/`silent`, but **one item per
    chat**, updated with the latest message. A back-and-forth is one thing to handle.
  - A group or meeting message without a mention → skipped. A busy group chat runs at about a
    dozen messages a day; that would bury Intake.
- **A planned 1:1 is frozen (0.8.4, 2026-10-26).** Planning a chat item means "this message";
  later messages in the chat must not rewrite it. When a newer message arrives, the planned item
  moves to a key of its own and the message starts a new Intake item. Edits and reactions to
  messages no newer than the planned one are ignored.
- **Titles are editable (0.8.4).** F2, double-click, or right-click → Edit title, on anything but
  a meeting. An edited title is sticky — later polls keep it — and blank restores the source's.
  An Outlook event rNotice made for the item is renamed to match.
- **Your reply closes a 1:1.** If your own message is the latest in a one-on-one chat, its
  Intake item moves to Done with the note "You replied in Teams". Their next message reopens it.
  A planned one stays planned.
- **First read** of a chat goes back `teamsBackfillHours` (24), quietly — nothing older than 15
  minutes alerts, the same rule as mail.
- Polls on `refreshSeconds` with the other sources, not the 120s below.

Checked on real chats on 2026-10-23: 100 chats listed (27 group, 25 one-on-one, 48 meeting),
17 with messages in the last day; the filter honoured; mentions of you found by user id; every
message carries a `webUrl` for Open in Teams. The day's messages gave 7 one-on-one items.

Original design:

You said *specific* chats, and that's the right scope for more than tidiness: it keeps us on
`GET /chats/{id}/messages/delta` per watched chat, instead of the tenant-wide export APIs.

- **Watched chats** are configured by id. Delta per chat, same `deltaLink` pattern.
- **@mentions** are found by scanning each returned message's `mentions` array for your own user
  id. The `mentions` property names the users, bots, channels, teams or tags @mentioned in a
  message. There is **no "get my mentions" endpoint** — this is the mechanism.
  ([chatMessageMention](https://learn.microsoft.com/en-us/graph/api/resources/chatmessagemention?view=graph-rest-1.0))
- Delta only reaches back about eight months. Irrelevant for us; worth knowing.

> **Channel @mentions are a different, much heavier problem.** Catching a mention in a Teams
> *channel* means `ChannelMessage.Read.All`, which is **admin-consent-only** and grants reading
> channel messages org-wide — a scope a security company will rightly push back on. It also
> means iterating every channel you're in.
> ([permission reference](https://graphpermissions.merill.net/permission/ChannelMessage.Read.All))
>
> **Cheaper route worth considering:** Teams already emails you missed-activity notifications,
> and we're reading your mailbox anyway. Channel mentions could arrive as mail items for free,
> with no new scope at all. Less precise, enormously less privileged. Open question.

**Cost is no longer a factor.** Microsoft ended metering on the Teams Graph APIs on 25 August
2025 — no billing configuration, no per-message charge, and the Model A / Model B selection that
older articles describe is gone.
([metered API list](https://learn.microsoft.com/en-us/graph/metered-api-list))
Older sources quoting per-message rates predate that change. Some Teams APIs remain *protected*
and need an access request; a Microsoft answer indicates delegated permissions don't require it,
but that post predates the current docs — **verify in the spike**.

### Workbook

An Excel **Table** named `Actions`, polled on an ETag pre-check, rows becoming items.

**Location (decided):** your OneDrive for Business, in the `efficiency` folder —
`/personal/robert_sinclair_bravurasecurity_com/Documents/efficiency` on
`bravurasecurity-my.sharepoint.com`. The `-my.sharepoint.com` host and the `/personal/<upn>/`
path make this OneDrive for Business, not a SharePoint site library, despite the hostname. So:

- Graph path is `/me/drive/root:/efficiency/<file>.xlsx:/workbook/tables/Actions`.
- Delegated **`Files.ReadWrite` covers it.** No `Files.ReadWrite.All`, no `Sites.ReadWrite.All`,
  no admin consent.
- It is business storage, which the Excel REST API requires — OneDrive Consumer workbooks are
  not supported.

**The file does not exist yet.** rNotice's repo carries the template and the app creates it on
first run if it's missing, so there is no manual setup step and no schema drift between the
spec and reality. The creation path is proven — see *Write path: proven by spike* under
*Writes*.

**What changed is its status.** It is no longer the system of record — it can't be, because an
email or a Teams message has no row to live in and the app doesn't create rows. The workbook is
one source among four, plus a write-back target for its own rows.

#### Columns

| Column | Type | Written by | Notes |
| --- | --- | --- | --- |
| `Id` | text (GUID) | you / Excel | **Stable key. Required.** Row index is not stable; this is. |
| `Title` | text | you | Alert headline |
| `Details` | text | you | Body text |
| `Client` | text | you | Client or environment. Optional, used for grouping. |
| `DueUtc` | **text**, ISO 8601 | you | e.g. `2026-10-20T13:00:00Z` |
| `LeadMinutes` | number | you | How long before `DueUtc` to alert. Blank = config default. |
| `Priority` | text | you | `P1`…`P4`. Blank = `P3`. |
| `Interrupt` | text | you | `takeover` \| `alert` \| `toast` \| `silent`. Blank = `alert`. |
| `Status` | text | **app** | `Pending` \| `Acked` \| `Done` \| `Cancelled` |
| `AckedAtUtc` | text, ISO 8601 | **app** | When you cleared it |
| `AckedBy` | text | **app** | UPN of the signed-in user |
| `CalendarEventId` | text | **app** | Set on promotion. Blank = not promoted, the common case. |

**Dates as text, deliberately.** Excel stores dates as serial numbers against a workbook epoch
and Graph returns floats whose meaning depends on the file's date system. ISO 8601 in UTC
removes a whole class of off-by-a-day bugs, and sorts correctly as text anyway.

Rows the app ignores: `Status` not `Pending`; `DueUtc` empty or unparseable (logged once per
row, not per poll); `DueUtc` beyond `horizonDays`.

**No recurrence column, by decision.** Repeating work — weekly checks, monthly reviews,
quarterly cert runs — lives as a **recurring Outlook event**, not a workbook row. `calendarView`
already expands occurrences server-side, so each instance arrives as a calendar item for free.

That is a genuinely good trade. No RRULE parsing, no next-occurrence state machine, no
"generate the next row on completion" logic — all of which are where recurring-task features go
wrong. It also means you edit a repeating commitment in Outlook, where you already know how.

One consequence worth stating: a recurring work event you created yourself carries no `rNotice`
category, so it **does** produce a calendar-sourced alert. That is the mechanism, not a bug.
Only events rNotice promoted are excluded, because their item already owns the alert.

### Items that have no row

A mail or Teams item lives only in the local store. If you decide it's real work worth tracking
with everything else, **promote it to the workbook** — the same explicit gesture as promoting to
the calendar, and the one case where the app creates a row.

That is the honest resolution to "no row creation". The rule holds for anything automatic:
nothing the app ingests writes itself into your shared file. Promotion is you, by hand, once.

> Open question 6 asks whether you want this at all. The alternative is that mail and Teams
> items live only in the app and never reach the sheet.

---

## Classification

This is the part that decides whether the app is useful or just another thing shouting at you.

### Rules first, and rules go a long way

Three of your four examples need no judgement at all:

| Example | Rule | Result |
| --- | --- | --- |
| Meeting starting in ~1 min | Calendar instance start − now ≤ `takeoverLeadSeconds` | `P1` / `takeover` |
| A reply to an email | Message `conversationId` matches a thread you have sent in, received after your first send there, sender ≠ you, not a meeting message | `P1` / `toast` |
| @mention in a watched Teams chat | Your user id appears in the message's `mentions` array | `P2` / `alert` |
| *A Teams message that is a request for information* | — needs judgement — | model |

Other deterministic rules: VIP sender list → `P1`/`alert`. Workbook row inside its lead time → its own `Priority` and
`Interrupt` columns. Message where you are the only recipient on the To line → one priority
step up.

#### Reply matching: checked by spike, 2026-10-21

`conversationId` matching is still not something Microsoft documents as a way to track replies.
So a read-only spike checked it against real mail, using RFC 5322 threading as the ground truth:
an inbox message is a reply on your thread if its `In-Reply-To` or `References` header names
the `Message-ID` of something you sent.

Data: 101 sent messages (76 conversations) over 60 days, 374 inbox messages from others over
30 days. Every inbox message had its internet headers. 24 were true replies.

| Rule | Replies caught | Missed | False alarms |
| --- | --- | --- | --- |
| `conversationId` in a conversation you've sent in (as first specced) | 24 | 0 | **26** |
| + received after your first send in that conversation | 24 | 0 | 17 |
| + skip meeting messages (Graph `@odata.type` `eventMessage*`) | **24** | **0** | **0** |

Where the 26 false alarms came from:

- **9: the message you replied to.** It shares the conversation and arrives before your send.
  The time check removes them.
- **17: meeting traffic.** "Accepted:" and "Canceled:" for invites you sent go into the same
  conversation, with no `References` header. Skipping `eventMessage*` removes them. There were
  50 meeting messages in the inbox and none was a true reply, so skipping them entirely costs
  nothing. The calendar source already covers the meeting itself.

The 24 true replies included external senders (22) and messages where you were only on CC (4).
All were caught.

**What the spike did not cover.** No true reply in the sample had an edited subject or came
from a thread forwarded on from outside, so those two cases are untested, not proven. Exchange
groups conversations partly by subject, so an edited subject is the most likely way to miss a
reply. If it matters, the fallback is to match `References` against your sent `Message-ID`s.
That is the ground truth used here, but it needs `internetMessageHeaders`, which `messages/delta`
may not return.

**What this means for the poller:** keep `conversationId → first sent time` from Sent Items, not
a bare set of ids, and carry `@odata.type` through to `MailMessage.IsMeetingMessage`.

The time check matters for more than backfill. Marking a message read changes it, and changed
messages come back through delta. Without the check, a mail you read after replying would be
reclassified as a P1 reply.

**No rule for one-on-one Teams messages, by decision.** An earlier draft made a 1:1 message
without a mention `P3`/`silent` by rule. That kept most requests for information away from the
model, since that's how most of them arrive. Without a mention, 1:1 and group messages both go
to the model.

### Then the model, for what's left

"Is this a request for information or is it chatter" is a judgement call. No rule gets it right,
and getting it wrong in either direction is costly — miss a real question, or cry wolf.

So: items no rule classified go to Claude, in batches, on a timer.

- **Input is the snippet, not the body.** Subject, sender, first ~500 characters. Enough to
  judge, far less to expose.
- **Output is structured** — `output_config.format` with a schema of `{priority, interrupt,
  reason}`, so there's no response parsing to get wrong.
- **Cached by source id.** An item is classified once. Re-polling never re-asks.
- **Batched.** Several items per request, with the rules and your preferences as a cached
  prefix.
- **Model: `claude-opus-5-5`.** Decided. $4/$20 per MTok, and at snippet volumes that is a few
  dollars a month at worst. The whole value of the classifier is judgement on genuinely
  ambiguous phrasing — "can you take a look at this when you get a chance" is or isn't a request
  depending on who sent it and what came before. Paying for the better judgement is the point.
  The model is a config setting, so it can be changed after seeing real behaviour.

### The classifier is optional and the app degrades honestly

**Nothing core depends on an LLM call.** With classification off, or the API unreachable,
rule-matched items keep their priority and everything else lands at `P3`/`silent` in the queue.
You lose the "is this a question" distinction. You do not lose the meeting takeover, the reply
toast, the mention alert, or anything from the workbook.

This is a deliberate line. An assistant that goes blind when an API key expires is worse than
one that was never that clever.

### You can overrule it, and it should learn

Changing an item's priority in the app sets `ClassifiedBy: user`. Those corrections accumulate
into a local preferences file that becomes part of the classifier's cached prefix — "Robert
treats anything from this sender as P1", "Robert dismisses release-pipeline mail". No training,
no fine-tuning, just examples in the prompt.

Corrections are local and reviewable as plain text. Nothing is learned that you can't read and
delete.

---

## Architecture

```
┌────────────────────────────────────────────────────┐
│ rNotice.Agent              (your account, no admin) │
│  ├─ MSAL + WAM → Graph token                       │
│  ├─ pollers: mail · calendar · teams · workbook    │
│  ├─ classifier: rules → (optional) Claude          │
│  ├─ item store (SQLite, local)                     │
│  ├─ scheduler → what fires, when, how loudly       │
│  ├─ promoter → promoted items ⇒ events / rows      │
│  ├─ drift detector → reports, never resolves       │
│  ├─ UI: tray · queue window · alert · takeover     │
│  └─ named pipe server  \\.\pipe\rNotice.Control     │
└────────────────────────┬───────────────────────────┘
                         │ JSON over named pipe
┌────────────────────────┴───────────────────────────┐
│ rNotice.Mcp                    (stdio MCP server)   │
│  launched by Claude Desktop / Claude Code          │
└────────────────────────────────────────────────────┘
```

| Project | Does |
| --- | --- |
| `rNotice.Core` | Models, config, item store, Graph clients, classification, scheduling, promotion, drift, pipe protocol |
| `rNotice.Agent` | WPF app. Tray, pollers, queue window, alert and takeover windows, pipe server. Autostarts at logon. |
| `rNotice.Mcp` | stdio MCP server. Owns no state and no token; proxies to the agent. |
| `rNotice.Tests` | Rules, scheduling, parsing, promotion, drift. No network, no model calls. |

**Item store is SQLite** under `%LOCALAPPDATA%\rNotice`. Four sources and a stage machine is past
what a JSON file should hold, and it has to survive restarts mid-flow.

### Why no Windows service — a real departure from rMic

rMic puts the work in a LocalSystem service because disabling a device needs admin. rNotice needs
the opposite, and two constraints force a plain user-session process:

1. **Graph's Excel API does not support application permissions.** Microsoft is explicit:
   *"Application permissions are not supported for this API. Your app must sign in as a user."*
   ([tablerow list](https://learn.microsoft.com/graph/api/tablerow-list)) Everything rides your
   delegated token.
2. **Services run in session 0 and cannot draw on your desktop.** The whole point is a window in
   front of everything.

One process, your account, your token, your desktop.

**Install is machine-wide, by decision.** Binaries go to `%ProgramFiles%\rNotice` with an Add or
Remove Programs entry, via `install/Install.ps1` from an elevated prompt. That is the only step
that needs admin. Everything per-user — sign-in, settings, the item store, logs — lives under
each user's own profile, as the README describes.

**Autostart is per user**, not machine-wide: the agent writes its own `HKCU\...\Run` value on
first run, the same approach as `rMic/src/rMic.Tray/AutoStart.cs`. An `HKLM` Run key would start
rNotice for every user on the machine, and the app registration only admits one — everyone else
would get a sign-in failure at every logon. The uninstaller removes the current user's value;
other users' values point at a missing exe and do nothing.

### Why the MCP server doesn't call Graph itself

One token holder means one MSAL cache, one place that can reach your tenant, one audit story.
The MCP server is a thin pipe client — Claude Desktop can spawn and kill it freely and nothing
is lost. It also means `show_alert` works, because the windows belong to the agent.

If the agent isn't running, every MCP tool returns a clear "agent not running" error rather than
silently doing nothing.

### Pipe ACL — narrower than rMic's

rMic grants `INTERACTIVE` on purpose: anyone at the keyboard should be able to kill the mic.
rNotice's pipe carries your mail, your calendar and your Teams messages. **Current user SID plus
LocalSystem only.** No `INTERACTIVE`.

---

## Auth

MSAL.NET public client with the Windows broker (WAM):

```csharp
var pca = PublicClientApplicationBuilder.Create(clientId)
    .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
    .Build();
```

WAM gives Windows Hello, Conditional Access and silent sign-in with the account you're already
signed into Windows as. Needs `Microsoft.Identity.Client.Broker` and Windows 10+.

**Redirect URI: `ms-appx-web://Microsoft.AAD.BrokerPlugin/{client_id}`.** It goes in the app
registration, not in MSAL code — a detail easy to miss, and the registration is wrong without
it. Going through the broker also means the app inherits Conditional Access and device
compliance rather than sidestepping them, which is the strongest thing in its favour when IT
reviews the request.
([WAM docs](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-acquire-token-wam))

### Scopes, all delegated

| Scope | For | Consent |
| --- | --- | --- |
| `Files.ReadWrite` | Workbook table rows (OneDrive for Business) | User |
| `Calendars.ReadWrite` | Calendar read + promoted events | User |
| `Mail.Read` | Mail bodies for classification | User |
| `Chat.Read` | Watched Teams chat messages | Consented (probe, 2026-10-21) |
| `MailboxSettings.Read` | Your working hours, for free-slot search | Verify |
| ~~`ChannelMessage.Read.All`~~ | ~~Channel mentions~~ — **dropped from v1** | ~~Admin, org-wide~~ |

No `.All` mail or calendar scope, no application permissions, no `Mail.Send`, no
`ChatMessage.Send`. The app can reach your own mailbox, calendar and chats, as you, read-only
except for the three writes listed above.

**Assume admin consent is required.** Earlier drafts of this spec claimed every scope was
user-consentable and the request would therefore be easy. That was too confident.
`Calendars.ReadWrite` delegated is confirmed as not requiring admin consent in Microsoft's
reference, and `Files.ReadWrite` is reported the same, but `Chat.Read` and
`MailboxSettings.Read` are unverified — and more to the point, **a tenant's user-consent
settings override the per-permission flag**. If Bravura restricts user consent, as a security
company plausibly does, everything here needs an administrator regardless of how Microsoft
classifies it. There is also a signalled change (MC1163922) that would move many Exchange and
Teams permissions to admin-approval by default.
([permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference))

The standalone request document for IT is at `rNotice/IT-REQUEST.md`.

> **The app registration is the long pole.** This is an app that reads your mail and your Teams
> messages at a cybersecurity company — a different conversation with whoever owns the tenant
> than "it reads a spreadsheet". `ChannelMessage.Read.All` is dropped, so nothing here is
> org-wide and nothing acts without a signed-in user, but assume it still needs an administrator.
>
> `rNotice/IT-REQUEST.md` is the standalone ask, written for someone with no prior knowledge of
> the project. It stages the permissions so a partial approval is still useful: the workbook,
> calendar and working-hours scopes alone give a working tool.

Sign-in is interactive on first run, silent from the MSAL cache after. If silent refresh fails
the tray goes to a warning state; it does not pop a browser at you unprompted.

---

## Polling

### Why not webhooks

Graph change notifications need a publicly reachable HTTPS endpoint that answers a validation
handshake within ~10 seconds and every notification within 3.
([change notifications](https://learn.microsoft.com/graph/api/resources/webhooks)) A laptop
behind corporate NAT is not that. So: delta queries on a timer.

### Cadence

| Source | Interval | Mechanism |
| --- | --- | --- |
| Calendar | 60s | `calendarView`; the takeover needs minute-level accuracy |
| Mail | 60s | Sent Items for the reply rule, then inbox `messages/delta` |
| Teams | 60s (`refreshSeconds`) | chat list, then each watched chat's messages since its watermark — see *Teams* |
| Workbook | 60s | `cTag` pre-check, then table rows only if it changed |

**Calendar and workbook share one interval, `refreshSeconds` (default 60, floor 30), by decision
(2026-10-22).** The workbook moved from 300s so that one "next refresh" in the queue footer means
both sources. The cost is small: an unchanged workbook is one metadata GET a minute, and rows are
read only when the `cTag` changes. Mail and Teams joined the same interval when they were built.

**Firing never depends on a poll.** Once an item is in the store the scheduler owns its timing.
A network outage delays seeing *new* work; it does not delay an alert already known about. The
meeting takeover in particular is computed from the local store, so it fires on time even with
Graph unreachable. This is the single most important property of the design.

One Microsoft Q&A answer states flatly that polling Graph "is not allowed and will result in
throttling". That is about aggressive polling; delta on a 60-second floor with backoff is the
documented pattern. Still — treat it as a warning, not noise, and never tighten these intervals
without measuring.

### Throttling

Outlook endpoints throttle at a documented 10,000 requests per 10 minutes per app per mailbox,
4 concurrent per mailbox, `429` with `Retry-After`.
([throttling limits](https://learn.microsoft.com/en-us/graph/throttling-limits)) The cadence
above is roughly 300 requests/hour with every source running — still well over two orders of
magnitude clear. Rules regardless: honour
`Retry-After` exactly, exponential backoff capped at 30 minutes, never a tight retry loop.

These figures come from Microsoft posts of varying age and Microsoft says they change. Order of
magnitude, not a budget to spend.

### Sessionless workbook calls

Persistent workbook sessions expire after ~5 minutes idle and return 404, which at a 5-minute
poll is pure overhead. Without the `workbook-session-id` header, changes save straight to the
file — which is what we want.
([session docs](https://learn.microsoft.com/en-us/graph/excel-manage-sessions))

---

## Writes

Three, all narrow, all on items you acted on.

### Acknowledging a workbook row

1. Re-read the row **by `Id`**, not by cached index — someone may have sorted or inserted.
2. `Id` gone (row deleted) → drop from local state, log, one-line toast. Write nothing.
3. `Status` no longer `Pending` (someone else got there) → accept theirs, update local, don't
   overwrite.
4. Otherwise `PATCH .../tables/Actions/rows/itemAt(index={n})` with a one-row, twelve-column
   values array: the three acknowledgement values, `null` in every other cell. `{n}` is the
   index from the re-read in step 1, never a cached one.

No optimistic concurrency exists on table rows, so 1–3 narrows the race rather than closing it.
Acceptable: the window is milliseconds and the only contended field is one you're setting
anyway.

#### Write path: proven by spike, 2026-10-21

A throwaway console app ran against a scratch workbook in `/efficiency` in this tenant, using
the probe's WAM sign-in and `Files.ReadWrite` only. The file was deleted afterwards.

| Call | Result |
| --- | --- |
| `PATCH .../rows/{index}` (documented) | **400 `ApiNotFound`**, with a full row and with a partial one. Do not use. |
| `PATCH .../rows/itemAt(index={n})` | **200.** Full row works. |
| Same, `null` in every cell except the ack columns | **200, and the `null` cells are left unchanged.** This is the route rNotice uses. |
| `PATCH .../rows/itemAt(index={n})/range`, same `null` pattern | 200, same behaviour. Fallback if `itemAt` ever stops working. |
| `{"values": "X"}` — a bare string, not a 2-D array | **200, and every cell in the row becomes `X`, `Id` included.** The report was right. |

This matches the open reports
([Q&A](https://learn.microsoft.com/en-us/answers/questions/1333728/graph-workbook-api-update-row)).
Two rules follow for the writer:

- **Always send a 2-D array, one row by twelve columns.** Build it from a typed object; never
  pass a scalar. A malformed write doesn't fail — it wipes the row's `Id`, and with it the only
  stable key.
- **`null` means "leave this cell alone".** Never send `""` for a cell rNotice doesn't own.

What else the spike showed:

- **Creating the workbook works.** Upload a blank `.xlsx` (generated with OpenXML), write the
  header row to `A1:L1`, set `numberFormat` `@` on the `DueUtc` and `AckedAtUtc` columns, then
  `POST .../workbook/tables/add` (`address: Sheet1!A1:L1`, `hasHeaders: true`) and rename the
  table from `Table1` to `Actions`. No session, no retries needed.
- **Text format holds.** `DueUtc` came back as `String` with format `@` after rows were added.
- **An ISO string written through Graph is not converted to a date, even in an unformatted
  column.** So the "Excel converted your date" guard in `Intake` is for dates typed by hand in
  Excel, not for anything rNotice writes.
- **Blank cells come back as `""`, not `null`. Numbers come back as JSON numbers.** `Intake`
  already treats `""` as blank and reads numbers through `IFormattable`.
- **Rows carry an `index` and nothing else stable.** Finding a row by `Id` means listing the
  rows and scanning — fine at this table's size.

### Promoting to the calendar

**As built (0.7.0, step 8).** Where this differs from the design below, this wins.

- **Where you choose it.** The Plan dialog has **Put it on my Outlook calendar**, ticked by
  default (`promoteByDefault`). Dragging a task onto the day view uses the same default.
  Right-click a planned task for **Add to Outlook calendar** or **Remove from Outlook
  calendar…**; removing confirms, names the event, and leaves the task planned.
- **The event.** Subject is the task title. Body is the snippet plus a footer with the item id.
  Tentative, category `rNotice`, no attendees, reminder on.
- **It follows the plan.** Re-plan and the event moves. Mark done, dismiss, move back to
  Intake, or plan with no time, and the event is deleted. A task sent back to Intake by a
  meeting clash loses its event the same way.
- **It follows Outlook.** Move the event in Outlook and the plan moves to match, with a note:
  "Moved in Outlook to Fri 14:00". Delete it in Outlook and the task stays planned, stops being
  promoted, and says so. **This replaces "flag it, change nothing" for these two cases**: the
  event is yours and you moved it on purpose, so a flag would only ask you to repeat yourself.
  A deletion only counts once the calendar has actually been read, and only inside the window
  read (now − 1 h to the horizon), so an outage or a far-off plan can't look like a deletion.
- **Stagger.** rNotice alerts `planAlertLeadMinutes` (5) before the start; Outlook's reminder
  fires at the start. Reversed from the design below, which gave Outlook the shorter lead: a
  task's start is the moment that matters, and rNotice is the one that's running.
- **Deletes stay safe.** Before deleting, rNotice re-reads the event and refuses unless it still
  carries the `rNotice` category. A 404 counts as already gone.
- **rNotice's own events aren't meetings.** They're kept out of meeting sync, busy time and
  clash detection; otherwise every promoted task would clash with itself.
- **Not built:** the MCP tool, promote from the alert window or tray, workbook promotion (open
  question 6), the orphan sweep, and the "scheduled after it was due" flag.

Checked live on 2026-10-23: created a test event for 15 Dec, read it back through
`calendarView` with its category, `showAs` tentative, reminder 0, no attendees; moved it;
deleted it; a second delete got 404 and returned "already gone".

Original design:

Nothing reaches your calendar automatically. Promotion is available from the alert window, the
queue window, the tray, and the `promote_to_calendar` MCP tool.

| Field | Value |
| --- | --- |
| `subject` | `Title`, prefixed with `Client` when set |
| `body` | `Details` or snippet, plus a footer line with the item `Id` |
| `start` / `end` | Chosen start to start + `durationMinutes` (default 30) |
| `categories` | `["rNotice"]` — the tag that makes orphan sweeps possible |
| `isReminderOn` | `true` |
| `reminderMinutesBeforeStart` | Staggered — see below |
| `showAs` | `tentative` |
| `attendees` | none, ever |

Promotion takes an optional start time; default is `DueUtc` − `LeadMinutes`, and **Find me a
slot** can supply one instead. That is what makes free-slot search do real work rather than
offer advice.
([event resource](https://learn.microsoft.com/en-us/graph/api/resources/event))

#### Stagger the two reminders

A promoted item gets an rNotice alert *and* an Outlook reminder for the same thing. Fired
together, that's two popups for one task, which is how you learn to dismiss both.

So they fire at different times. rNotice alerts first, at the item's full `LeadMinutes`.
Outlook's reminder gets a **shorter** lead — `reminderLeadMinutes`, config, default 5 — so it
only lands if you ignored the first. One nudge while the app is running, a genuine second chance
when it isn't, never both at once.

**Demoting** deletes the event and clears `CalendarEventId`, **behind a confirmation dialog that
names the event**. Deliberately not an MCP tool — Claude can promote, never demote.

### Promoting to the workbook

For mail and Teams items only, and only by hand. Creates a row with `Id`, `Title`, `Details`,
`Client` and `DueUtc` from the item. The one case where the app writes a new row.

### The delete rule

Two places rNotice deletes a calendar event:

1. **You demote an item** — confirmation dialog first.
2. **You complete an item that has one** — no dialog; you just explicitly completed it, and a
   second prompt on top of that is friction without safety.

The rule that makes both safe: **rNotice only ever deletes events rNotice created.** Every one
carries the `rNotice` category and a `CalendarEventId` the app wrote itself. It cannot touch a
meeting, an invite, or anything you made by hand — there is no code path that reaches them.

> Minor asymmetry worth noticing: demote confirms, completion doesn't, and completion is the one
> where the app acts on its own. The argument for it is that you just pressed Complete, so the
> intent is fresh and unambiguous. Say the word if you'd rather both confirm, or rather
> completion only deletes future blocks and leaves past ones as a record.

### Linking items to events

Two mechanisms, deliberately redundant.

**`CalendarEventId`** — the precise link, written on promotion. A non-empty value also answers
"is this promoted".

**The `rNotice` category** — the sweep. Answers "which events on this calendar are mine" without
consulting any source, which is how orphans get found.

**What I rejected:** `singleValueExtendedProperties`. Textbook answer, open reliability reports
— empty arrays for events created after June 2024
([Q&A](https://learn.microsoft.com/en-us/answers/questions/1809278/issue-with-retrieving-singlevalueextendedpropertie)),
and missing from `calendarView` results
([Q&A](https://learn.microsoft.com/en-us/answers/questions/462964/how-to-expand-microsoft-graph-extended-properties)).
A link that works most of the time is worse than none, because the failure mode is duplicate
events on your calendar. A column and a category are boring and they work.

### Drift

Flag it, change nothing. Only promoted items can drift, so volume is small by construction.

- Event start no longer matches the promoted time — you dragged the block in Outlook.
- Event gone, `CalendarEventId` still set, item still open — deleted in Outlook rather than
  demoted.
- A categorised event matching no live item — orphan.
- Item `Done` but its event still open — shouldn't occur, since completion deletes it, but
  worth detecting if a delete failed.
- Promoted start now after `DueUtc` — scheduled for after it was due. The one drift that's an
  actual planning error; flagged loudly.

Drift surfaces in the tray badge, a `list_conflicts` MCP tool, and the daily digest. **Never a
topmost window.** Drift isn't urgent, and treating it as urgent is how you train yourself to
dismiss windows without reading them.

---

## The UI

### Times are shown local and UTC

Everything is stored as UTC. Everything is **displayed as both** — your local time first, UTC
beside it. Change windows, logs and client comms are all in UTC in this line of work, and a
single displayed timezone means doing the conversion in your head at exactly the moment you
can least afford to get it wrong.

### Queue window

**As built (0.6.0, 2026-10-23), to Robert's layout:** left, Intake on the top third (Done as a
second tab) and the selected item on the bottom two-thirds; right, the full height, **Planned as
a calendar day** — meetings and planned tasks as blocks, side by side where they overlap, work
hours unshaded, a now line. Drag onto a time to plan there; drag onto the Done tab to finish.
**A planned task whose slot a meeting now occupies goes back to Intake with a note** saying which
meeting and when (`PlanConflicts`, checked after every calendar refresh). The paragraph below is
the original three-column design, kept for the record.

The main surface, and where intake → plan → complete actually happens. Three columns by stage,
sorted by priority then time. Keyboard-driven: plan, dismiss, promote, snooze without reaching
for the mouse. Opens from the tray or a global hotkey.

This is the piece that makes it an assistant rather than a notifier. The alerts are the
exception path; the queue is where you work.

### Alert window (`alert`)

WPF, `Topmost="True"`, no taskbar entry, centred on the monitor with the mouse. Shows title,
snippet, source, who it's from, due time, time remaining, `ClassifierReason`, and buttons:
**Plan**, **Dismiss**, **Snooze**, **Open source** (deep link to Outlook / Teams / Excel),
**Find me a slot**, **Promote to calendar**.

### Takeover window (`takeover`)

**Concretely, what happens:** every monitor goes dark except one panel in the middle of your
primary screen. That panel carries the meeting name, who it's with, a live countdown, a **Join**
button and a **Dismiss** button. Everything you were looking at is still there, still running,
just covered. You see nothing else until you clear it.

Think of a full-screen alarm clock rather than a notification. The nearest thing you already
know is how Teams shows an incoming call — but covering the whole screen, on every monitor, and
it does not go away on its own.

**What it is not:** it does not block input. Windows won't reliably let a background process
steal keyboard focus, so if you were mid-sentence in an editor your keystrokes may still land
there. It blocks what you can *see*, not what you can type. That is the strongest guarantee
available to software, and it is a real one — nobody misses a dark screen.

#### Which meetings

**Decided: every meeting**, minus `meetingBlocklist` subject patterns. No attendee test, no
internal/external split.

> I'd argued for restricting it to meetings with other attendees, on the grounds that five or
> six screen-dims a day trains you to clear them reflexively. You've overruled that, which is
> your call to make — it is your attention and your calendar. Two things follow that are worth
> doing regardless, and they're in the design below: the daily count becomes a report rather
> than a silent cap, and dismissal gets an arming delay so a click already in flight can't clear
> a takeover you never saw.
>
> If after a fortnight you're clearing them without reading, the takeover log will show it, and
> the attendee rule is a one-line config change away.

#### Dismissal

**Decided: clicking anywhere clears it**, as does any button.

One addition, because this interacts badly with "every meeting": the window **ignores input for
`armingDelayMs` (default 750) after it appears**. A click already travelling toward whatever you
were doing cannot dismiss a takeover you never actually saw. Standard practice for consent
dialogs and installers, for exactly this reason. Long enough to stop an in-flight click, short
enough that you won't notice it.

#### The daily count is a report, not a cap

The earlier draft had `maxTakeoversPerDay` hard-stop at 10. **With every meeting taking over,
that's now a bug** — a twelve-meeting day would silently stop dimming the screen somewhere
around lunch, and silently dropping a meeting takeover is precisely the failure this feature
exists to prevent.

So the cap is gone. Every takeover is logged, and the daily digest reports the count. If it
reads "11 takeovers today" for a week running, that's the signal to reconsider the rule — but
the app never makes that decision for you by going quiet.

#### Join

Reads **`event.onlineMeeting.joinUrl`** and launches it. One click from the dim screen into the
call, which is most of the practical value of catching a meeting at T−1.

> **Do not use `onlineMeetingUrl`.** It is the Skype-era property and Microsoft documents it as
> **null for Teams meeting events** — a trap that looks right and silently never fires. The join
> link lives on the `onlineMeeting` property.
> ([choose an online meeting API](https://learn.microsoft.com/en-us/graph/choose-online-meeting-api),
> [known issue](https://learn.microsoft.com/answers/questions/633866/onlinemeetingurl-is-null-and-isonlinemeeting-is-fa.html?orderby=newest))

No `joinUrl` on the event means no Join button — the panel shows **Open in Outlook** instead.

#### While you're screen sharing — a better mechanism than the one asked for

You asked for the takeover to fire on non-shared monitors only. The intent is right: your
audience should not see your next meeting's name and attendees, which may name a client.

The implementation is better served by **`SetWindowDisplayAffinity(hwnd,
WDA_EXCLUDEFROMCAPTURE)`**. On Windows 10 2004 and later the window is *"shown on a monitor
only"* — it renders for you and **does not appear in the capture stream at all**.
([SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity))

That is strictly better than picking monitors:

- **It needs no detection.** There is no reliable way to know which monitor or window a
  conferencing app is capturing. Guessing it means sometimes guessing wrong, in the one
  situation where wrong is embarrassing.
- **You get the full takeover.** Every screen dims, including the one you're sharing — you see
  it, the call doesn't. The partial takeover you'd get from monitor-picking is the weaker
  signal, in the moment you most need the stronger one.
- **It's always on**, not just while sharing. Nothing rNotice renders can ever land in a
  recording or a capture, including ones you didn't know were running.

Three honest limits:

- **Not a security measure.** Microsoft says so explicitly — it does not stop someone
  photographing the screen, and it needs DWM compositing.
- **Pre-2004 Windows falls back to `WDA_MONITOR`**, which shows a black rectangle in the capture
  instead of omitting the window. Still no content leak; just visible that something is there.
- One third-party report (IOActive, 2026) that certain DXGI direct-GPU capture paths can still
  capture such a window on some GPU and driver combinations. Single source — **check it in the
  spike against Teams specifically**, since Teams is what you actually present in.

If the spike shows Teams capturing it anyway, monitor-picking is the fallback and we're no worse
off than the original plan.

**Partial evidence, 2026-10-22:** the alert and queue can't be screenshotted with Windows' own
tools — Robert found he couldn't capture the meeting alert to show it. So exclusion works for
local capture. Teams is still unchecked. Because that also blocks legitimate screenshots, there
is now a switch: **Tray → Hide from screen capture** (`hideFromCapture`, default on), applied to
every open rNotice window at once.

### Toast (`toast`)

Corner notification, auto-dismissing, tray badge increments. No focus change, no sound by
default. This is the email-reply path: top of your queue, zero interruption.

### Be honest about "in front of everything"

`Topmost` puts a window above normal windows. It does **not** guarantee focus. Windows
deliberately stops a background process stealing the foreground, and flashes the taskbar button
instead. Microsoft states plainly that meeting every documented condition *"does not guarantee
activation."*
([SetForegroundWindow](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setforegroundwindow))

- Default: show topmost **without stealing focus**, plus `FlashWindowEx` and an optional sound.
  Your typing goes where you were typing.
- `stealFocus: true` in config, off by default, attempts `SetForegroundWindow` and accepts that
  it sometimes won't work. No `AttachThreadInput` hacks — fragile, and they break.
- `takeover` dims every monitor, which is much harder to miss than focus would be. It still
  cannot force focus.

### Suppression

- **Busy** comes from the calendar: a `busy` or `oof` instance covering now. `alert` and below
  queue until the meeting ends or `DueUtc`, whichever is first.
- `takeover` ignores busy entirely — it is mostly *about* meetings.
- `free` and `tentative` suppress nothing. Promoted events are `tentative`, so your own blocks
  never silence your own alerts.
- Fullscreen detection stays as a fallback when the calendar is unreachable.
- Quiet hours in config. `takeover` ignores them.
- Several items firing at once collapse into one window with a list, not a stack.

### Free-slot suggestions

Next gap of at least `durationMinutes`, inside working hours, before `DueUtc`. Computed from the
calendar window already in memory — no `getSchedule` call. It **suggests**; acting on it means
promoting, which stays explicit.

~~**Working hours come from your mailbox settings.**~~ **Changed by decision (2026-10-22): work
hours are an rNotice setting**, `workHours`, default 08:00–16:00 Mon–Fri in *Canada Central
Standard Time* (fixed UTC−6, no daylight saving). They're the hours you want to plan work into,
which isn't necessarily what Outlook shows colleagues. `MailboxSettings.Read` stays consented
but unused for now.

**As built (0.4.4), ahead of step 8.** The Plan dialog suggests slots now, without promotion:

- **Duration** chosen in the dialog: 15, 30, 45, 60, 90 or 120 minutes; default `planMinutes` (30).
- **Starts on the quarter hour** (:00 :15 :30 :45), from now rounded up.
- **Clear of anything not marked free** on the calendar — busy, out of office, tentative,
  working elsewhere, all-day ones included — **and of other planned items** (start plus their
  planned duration, now stored per item).
- **Ends by the close of the working day.** A 30-minute slot is never offered at 15:45.
- **One suggestion per free gap**, five at most, up to two weeks out.

Found the hard way: the first Plan dialog's fixed "tomorrow 09:00" put a task on top of a
meeting. Checked against the live calendar on Thu 22 Oct at 15:53: Fri 08:00, Fri 10:30
(after 09:00/09:30/10:00 meetings), Mon 08:00, Mon 10:00, Tue 08:00.

> **This adds `MailboxSettings.Read` to the app registration.** It is the only scope added since
> the request was drafted — fold it in before the request goes to IT, or free-slot search falls
> back to configured hours. Confirm whether it needs admin consent; the delegated form is
> normally user-consentable but I have not verified it against Microsoft's own permission table.
> ([mailboxSettings](https://learn.microsoft.com/en-us/graph/api/user-get-mailboxsettings))

---

## MCP server and Skill

### Tools

| Tool | Writes? | Does |
| --- | --- | --- |
| `list_items` | no | Filter by stage, priority, source, client, due window |
| `get_item` | no | Full detail for one item, including `ClassifierReason` |
| `whats_my_day` | no | Calendar, due items and the intake queue on one timeline |
| `whats_urgent` | no | `P1`/`P2` in `Intake` or `Planned`, most pressing first |
| `agent_status` | no | Per-source last poll, auth state, classifier state, queue depths, last error |
| `list_conflicts` | no | Drift the detector has found |
| `find_free_slot` | no | Next gap long enough for an item. Suggests only. |
| `refresh` | no | Force a poll. Returns what changed. |
| `show_alert` | no | Force an alert up for an item, or ad-hoc text |
| `snooze_item` | no | Local snooze. Touches no source. |
| `set_stage` | **local** | Move an item between stages. Local store only. |
| `set_priority` | **local** | Overrule the classifier. Sets `ClassifiedBy: user`, feeds preferences. |
| `acknowledge_action` | **yes** | Set the three acknowledgement columns on one workbook row |
| `promote_to_calendar` | **yes** | Put one item on your calendar, optionally at a given time |
| `promote_to_workbook` | **yes** | Create a row from a mail or Teams item |

Three tools reach outside the machine, all on one item, all requiring an item that exists.

There is no `send_mail`, no `reply`, no `post_to_teams`, no `delete_*`, no `demote`, no
`reschedule`. Claude can read the queue, reason about it, move things through the flow, put
something on your calendar and close things out. **It cannot communicate on your behalf and it
cannot delete anything**, in your workbook, your calendar or your mailbox.

### Skill

`rNotice/skill/SKILL.md`, installable into Claude Desktop or Claude Code. It teaches Claude:

- To call `whats_my_day` or `whats_urgent` when you open a chat and ask anything about your day,
  your load, or what's outstanding — one timeline, not four lists you merge in your head.
- To lead with what's overdue or due soonest, never a full dump.
- To quote `ClassifierReason` when you ask why something is `P1`.
- To never promote, acknowledge or change a stage without you saying so in that turn.
- The source semantics, so "what's left for Contoso this week" works without re-explaining.

> **The skill pulls your mail and Teams content into a chat context.** That is the feature, and
> it is also the thing to think hardest about. See below.

---

## Security

This section got a lot more serious when the sources went from one spreadsheet to your mailbox
and your Teams messages.

### What the app can see

Your inbox bodies, your calendar, named Teams chats, one workbook. Nothing shared, nothing
org-wide, nothing belonging to anyone else — unless `ChannelMessage.Read.All` is added, which
is exactly why that scope should be dropped unless it's genuinely needed.

### What leaves the machine

Two destinations, and they are not the same risk.

**Microsoft Graph.** Your own tenant, your own data, your own token. This is the same data
Outlook already moves. Nothing new.

**The Anthropic API**, if the classifier is on. Subject lines, sender addresses and ~500
character snippets of mail and Teams messages leave your machine and your tenant. At a company
doing PAM and IGA, some of that content will concern client environments.

Mitigations in the design:

- **Snippets, never full bodies.** Enough to judge, far less to expose.
- **The classifier is optional** and the app works without it. Rules cover the examples you
  gave.
- **A `neverClassify` list** — senders, domains, subject patterns and chat ids whose content
  never goes to the API, no matter what. They still appear in the queue; they just arrive
  unclassified.
- **Every outbound call is logged** with the item id and the byte count, so there's an auditable
  record of what left.

> **Decided: Bravura has policy approval to use Anthropic on all data.** Classification runs on
> every source, on by default. The mitigations above stay in the design regardless — they are
> good engineering, not a policy workaround, and `neverClassify` remains useful for the handful
> of threads you'd rather keep local whatever the policy says.

### Prompt injection, which is now a real attack surface

The app reads mail from anyone who can email you, and Teams messages from anyone in a watched
chat. That content flows into a classifier prompt and into your Claude chats via the skill.

**Anyone who can email you can put text in front of a model that's making decisions for you.**

- Ingested content is **data, never instruction**. The classifier prompt says so explicitly and
  wraps every snippet in a delimiter.
- The classifier's only output is a structured `{priority, interrupt, reason}`. It cannot call
  tools, cannot act, cannot cause a write. The worst a malicious email achieves is mislabelling
  itself — and a `takeover` it didn't earn, which is why `maxTakeoversPerDay` exists.
- The skill tells Claude the same: item content is data. A mail body asking Claude to acknowledge
  rows or promote things is to be reported, not obeyed.
- Every tool that reaches outside the machine requires you to ask for it in that turn.

This is the kind of thing worth a `/security-review` pass of its own once there's code.

### The rest

- **No secrets in the workbook.** It's a shared file in a tenant, and its contents reach a chat
  context. Reference a credential by name or vault path; never paste one. Goes in the README in
  bold.
- **No invites, no attendees, no send scopes.** The app cannot contact anyone.
- **Local store is encrypted with DPAPI**, scoped to your Windows user. The SQLite file under
  `%LOCALAPPDATA%\rNotice` holds mail and Teams snippets, and DPAPI ties decryption to your
  account with no key for us to manage or lose. It protects against the realistic threat — the
  file lifted off a backup, a stolen disk, another account on the machine — rather than against
  someone who already has your logged-on session, which nothing would.
- **Tokens** live in the MSAL/WAM cache under your profile. The MCP server holds none.
- **Narrow pipe ACL** — current user only.
- **Logging** to `%LOCALAPPDATA%\rNotice\logs`. Titles and senders logged; bodies and snippets
  never.
- **Retention.** Items in `Done`/`Dismissed` are purged after `retentionDays` (default 30). The
  app is not an archive of your mail.

---

## Room to grow

"Reminders now, control later" still shapes two things:

- **The pipe protocol is versioned and tool-shaped** from day one. Machine-control tools mean
  adding verbs, not changing transport.
- **Privileged work will need a service.** It goes in a `rNotice.Service` running LocalSystem,
  exactly like `rMic.Service`, with the agent as its unprivileged client. The agent stays
  unprivileged forever.

The obvious next source is Jira or Salesforce. The item model is already source-agnostic; adding
one means a poller and a mapping, nothing structural.

---

## Build order

1. **Spike the Graph surface.** App registration, WAM sign-in, then prove each risky path on
   real data: `PATCH` a table row; create a categorised event and read it back via
   `calendarView`; run `messages/delta` and confirm `conversationId` reply-matching on real
   threads; run a per-chat Teams delta and find your own id in `mentions`. Throwaway code.
   **Everything is blocked on this**, and the reply-matching and Teams halves are where the
   surprises will be.
2. `rNotice.Core` — item model, store, rules engine, scheduler. Pure logic, fully tested, no
   network, no model.
3. `rNotice.Agent` with **workbook and calendar only** — tray, queue window, alert window,
   autostart. Rules-only classification. Ships without touching your mailbox.
   **Built 2026-10-21 (0.3.0).** Busy and quiet-hours suppression came along, since Core
   already had it. Meetings use the alert window until step 4's takeover. The first real run
   changed two things:
   - **A meeting more than `takeoverGraceSeconds` past its start no longer alerts.** On startup,
     every long block already under way (a 06:00 deploy window, at 12:30) put up an alert.
     The meeting stays in the queue. Non-meeting takeovers that are overdue still alert.
   - **The queue sorts "needs you now" before "upcoming"**, and the tray counts only the
     former as urgent. Every meeting is P1, so 28 meetings over 14 days sat above everything,
     and the tray would have shown urgent permanently.

   - **Meetings enter as Planned, not Intake** (decided 2026-10-22, see *Stages*). Intake had
     filled with every meeting in the horizon.
4. Takeover window, toast, suppression. ~~Acknowledge write-back~~ — **moved to steps 9–10
   (2026-10-23)**, where the MCP `acknowledge_action` tool needs it; nothing uses the workbook
   yet. Toasts and suppression shipped with step 3.
   **Takeover built 2026-10-23 (0.5.0)** to Robert's design, which replaces *Takeover window*
   below where they differ: the large meeting alert stays as it is, and **every monitor dims a
   few shades** behind it. **Double-click the dim to clear it**; the alert stays up for Join,
   Open in Outlook or Mark done. The dim and the alert both ignore input for `armingDelayMs`
   (750) after they appear, so a click already in flight can't act on something unseen.
5. Mail source. Read-only, rules-only. The reply-toast rule is the whole value here.
   **Built 2026-10-22 (0.4.0), ahead of step 4** — see *Mailbox*. It shares `refreshSeconds`
   with calendar and workbook, so one countdown covers all three.
6. Teams source, named chats only.
   **Built 2026-10-23 (0.8.0)** — see *Teams*. Picked group chats for @mentions, every
   one-on-one chat as one item per chat. No per-chat delta exists for delegated access, so it
   reads by watermark.
7. Model classification, behind a flag, off by default.
   **Decided 2026-10-23:** effort starts at `low` (Opus 5.5 defaults to `medium`; raise it only if
   a labelled sample shows `low` missing things). **Phase 1 classifies Teams messages only**; mail
   stays rules-only until phase 1 has run on real traffic. API calls bill to the Anthropic Console
   org that owns the key, separately from Claude.ai / Claude Code plan usage.
   **Pending AI (2026-10-28):** when the model judges a Teams reply important, it should come up
   as a top window alert (the alert window, no dim) instead of a toast. Until the API is
   connected, replies stay as they are.
8. Promotion: calendar, workbook, demote, staggered reminders, drift, free slots.
   **Calendar promotion built 2026-10-23 (0.7.0)** — see *Promoting to the calendar*. Free
   slots shipped earlier (0.4.4). Workbook promotion is out (open question 6, still open).
9. `rNotice.Mcp` + pipe protocol. **Includes the workbook acknowledge write-back** (from step
   4), proven by the 2026-10-21 spike, for `acknowledge_action`.
10. Skill, README, install script.
11. **Presence: joined detection.** **Built 2026-10-27 (0.9.0).** Only `InACall`,
    `InAConferenceCall` and `Presenting` count: Teams sets *In a meeting* from the Outlook
    calendar, joined or not, while call and presenting come from the Teams app
    ([User presence in Teams](https://learn.microsoft.com/en-us/microsoftteams/presence-admins)).
    Two meetings under way at once are ambiguous and neither is marked. Note: the Graph
    `presence` reference lists the call values under `availability`; its own get-presence
    example returns `"activity": "Presenting"`, which is what rNotice reads. Tray → Check Teams
    presence shows the live value, for confirming the mapping in a real meeting.
    Added 2026-10-26, after `Presence.Read` was granted. While
    a meeting is in progress, read your own presence once a minute; if Teams shows you in a
    call, in a meeting or presenting, mark the meeting joined (green), as Join in rNotice does.
    Kept to what was promised to IT: your own presence only, the `activity` field only, nothing
    stored, polled only while a meeting is under way.

**Working on it (0.9.4, 2026-10-27).** A task can be marked in progress. Decided with Robert: the
plan stays where it is; alerts stop until a check-in when the planned block ends (or after
`planMinutes`); the check-in is an ordinary alert offering Done, Still working (+15/30/60 min) or
Rebook; time spent is recorded per item (`work_started_utc`, `worked_minutes`, local only) and
shown. A task being worked on is exempt from meeting-clash bounces and from the Teams
reply-closes rule.

**Running over (0.9.5, 2026-10-27).** Robert's example: A 9–10, B 10–10:30, C 10:30–11; at 10:00,
continuing A for 30 min makes A 9–10:30, B 10:30–11, C 11–11:30. Rules (`Overrun`):

- **Still working** on a planned task grows its block by that much from the planned end (answered
  well after the end: from now, on the quarter hour). Its Outlook event follows.
- Each later plan the grown block, or an already-pushed plan, now lands on moves to the first free
  slot after it, in planned order: quarter hour, work hours, clear of meetings (so it can jump a
  meeting or go to the next working day). A plan with room to spare stays, and so does what's
  after it. Moved plans get a note (*Pushed to 10:30: 'A' ran over*) and re-arm their alert.
- The dropdown says what each choice does: *30 min · moves 2 plans · into Standup*.
- **Hand-off.** The check-in comes no later than the next plan's alert, and while you're working a
  later plan doesn't alert on its own — one prompt at 10:00, not two. The check-in shows
  *Next up: B at 10:00* and **Start next**, which stops A (time kept, back to Intake with a note;
  the time it had in its block stays as worked time, below) and starts work on B.
- Dragging a working task's event end in Outlook moves its check-in with it; it doesn't push the
  plans after it (rNotice only pushes on **Still working**).
- An unplanned task has no block: **Still working** only moves its check-in.

**Done stays on the schedule (0.9.6, 2026-10-28).** Asked by Robert: a past item marked done
should stay in the main window's schedule. A planned task marked done keeps its block, grey,
with *done · worked 1 h 05 min*, and keeps its Outlook event as a record; rNotice never creates
an event for a done task. Done before its block started frees the slot (plan cleared, event
removed). The calendar is now read from local midnight rather than an hour back, so today's
finished meetings stay on the schedule (grey, as busy time).

**Worked time stays in Outlook (0.9.7, 2026-10-28).** Asked by Robert: rNotice shouldn't remove
time you've already spent from Outlook. A plan and its event are no longer one-to-one for life
(`WorkedTime`):

- Once a planned block has started (a minute in), any change that leaves it — rebook, re-plan,
  drag, **Start next**, a meeting bouncing it to Intake, dismiss — keeps the time so far as a
  **worked block**: from the block's start to now (or the block's end, if that came first).
- If the block was in Outlook, that event stays as the record, trimmed to the same span, and the
  item lets go of it. A new plan gets a new event. So a task worked 9:00–9:40 and rebooked to
  14:00 has two events: 9:00–9:40 and 14:00–14:30.
- Worked blocks show on the schedule in grey, *worked*, and select their item. They're stored as
  times and ids only (`worked_blocks`), and go when the item is purged. One whose event was
  deleted in Outlook goes from the schedule on the next calendar read.
- Not affected: a block that hasn't started still moves or goes as before; **Still working**
  keeps the same block and only moves its end; Done keeps the whole block (0.9.6).

**Planned tasks always interrupt (0.8.3, 2026-10-26).** A task you plan with a time alerts at
that time as at least `planInterrupt` (default `Takeover`: every monitor dims, the window sits on
top). Before this a plan kept the classification of the mail or chat it came from — usually
`P3`/`silent` while there's no classifier — and the scheduler skips silent items, so four planned
tasks on 2026-10-26 passed without a sound. A plan found more than `takeoverGraceSeconds` after
its start (rNotice was off) comes up once as an ordinary alert, without the dim.

Step 3 is a genuinely useful app on its own. Each of 5, 6 and 7 adds a source or a capability
that someone might veto — mail scope, Teams scope, API egress — and each is severable. **If any
one of them is refused, everything before it still ships.** That is deliberate: the risky
approvals shouldn't be able to block the whole project.

---

## Open questions

**Fourteen of seventeen are settled.** What remains: the app registration (1, with IT), the
Teams chat ids (3, supplied at install), and one real design question (6).

1. ~~The app registration~~ **Created by IT on 2026-10-21.** Client ID
   `2923549f-f444-48f7-8084-c8b7799db2e0`. **Still unknown: which of the five scopes were
   actually consented to** — build 0.1 (`rNotice.Probe`) exists to answer exactly that. Original
   ask, for the record: public client / native, delegated:
   `Files.ReadWrite`, `Calendars.ReadWrite`, `MailboxSettings.Read`, `Mail.Read`, `Chat.Read`.
   No admin-consent scope. **`MailboxSettings.Read` was added after the earlier drafts — make
   sure it's in the request.** The long pole: build steps 5-7 wait on it, steps 1-4 do not.
   Still unknown: how long it takes, and whether `Mail.Read` draws extra scrutiny.
2. ~~Channel @mentions — in or out?~~ **Answered: out of v1.** No `ChannelMessage.Read.All`,
   no admin-consent scope in the app registration. Channel mentions, if they matter, arrive via
   Teams' missed-activity emails in the mailbox we already read.
3. ~~Which Teams chats?~~ **Answered: picked in the app** (tray → Watched Teams chats…), plus
   every one-on-one chat. ~~Which mail folders?~~
   **Answered: Inbox only.**
4. ~~Where does the workbook live?~~ **Answered: OneDrive for Business,
   `Documents/efficiency`.** `Files.ReadWrite` covers it; no admin consent needed.
5. ~~Does the sheet already exist?~~ **Answered: no.** rNotice ships the template and creates the
   file on first run. No migration, no manual setup.
6. **Should mail and Teams items be promotable to the workbook?** *Still open — the only real
   design question left.* It is the one place the app creates a row. The alternative is that
   mail and Teams items live only in rNotice's own store and the workbook stays purely yours.
   Not blocking: it's a build-step-8 decision, and the app is complete without it.
   **Left out of step 8 (2026-10-23)**: calendar promotion shipped without it.
7. ~~Classifier model?~~ **Answered: `claude-opus-5-5`.** Configurable, so it can change after
   real behaviour is visible.
8. ~~Can Bravura content go to the Anthropic API?~~ **Answered: yes, policy approval covers all
   data.** Classification is on by default across every source.
9. ~~Confirm the demote exception.~~ **Answered: demote deletes, behind a confirmation dialog
   naming the event.** Not exposed as an MCP tool.
10. ~~Should the local store be encrypted?~~ **Answered: yes, DPAPI**, scoped to your Windows
    user.
11. ~~What happens to a promoted event when its item is completed?~~ **Answered: delete it.** No
    dialog — the Complete press is the confirmation. rNotice only ever deletes events it created
    itself.
12. ~~Does promoting change alerting?~~ **Answered: no.** rNotice alerts at full lead time;
    Outlook's reminder stays staggered 5 minutes behind as the backstop for when the app isn't
    running.
13. ~~Working hours for free-slot search?~~ **Answered: your Outlook mailbox settings.** Adds
    `MailboxSettings.Read` to the app registration — **get it into the IT request.**
14. ~~Which meetings alert, and which take over?~~ **Answered: every meeting does both**, minus
    the subject blocklist. My attendee-based split is dropped. Mitigations in the design: the
    daily count is a report rather than a silent cap, and dismissal has a 750ms arming delay.
    Revisit if the takeover log shows reflex dismissals.
15. ~~Timezone display?~~ **Answered: local and UTC, always both.**
16. ~~Recurrence on workbook rows?~~ **Answered: no column.** Repeats live as recurring Outlook
    events and arrive as calendar items.
17. ~~Name.~~ **Answered: rNotice.**
