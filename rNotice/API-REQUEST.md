# Draft request: Anthropic API key for rNotice

For whoever issues Anthropic Console API keys. Edit the bracketed parts before sending.

---

**Subject:** rNotice — Anthropic API key request

---

Hi [name],

I'd like an **Anthropic API key** for rNotice, a desktop assistant I've built for my own work.

**What rNotice is.** A Windows app that runs under my account. It reads my own Outlook mail,
calendar and Teams chats through Microsoft Graph, using the app registration IT set up. It turns
what needs my attention into a queue and a day schedule. It alerts me before meetings, plans
tasks into free time on my calendar, and links case emails to the Salesforce case.

**What the API is for.** Deciding which messages need me. Rules already cover the clear cases:
meetings, @mentions, replies on threads I've written in, and VIP senders. What's left is a
judgement call, mostly Teams messages. For example, "can you take a look at this when you get a
chance" may or may not be a request. For each of those, the model returns a priority, how
strongly to interrupt me, and a one-line reason. An important Teams reply would then come up as
an alert window instead of a toast.

**What is sent.**
- For each message: the subject or chat name, the sender, and the full message text.
  Attachments are never sent.
- My classification rules and past corrections, as a cached prefix.
- Phase 1 is **Teams messages only**. Mail stays on rules until phase 1 has run on real traffic.
- Bravura has policy approval to use Anthropic on all data. Even so, rNotice has a "never
  classify" list for senders, domains and chats whose content must not be sent.

**What the model can do.** Nothing but answer. The output is a fixed JSON schema (priority,
interrupt, reason). There are no tools, so it cannot send, delete or change anything. Message
content is passed as data, not instructions. The worst a hostile message can do is mislabel
itself, and a daily cap on full-screen alerts limits that.

**Usage and cost.**
- Model: `claude-opus-5-5`, effort `low`. The model is a setting and can be changed.
- Each message is classified once and never re-sent. Messages are batched on a timer.
- Expected volume: [tens to low hundreds of messages a day]. Teams messages are usually short,
  so I expect a few dollars a month at most.
- I'm happy to have a monthly spend limit set on the key, for example [$20].

**Handling the key and the data.**
- [Planned] The key is stored encrypted with Windows DPAPI under my account, like rNotice's
  local data. It is not in source control and not in any shared file.
- Every API call is logged locally with the item ID and byte count, so there's a record of what
  was sent. Message text is never written to the logs.
- If the key is revoked or the API is unreachable, rNotice keeps working on rules alone.

**What I need.**
- An API key in [the Bravura Console org / workspace name], with access to `claude-opus-5-5`.
- [Optional] A spend limit on the key or workspace.

Thanks,
Robert
