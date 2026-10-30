# Sent to IT: Anthropic API key request

**Sent 2026-10-29** by Robert to the Bravura Security IT Department. This is the text as sent,
kept as the record of what was promised. The step 7 classifier is built to these promises. If
the build needs to go past them, IT is told first.

**Status: declined by IT, 2026-10-30.** Step 7 moves to a Claude Code routine on Robert's
subscription instead: see `ROUTINE-CLASSIFY.md`.

**What it promises, in short:**
- Sent for each message: subject or chat name, sender, and the **first 2000 characters**.
  Nothing more of the message.
- Phase 1 is **Teams messages only**. Mail stays on rules until phase 1 has run on real traffic.
- Output is a fixed JSON schema (priority, interrupt, reason). No tools. Message content is
  data, not instructions. A daily cap on full-screen alerts.
- `claude-opus-5-5` at effort `low`. Each message is classified once, batched on a timer.
- Suggested spend limit: $20 a month.
- [Planned] The key is stored encrypted with DPAPI under Robert's account, never in source
  control or a shared file.
- Every API call is logged locally with the item ID and byte count. Message text is never
  logged.
- With no key or no API, rNotice keeps working on rules alone.

---

## As sent

Anthropic API key request
Robert Sinclair
SM Bravura Security IT Department

I'd like an Anthropic API key for a desktop assistant I'm building for my own work.

This application is a Windows app that runs under my account. It reads my own Outlook mail,
calendar and Teams chats through Microsoft Graph, using the app registration IT set up. It turns
what needs my attention into a queue and a day schedule. It alerts me before meetings, plans
tasks into free time on my calendar, and links case emails to the Salesforce case.

What the API is for. Deciding which messages need me. Rules already cover the clear cases:
meetings, @mentions, replies on threads I've written in, and VIP senders. What's left is a
judgement call, mostly Teams messages. For example, "can you take a look at this when you get a
chance" may or may not be a request. For each of those, the model returns a priority, how
strongly to interrupt me, and a one-line reason. An important Teams reply would then come up as
an alert window instead of a toast.

What is sent.

* For each message: the subject or chat name, the sender, and the first 2000 characters.

* Phase 1 is Teams messages only. Mail stays on rules until phase 1 has run on real traffic.
* Bravura has policy approval to use Anthropic on all data.
*
* What the model can do. Nothing but answer. The output is a fixed JSON schema (priority,
  interrupt, reason). There are no tools, so it cannot send, delete or change anything. Message
  content is passed as data, not instructions. The worst a hostile message can do is mislabel
  itself, and a daily cap on full-screen alerts limits that.

Usage and cost.

* Model: `claude-opus-5-5`, effort `low`. The model is a setting and can be changed.
* Each message is classified once and never re-sent. Messages are batched on a timer.
* Expected volume: [tens to low hundreds of messages a day]. I expect a few dollars a month at
  most. Claude suggested a spend limit of $20 should be more than sufficient for this,
  especially at this stage.

Handling the key and the data.

* [Planned] The key is stored encrypted with Windows DPAPI under my account, like the
  applications local data. It is not in source control and not in any shared file.
* Every API call is logged locally with the item ID and byte count, so there's a record of what
  was sent. Message text is never written to the logs.
* If the key is revoked or the API is unreachable, the application keeps working on rules alone.

What I need.

* An API key in [the Bravura Console org / workspace name], with access to `claude-opus-5-5`.
  (or opus 5 I guess)
* [Optional] A spend limit on the key or workspace.

Thanks, Robert
