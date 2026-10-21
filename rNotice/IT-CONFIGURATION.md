# rNotice — Entra configuration steps for IT

Click-by-click. Roughly five minutes.

**Who can do this:** Application Administrator, Cloud Application Administrator, or Global
Administrator. Steps 1–4 need app-management rights; step 5 (consent) needs one of those roles.

**Portal:** [entra.microsoft.com](https://entra.microsoft.com). Menu labels move between portal
versions — if a label differs slightly, the path is still right.

---

## Step 1 — Create the registration

**Identity** → **Applications** → **App registrations** → **New registration**

| Field | Value |
| --- | --- |
| Name | `rNotice` |
| Supported account types | **Accounts in this organizational directory only (Single tenant)** |
| Redirect URI | **Leave blank** |

**Register.**

> The redirect URI is left blank deliberately. It has to contain the client ID, which doesn't
> exist until the app is created. It gets added in step 3. This is Microsoft's own documented
> order.

## Step 2 — Copy the Application (client) ID

From the **Overview** page, copy **Application (client) ID**. That's the one value Robert needs.
It is not a secret — it ships inside the application binary by design.

## Step 3 — Redirect URI and public client flows

**Authentication** → **Add a platform** → **Mobile and desktop applications**

Under **Custom redirect URIs**, enter, with the client ID from step 2 substituted in:

```
ms-appx-web://Microsoft.AAD.BrokerPlugin/<application-client-id>
```

**Configure.**

> Redirect URIs are matched by exact string comparison. A missing slash or a changed character
> breaks sign-in with an unhelpful error. Paste, don't retype.

Then on the same page, under **Advanced settings** → **Allow public client flows**, set *Enable
the following mobile and desktop flows* to **Yes**. **Save.**

> This is what makes it a public client — an app with no credential of its own. It is the
> opposite of the configuration that would let it run unattended.

## Step 4 — Add the API permissions

**API permissions** → **Add a permission** → **Microsoft Graph** → **Delegated permissions**

> **Delegated**, not Application. This is the whole point — see the comparison in
> `IT-REPLY-USER-AUTH.md`. The Application permissions tile should not be opened at all.

Search for and tick each:

```
Calendars.ReadWrite
Chat.Read
Files.ReadWrite
Mail.Read
MailboxSettings.Read
```

**Add permissions.**

`User.Read` will already be present — Entra adds it automatically on creation. Leave it; it only
reads the signed-in user's own profile.

## Can permissions be requested at runtime instead?

Worth answering because it changes how much you commit to today.

**The registration itself: no.** It has to exist before anything runs. The client ID is what a
token request identifies itself as, and Entra won't issue a token to a client it can't name.

**The permissions: technically yes, practically no here.** The v2.0 endpoint supports
*incremental consent* — an app can name scopes in the token request and Microsoft confirms that
*"including the scopes in the scope parameter when requesting an access token makes predefining
them in the application registration unnecessary"*.
([developer guide to consent](https://learn.microsoft.com/en-us/entra/identity-platform/consent-types-developer))

The catch is admin consent specifically. Microsoft: dynamic consent *"presents a challenge for
permissions that require admin consent, because the admin consent experience doesn't know about
those permissions at consent time"*, and when using the admin consent URL *"the permissions must
already be configured with the application registration"*.
([troubleshooting consent](https://learn.microsoft.com/en-us/troubleshoot/entra/entra-id/app-integration/troubleshoot-consent-issues))

So if these scopes need an administrator in this tenant — which the blocked registration
suggests they do — **requesting them at runtime just produces an approval prompt and no access.**
It doesn't save a round trip; it adds one.

### What does work: declare all five, consent in stages

The useful middle path, and it costs you nothing today:

1. **Declare all five** in step 4 below. Declaring is not granting — a declared, unconsented
   permission appears as *Not granted* and does precisely nothing.
2. **Consent to whichever subset you're comfortable with** in step 5. Three of them give a
   working tool.
3. **Consent to the rest later**, if and when you want to, by returning to this same page and
   granting them. **No registration change, no new request, no second conversation about the
   app itself.**

That gives you staged approval with one setup, which is the thing incremental consent was meant
to buy and can't deliver here.

> If you'd rather test whether Robert can self-consent without you, create the registration with
> **no** permissions and let him try — the procedure is in `APP-REGISTRATION.md`, step 4. If
> tenant policy lets him consent for himself, genuine runtime consent becomes available and you
> need not be involved per scope. If it doesn't, fall back to declaring all five as above.

## Step 5 — Grant consent

**Grant admin consent for Bravura Security** → **Yes**

The **Status** column should then read *Granted for Bravura Security* with a green tick on every
row.

## Step 6 — Restrict the app to one user (recommended)

This is optional but it's the strongest control available here, and it costs nothing.

**Identity** → **Applications** → **Enterprise applications** → `rNotice`

- **Properties** → **Assignment required?** → **Yes** → **Save**
- **Users and groups** → **Add user/group** → add **Robert Sinclair** → **Assign**

Now only Robert can sign in to this app. Anyone else who somehow obtained the client ID gets an
error. Assigning a single user needs no extra licensing.
([Microsoft reference](https://learn.microsoft.com/en-us/entra/identity-platform/howto-restrict-your-app-to-a-set-of-users))

> Note: apps that require assignment can't be user-consented, only admin-consented. Since you're
> granting admin consent in step 5 anyway, this changes nothing.

## Step 7 — Do not do these

- **Do not create a client secret or certificate.** *Certificates & secrets* should stay empty.
  There is nothing in this design that uses one, and adding one is the first half of making the
  app able to run on its own.
- **Do not add any Application permission.** Delegated only.
- **Do not add Robert as an Owner** if you want the permission list to stay under your control.
  An owner can edit the app's permissions; he only needs the client ID, not ownership.

---

## Verification — confirm it's configured as described

Worth doing, and it takes a few seconds:

| Check | Expected |
| --- | --- |
| **API permissions** → Type column | `Delegated` on every row |
| **API permissions** → Application permissions section | Empty |
| **Certificates & secrets** | No secrets, no certificates |
| **Authentication** → Allow public client flows | Yes |
| **Overview** → Supported account types | My organization only |
| Later: **Sign-in logs** | Interactive user sign-ins by Robert — not service principal sign-ins |

The last row is the meaningful ongoing one. App-only access would appear under **Service
principal sign-ins**. If that stays empty, the app is only ever running as a signed-in user.

## What to send Robert

Just the **Application (client) ID**. No secret exists, and neither the client ID nor the tenant
ID is a credential, so plain email is fine.

Also worth telling him which permissions you actually granted, if you granted a subset — the
build is staged so it works with `Files.ReadWrite`, `Calendars.ReadWrite` and
`MailboxSettings.Read` alone.

## How to revoke

Any of these, independently of each other and of anything else in the tenant:

- **Enterprise applications** → `rNotice` → **Properties** → **Delete** — removes the service
  principal and all consent
- **Users and groups** → remove Robert's assignment — blocks sign-in, keeps the registration
- **API permissions** → remove a scope and re-consent — narrows it without removing it
- Revoking Robert's sessions invalidates the refresh token immediately

Nothing else in the tenant depends on this app.
