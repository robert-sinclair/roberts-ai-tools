# rNotice — app registration runbook

How to create the Entra app registration yourself, find out whether you can consent to the
permissions without an administrator, and what to do when you can't.

> ## Outcome: blocked at step 1 — IT is required
>
> Tried 2026-10-16. App registrations is **viewable**, but **New registration returns 401 "You
> don't have access"**. That is the tenant setting `Users can register applications = No`.
>
> **The CLI route does not help here.** That workaround applies to a different setting
> (*Restrict access to Microsoft Entra admin center*), which blocks only the portal. This one is
> enforced in the directory authorization policy (`AllowedToCreateApps = false`), so
> `az ad app create` and `New-MgApplication` fail identically. Don't spend time on them.
>
> **Go to `IT-REQUEST.md`.** The rest of this document is kept for the record, and for the
> consent test in step 4 — which becomes useful again once the registration exists.

Originally written as a self-serve attempt before escalating. Worst case you learn exactly which
permissions need an admin, which makes the request to IT smaller and much more specific.

---

## Two rights, not one

Creating an app registration and consenting to its permissions are governed separately. You can
easily have the first and not the second.

| | Controlled by | Default |
| --- | --- | --- |
| **Create an app registration** | Entra → Users → User settings → *Users can register applications* | **Yes** — all users can register apps and manage the ones they create ([Microsoft](https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/delegate-app-roles)) |
| **Consent to its permissions** | Entra → Enterprise applications → Consent and permissions → *User consent settings* | Varies. Commonly "verified publisher + low impact permissions only". |

The second is the one that will stop you. Under the "verified publisher" setting, apps
**registered in your own tenant do qualify** — but only for permissions an admin has classified
as *low impact*, and `Mail.Read` is very unlikely to be in that set.
([Microsoft — user and admin consent](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/user-admin-consent-overview))

---

## Step 1 — Register the app

