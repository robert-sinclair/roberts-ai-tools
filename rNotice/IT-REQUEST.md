# rNotice — Entra app registration request

**Requester:** Robert Sinclair, DevOps / Cloud Solutions Architect
**For:** an internal productivity tool, used by one person on one workstation
**Asking for:** one Entra ID app registration with five delegated Microsoft Graph permissions

This document assumes no prior knowledge of the project. Everything IT needs to assess and
action the request is below.

> **What I already tried:** creating the registration myself. App registrations is viewable, but
> **New registration returns 401 "You don't have access"** — the tenant has `Users can register
> applications = No`, so this needs you. Nothing below has been created yet.

---

## 1. What the tool is

rNotice is a small Windows desktop application I'm building for my own use. It watches four
places work arrives — my Outlook inbox, my Outlook calendar, a handful of named Teams chats, and
a task spreadsheet in my OneDrive — and shows me a single prioritised queue instead of four
places to check.

Its one visible behaviour is an on-screen alert. When a meeting is about to start, the screen
dims and tells me. When something lands that needs an answer, it joins a list.

It runs on my workstation, under my account, with no elevated privileges. It is not a service,
it is not deployed to anyone else, and it does not run in Azure or on any server.

**This is a personal productivity tool, not a product.** If it turns out to be useful to other
people later, that is a separate conversation with a separate security review.

---

## 2. The registration

| Property | Value |
| --- | --- |
| Name | `rNotice` |
| Supported account types | **Single tenant** — accounts in this organizational directory only |
| Application type | **Public client / native.** No client secret, no certificate. |
| Platform | Mobile and desktop applications |
| Redirect URI | `ms-appx-web://Microsoft.AAD.BrokerPlugin/{client_id}` |
| "Allow public client flows" | Yes |
| Permission type | **Delegated only.** No application permissions. |
| Owner | Robert Sinclair |

The redirect URI is the standard Windows Web Account Manager (WAM) broker URI. It is set in the
app registration, not in application code, and it means authentication goes through the Windows
broker — so the app inherits Windows Hello, Conditional Access and device compliance policy
rather than bypassing them.
([Microsoft reference](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-acquire-token-wam))

---

## 3. Permissions requested

All five are **delegated**. The app acts as me, sees only what I can already see, and cannot
reach another person's mailbox, calendar, chats or files.

| Permission | Why it's needed | What it does **not** grant |
| --- | --- | --- |
| `Files.ReadWrite` | Read the task list and write back a completion status | Only files I can already access. Not `.All` — no org-wide file access. |
| `Calendars.ReadWrite` | Read my calendar to know what's coming; create time blocks for tasks I schedule | My calendar only. Cannot read or write anyone else's. Cannot send meeting invitations. |
| `Mail.Read` | Read my inbox to spot replies on threads I'm waiting on | **Read only.** No `Mail.Send`, no `Mail.ReadWrite`. The app cannot send, reply, forward, delete or move a single message. |
| `Chat.Read` | Read a short, named list of Teams chats to spot @mentions | My chats only. Explicitly **not** `ChannelMessage.Read.All` — no org-wide channel message access. Cannot post messages. |
| `MailboxSettings.Read` | Read my working hours, so the app doesn't suggest scheduling work at 11pm | Read only. Cannot change any mailbox setting. |

### Scopes deliberately not requested

Worth stating explicitly, because they are the ones that would reasonably cause concern:

- **`ChannelMessage.Read.All`** — would allow reading Teams channel messages across the
  organisation. Dropped from scope. The feature it would have enabled is handled instead by
  Teams' own missed-activity emails, which already arrive in my inbox.
- **`Mail.Send`, `ChatMessage.Send`** — the app never communicates on my behalf.
- **Any `.All` variant**, and **any application permission** — the app has no reason to act
  without a signed-in user, and nothing it does should work when I'm not there.

---

## 4. What the application cannot do

By design, and enforced by the permissions above rather than by promises in code:

- **It cannot send anything.** No email, no Teams message, no meeting invite. It has no send
  permission of any kind.
- **It cannot delete my mail or anyone else's anything.** The only objects it deletes are
  calendar events it created itself, which carry a marker it set.
- **It cannot see other people's data.** Delegated permissions, single user, no `.All` scopes.
- **It cannot run unattended.** No application permissions, no secret, no certificate. Every
  call rides my interactive sign-in.
- **It cannot be used by anyone else.** Single tenant, installed on one machine, no distribution.

