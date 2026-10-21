# Draft email to IT

Attach `IT-REQUEST.md` (or paste it below the signature) — the repo is private, so don't rely on
a link. Keep the email itself short; the detail is in the attachment.

---

**Subject:** App registration request — internal productivity tool (rNotice)

---

Hi [name],

I'm building a small Windows desktop tool for my own use and I need an Entra app registration
for it. I tried to create it myself but `Users can register applications` is set to `No`, so
it's blocked at **New registration** with a 401.

**Either of these works for me — whichever you prefer:**

1. **You create the registration** and send me the Application (client) ID. Smaller change,
   nothing granted to me on an ongoing basis. This is my preference.
2. **Assign me the Application Developer role** and I'll create it myself. Flagging honestly:
   Microsoft labels that a privileged role, and there's a third-party report that 15 directory
   permissions were added to it in August 2026 that Microsoft Learn doesn't list yet. I'd rather
   you went with option 1 unless you'd prefer not to be in the loop on things like this.

**What the tool does:** reads my Outlook inbox, my calendar, a few named Teams chats and a task
spreadsheet in my OneDrive, and shows me one prioritised list instead of four places to check.
Its main visible behaviour is an on-screen alert before a meeting starts. It runs on my
workstation, under my account, unelevated. One user, one machine, not deployed to anyone.

**Registration properties:**

| | |
|---|---|
| Name | `rNotice` |
| Account types | Single tenant |
| Type | Public client / native — **no client secret, no certificate** |
| Redirect URI | `ms-appx-web://Microsoft.AAD.BrokerPlugin/{client_id}` |
| Public client flows | Enabled |

**Delegated Microsoft Graph permissions** (no application permissions):

| Permission | For |
|---|---|
| `Files.ReadWrite` | Read the task list, write back a completion status |
| `Calendars.ReadWrite` | Read my calendar; create time blocks for tasks I schedule |
| `Mail.Read` | Spot replies on threads I'm waiting on — **read only** |
| `Chat.Read` | A short named list of Teams chats, for @mentions |
| `MailboxSettings.Read` | My working hours, so it doesn't suggest scheduling work at 11pm |

**Three things that should make this straightforward:**

- **No send permission of any kind.** It cannot email, reply, post to Teams or send an invite —
  not by policy, by the absence of the permission.
- **No `.All` scopes and no application permissions.** It sees exactly what I can already see,
  acts only as me, and stops working the moment my account is disabled.
- **Broker-based auth (WAM)**, so it inherits Conditional Access, device compliance and Windows
  Hello rather than sidestepping them — it authenticates the same way Outlook does.

If `Mail.Read` is the sticking point, **approve `Files.ReadWrite`, `Calendars.ReadWrite` and
`MailboxSettings.Read` and I have a working tool.** Mail and Teams can be a separate
conversation. The build is staged so each source is independent.

**What I need back:** the Application (client) ID, and consent granted for whichever scopes
you're comfortable with. If you refuse any, just tell me which so I can build around it.

On transfer — **nothing here is secret.** It's a public client registration, so there's no
client secret or certificate, and neither the client ID nor the tenant ID is a credential.
Plain email is fine. Happy to use Safe if you'd rather, but it isn't needed.

Full technical detail, including data handling and the threat model, is in the attached document.

Thanks,
Robert
