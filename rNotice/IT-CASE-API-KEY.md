# Draft reply to IT: why an API key

**Draft, 2026-10-30. Shelved:** not sent. Robert is running the classify routine for a few weeks
first (`ROUTINE-CLASSIFY.md`, 0.10.0). Its real timings and failures will make this case
stronger or answer it.

IT declined the key requested on 2026-10-29 (`IT-REQUEST-ANTHROPIC-API.md`) and asked Robert to
show that an API key is required and gives a benefit. Fill in the bracketed parts before
sending. The routine timing should be measured, not guessed.

---

**Subject:** RE: Anthropic API key request — why the API, not a routine

---

Hi [name],

Thanks for looking at this. You asked me to show the key is needed and worth it. The honest
answer: it isn't strictly required. I can do the same job with a Claude Code routine on my
existing subscription, and I've designed that as the fallback. But the API is the **smaller,
safer and more reliable** way to do it. Here's the comparison.

**What the job is.** Read a short Teams conversation (each message cut to 2000 characters) and
return a priority, a confidence score, a title and a description. One answer per conversation.
No other action.

| | API key | Routine (no key) |
| --- | --- | --- |
| **What the model can do** | Only answer. No tools are given to it, so it can't act on anything. | Runs as a full Claude Code session with my connectors. It needs the Microsoft 365 connector to return its answer, and that connector can't be cut down to one tool. So it could technically send mail or Teams messages as me. Only the prompt stops it. |
| **Effect of a hostile message** | It can only mislabel itself. | The same, unless it talks the session into using a connector. That is a far bigger blast radius. |
| **Answer format** | Enforced by the API: a fixed JSON schema. | A file the session writes to my OneDrive. It can be missing or malformed, and rNotice has to poll for it. |
| **Speed** | One request, answered in seconds. | Starts a cloud session per request: [measured: about N minutes]. |
| **Limits** | Per-key spend limit, set by you. | 30 requests an hour per routine. Shares my Claude usage limit, so a busy day can block my other work, or the other way round. |
| **Status** | Generally available, versioned API. | Research preview. The trigger endpoint is experimental, and Anthropic says its shape and limits may change. |
| **What's stored outside my machine** | Nothing of mine. | A result file in my OneDrive for each request, until rNotice deletes it, and a session transcript in my Claude account. |
| **Audit** | Every call logged locally with item ID and byte count. A spend limit you control. | A session per request in my Claude account. Harder to review, and you have no control over it. |

**Same data either way.** Both send the same text to Anthropic. Bravura's policy approval
already covers that. The question is only which path is narrower.

**What the routine does better.** It can look up a case in Salesforce, through the read-only
connector, to link the right case. With the API, rNotice can't do that for Teams messages. I
think that's a small gain against the risks above.

**Cost.** A few dollars a month at most, at [tens to low hundreds of messages a day]. A $20
monthly limit on the key covers it with room to spare.

**What I'm asking for.** The same as before: a key with access to `claude-opus-5-5`, with a
spend limit. If there's a specific concern behind the decline, such as where the key is stored,
who can use it, or billing, tell me and I'll address it directly. I'm happy to walk through the
code.

Thanks,
Robert