---

## 5. Data handling

**Microsoft 365 data stays in the tenant**, except as follows.

The app uses the Anthropic API (Claude) to help sort incoming items by importance — deciding
whether a message is a question that needs an answer or something that can wait. For that, it
sends the subject line, the sender address and roughly the first 500 characters of a message.
Never the full body, never attachments.

**Bravura has existing policy approval to use Anthropic across all data.** This request does not
seek new approval for that; it is noted for completeness.

Handling details:
- A configurable exclusion list blocks named senders, domains and chats from ever being sent.
- Every outbound call is logged locally with the item id and byte count.
- Classification can be switched off entirely; the app still works without it.

**On the workstation:**
- Message snippets are held in a local SQLite database, **encrypted with Windows DPAPI**, scoped
  to my Windows account.
- Completed items are purged after 30 days. The app is not a mail archive.
- Authentication tokens are held by the Windows broker (WAM), not by the application.
- Logs record subjects and senders, never message bodies.

---

## 6. Why delegated and not application permissions

Partly principle, partly necessity.

The Microsoft Graph Excel API — used to read the task spreadsheet — **does not support
application permissions at all**. Microsoft's documentation states: *"Application permissions
are not supported for this API. Your app must sign in as a user."*
([reference](https://learn.microsoft.com/graph/api/tablerow-list))

That constraint pushed the whole design toward delegated-only, which is the better posture
anyway: the app's reach is exactly my own, it stops working the moment my account is disabled,
and it has no credential that could be lifted and replayed.

---

## 7. If you can only approve part of this

The build is deliberately staged so that a partial approval is still useful. In priority order:

| Priority | Permissions | Gets me |
| --- | --- | --- |
| 1 | `Files.ReadWrite` + `Calendars.ReadWrite` + `MailboxSettings.Read` | A working task queue and meeting alerts. The core of the tool. |
| 2 | add `Mail.Read` | Spotting replies on threads I'm waiting on. |
| 3 | add `Chat.Read` | Teams @mentions in named chats. |

**If `Mail.Read` is the sticking point, approve group 1 and we can discuss the rest separately.**
Nothing is wasted — the application is built so each source is independent.

---

## 8. Questions you may have

**Is this going to production?**
No. One user, one workstation, no deployment pipeline, no other users.

**Who supports it?**
I do. It is my own code, in a Bravura GitHub repository
(`Bravura-Security/roberts-ai-tools`).

**Does it need a client secret or certificate?**
No. Public client / native, broker-based auth. There is no credential to rotate or leak.

**Does it bypass Conditional Access or MFA?**
No — the opposite. Using the WAM broker means it goes through the same Windows authentication
path as Outlook and Teams, inheriting Conditional Access, device compliance and Windows Hello.

**Can you revoke it?**
Yes, at any time, by removing the app registration or revoking consent. The app fails closed —
no cached data is reachable without a valid token, and the local store is DPAPI-bound to my
Windows account.

**Does it cost anything?**
No Microsoft licensing cost. Metering on the Teams Graph APIs ended in August 2025.
([reference](https://learn.microsoft.com/en-us/graph/metered-api-list))
The Anthropic API usage is a few dollars a month at most, under existing approval.

---

## 9. What I need back

1. **The registration created**, with the properties in section 2. I can't create it myself —
   `Users can register applications` is set to `No` in this tenant.
2. The **Application (client) ID**.
3. **Consent granted** for the five delegated permissions — or for whichever subset you're
   comfortable with, per the staging in section 7.
4. If anything is refused, **which scopes**, so I can build around it.

### An alternative, if you'd rather not create it for me

Assigning me the **Application Developer** role would let me create the registration myself. It
is scoped to exactly that: create app registrations, own what I create.

I'm raising it as an option, not asking for it. Two things you should weigh: Microsoft labels it
a privileged role, and a third-party tracker reports 15 directory permissions were quietly added
to it in August 2026 that Microsoft Learn doesn't yet list. **Creating the registration for me is
the smaller change and I'd rather you did that** — I mention the role only in case you'd prefer
not to be in the loop on future tools.

I'm assuming admin consent is required rather than user consent. Several of these are
user-consentable in Microsoft's own reference, but tenant user-consent settings override that,
and I'd rather the request arrive correctly scoped than bounce.

The full technical specification is in the same repository at `rNotice/SPEC.md` if you want the
detail — including the security section, which covers prompt injection, data at rest and the
threat model.
