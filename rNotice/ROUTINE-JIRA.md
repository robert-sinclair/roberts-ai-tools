# The Jira routine (build step 12)

Asked by Robert on 2026-11-09. The queue window has a **Jira** tab that lists tickets from a
filter (SAASK by default, but any JQL works), grouped by priority, then oldest first, with the
owner shown. You can plan a ticket like anything else.

**Read only, both ways.** Planning a ticket makes an rNotice item and writes nothing to Jira.
Marking that item Done closes the rNotice item only. The ticket is untouched.

## The flow

```
rNotice ── fire (requestId, jql) ──▶ Jira routine ── searchJiraIssuesUsingJql ──▶ Jira
   ▲                                     │
   └──── reads <requestId>.json ◀── OneDrive efficiency folder ◀── sharepoint_upload_file
```

It works like the classify routine (`ROUTINE-CLASSIFY.md`): rNotice fires the routine with one
JSON request, the routine writes `<requestId>.json` to the `efficiency` folder, and rNotice
reads the file, checks it, keeps it and deletes it.

**Why a routine of its own.** The classify routine reads Teams messages written by other
people. Keeping the Atlassian connector out of that routine means a message can't steer it into
Jira. This routine only receives rNotice's own filter.

## When rNotice asks

- When you open the Jira tab and the list shown is more than 30 minutes old, or the filter
  changed.
- When you press **Refresh**.
- Never in the background. Each ask is one routine run, and it counts toward the 25 runs an hour
  that rNotice allows across both routines.
- One request at a time. No answer in 15 minutes: given up, and the old list stays.
- While a request is out, rNotice checks the folder for the answer every 20 seconds.

## What rNotice sends (the `text`)

```json
{"requestId": "5b0c4a1e-…", "kind": "jiraTickets", "resultsFolder": "Documents/efficiency",
 "jql": "project = SAASK AND statusCategory != Done", "maxTickets": 200}
```

The filter is `jiraJql` in `settings.json`. The default is
`project = SAASK AND statusCategory != Done`.

## What the routine writes (`<requestId>.json`)

```json
{"requestId": "5b0c4a1e-…", "kind": "jiraTickets", "tickets": [
  {"key": "SAASK-12", "summary": "Rotate the certs", "status": "To Do", "priority": "High",
   "assignee": "Jane Doe", "created": "2026-10-01T15:30:00.000-0600", "type": "Task"}]}
```

rNotice checks it before using it:
- The `requestId` must match the request that's out. Anything else is dropped.
- Each ticket needs a key shaped like `SAASK-12`, a summary and a created date. A ticket missing
  any of them is skipped and counted.
- Text is cut to length: summaries to 200 characters, the other fields to 60.
- Ticket links are built from `jiraUrl` in `settings.json`, never taken from the file.
- At most 200 tickets.

The list is kept encrypted in rNotice's store, so the tab shows it after a restart.

## The tab

- **Grouped by priority:** the usual Jira names, highest first. A name rNotice doesn't know
  comes after them, and no priority ("None") comes last. Today every open SAASK ticket has
  priority None, so they all sit in one group.
- **Within a group:** oldest first.
- **Columns:** Key, Summary, Owner, Status, Age (days since created), and rNotice (whether
  rNotice has an open item for it: "In Intake" or "Planned Tue 14:00").
- **Plan** (button, double-click, or right-click → Plan…) opens the ordinary Plan dialog.
  Cancelling saves nothing. If an open item already exists for the ticket, that item is planned
  again instead of making a second one. A ticket whose item is Done gets a new item.
- **Right-click → Open in Jira** opens the ticket.

The item is `Source.Jira`. Its title is `SAASK-12 <summary>`. Its text has the summary, the
type, status and owner, and the link. It's a copy from the day you planned it and doesn't
follow later changes in Jira.

## The routine

**Settings:**
- **Name:** rNotice Jira
- **Trigger:** API only.
- **Repository:** none.
- **Connectors:** Microsoft 365 (to write the file) and Atlassian (to read Jira). Remove every
  other connector. If the Atlassian connector lets you turn tools off, leave only
  `searchJiraIssuesUsingJql` on.
- **Environment:** Default.
- **Model:** Sonnet 5.5. The job is copying, not judging, and a faster model returns sooner.

The Atlassian connector can create and edit tickets, and the Microsoft 365 connector can send
mail and Teams messages as you. The prompt forbids both. That's an instruction, not a guarantee.

**Prompt:** paste `ROUTINE-JIRA-PROMPT.txt`.

**Then in rNotice:** tray → Dev view → **Jira routine**. Paste the routine's fire URL and the
token from its API trigger, then **Save**. rNotice refuses the classify routine's fire URL here.

## What this reads, and IT

This is a new read. It isn't covered by any of the IT requests, which are all about Microsoft
Graph. rNotice itself never talks to Jira and holds no Jira credential: the routine reads Jira
with your own Atlassian connector in Claude, the same as reading Jira in a Claude chat. What
reaches rNotice is key, summary, status, priority, assignee name, created date and type for the
tickets that match the filter.