[entra.microsoft.com](https://entra.microsoft.com) → **Applications** → **App registrations** →
**New registration**.

| Field | Value |
| --- | --- |
| Name | `rNotice` |
| Supported account types | **Accounts in this organizational directory only (single tenant)** |
| Redirect URI | Leave blank for now — added in step 2 |

**Register**. Copy the **Application (client) ID** and the **Directory (tenant) ID** off the
overview page; you need both.

> If **New registration** is greyed out or errors, the tenant has `Users can register
> applications = No`. Stop here — you need IT, and `IT-REQUEST.md` is the ask. Mention that you
> also need either the registration created for you or the **Application Developer** role.

## Step 2 — Add redirect URIs

**Authentication** → **Add a platform** → **Mobile and desktop applications**.

Add both:

```
ms-appx-web://Microsoft.AAD.BrokerPlugin/{your-client-id}
http://localhost
```

The first is the Windows broker (WAM) URI the real app uses. The second is only for the consent
test in step 4 — you can delete it afterwards.

Then under **Advanced settings**, set **Allow public client flows** to **Yes**.

## Step 3 — Add the permissions

**API permissions** → **Add a permission** → **Microsoft Graph** → **Delegated permissions**.

Add all five:

```
Files.ReadWrite
Calendars.ReadWrite
MailboxSettings.Read
Mail.Read
Chat.Read
```

The list will now show a **Status** column. Anything reading *"Not granted"* with a warning icon
is normal at this stage — it means nobody has consented yet, not that you've done it wrong.

> If a permission shows **"Admin consent required: Yes"** in its row, that one is settled — no
> amount of trying will let you self-consent to it.

## Step 4 — Find out if you can consent

Do **not** click "Grant admin consent" — you almost certainly can't, and it tells you nothing
about user consent.

Instead, trigger a real consent prompt in a browser. Paste this into the address bar, replacing
`{client-id}`, and sign in as yourself:

```
https://login.microsoftonline.com/bravurasecurity.com/oauth2/v2.0/authorize
  ?client_id={client-id}
  &response_type=code
  &redirect_uri=http%3A%2F%2Flocalhost
  &response_mode=query
  &scope=offline_access%20Files.ReadWrite%20Calendars.ReadWrite%20MailboxSettings.Read
  &state=test1
```

(All one line, no spaces or line breaks.)

### Test in two batches — this is the point of the exercise

The URL above asks for **only the three low-sensitivity scopes**. Run it first. Then run it again
with `%20Mail.Read%20Chat.Read` appended to the `scope` parameter.

Running them separately tells you *where the boundary is*, not just that there is one. "I can
self-consent to Files and Calendars but Mail.Read needs you" is a far better conversation with
IT than "I need five permissions approved".

### Reading the result

| What you see | What it means |
| --- | --- |
| A consent screen listing the permissions, with **Accept** | **You can self-consent.** Click Accept. The browser lands on a `localhost` page that fails to load — that's expected and fine, the consent is recorded. |
| **"Need admin approval"** | Tenant policy blocks user consent for these scopes. See below. |
| **"Need admin approval"** *with a request box* | Best outcome short of self-consent — the admin consent request workflow is enabled. Fill in the justification and it routes to the right people automatically, with the app and scopes already attached. |
| `AADSTS650057` or an invalid-resource error | A scope name is wrong, or the redirect URI doesn't match what you registered. |
| `AADSTS900971` / redirect mismatch | `http://localhost` isn't registered. Back to step 2. |

Check what actually got consented at **Enterprise applications** → `rNotice` → **Permissions**.

## Step 5 — Clean up

Once you know the answer, remove `http://localhost` from the redirect URIs. The real app only
uses the broker URI.

---

## If you're blocked

Send `IT-REQUEST.md`. If you got as far as a consent test, add a line at the top saying which
scopes went through and which didn't — it turns an open-ended permissions request into a narrow
one.

If the admin consent request workflow in step 4 appeared, use it **as well as** the document. The
workflow gives IT the technical detail already attached; the document gives them the reasoning
and the staged fallback.

### Two ways IT can unblock this — ask for the first

**A. IT creates the registration and grants consent.** One action, nothing standing granted to
you. **This is the smaller ask and the one to lead with.**

**B. IT grants you the Application Developer role.** This built-in role lets you create app
registrations *"when the Users can register applications setting is set to No"*, makes you owner
of what you create, and — the part worth knowing — *"grants permission to consent on one's own
behalf when the Users can consent to apps accessing company data on their behalf setting is set
to No"*.
([role reference](https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/permissions-reference))

So B could unblock the whole thing in one assignment: create the app, self-consent to whatever
is user-consentable. It still **cannot** grant admin consent for privileged Graph permissions —
those need a Privileged Role Administrator — so if `Mail.Read` is classified that way in this
tenant, you'd still be back at IT for that one.

**Expect pushback on B, and raise it honestly.** Microsoft labels Application Developer a
privileged role. A third-party tracker also reports Microsoft quietly added 15 directory
permissions to it on 21 August 2026, taking it from 3 actions to 18, with Microsoft Learn still
listing only the original three — unverified, but an identity team at a PAM vendor will likely
find it, and it goes better coming from you.
([report](https://ourcloudnetwork.com/microsoft-quietly-expands-the-entra-application-developer-role/))

For this project, **ask for A.** Ask for B separately if you expect to build more tools and want
to stop going back to IT each time — on its own merits, not bundled into this request.

## What this gets you if it works

The three low-sensitivity scopes alone unblock **build steps 1–4** — the Graph spike, the item
model, and a working app on the workbook and calendar. That's a genuinely useful tool that never
touches your mailbox, and it can be built while the mail and Teams scopes are still in a queue
somewhere.

---

## A note on doing this yourself

Registering an app in your own tenant and consenting to delegated permissions for your own
account is a normal, supported, audited action — it's the same path any developer takes, and
every grant is visible to IT in Enterprise applications.

That said: at a company that sells privileged access management, an app registration that reads
a mailbox is exactly the kind of thing identity teams want to know exists. If you can
self-consent, **tell IT you did** rather than leaving them to find it. `IT-REQUEST.md` works
just as well as a notification as it does as a request, and the conversation goes better when
you started it.
