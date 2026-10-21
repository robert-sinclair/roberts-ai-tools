# Replies to IT on authentication

Two follow-ups from IT, the second sharper than the first. Send **one reply covering both** — the
second question supersedes the first and answering it answers both.

> *"Is there not a way to use user authentication and it uses that user's info to get all this
> info you require?"*
>
> *"Because I believe app registration will allow your app to run on its own and pull this data,
> where a user should only be allowed to see what their permissions give it."*

**The concern is legitimate and precisely stated — it just describes a different thing than what
was requested.** "App registration" is the container. The permissions inside it come in two
kinds, and they behave in opposite ways:

| | **Application permissions** | **Delegated permissions** |
|---|---|---|
| Also called | App roles | Scopes |
| Runs | Without a signed-in user | Only as a signed-in user |
| Reach | Any data the permission covers, tenant-wide | The **overlap** of the scope and that user's own rights |
| Credential | Secret or certificate required | None — public client |
| Consent | **Admin only** | User or admin |

The second sentence of their question — *a user should only be allowed to see what their
permissions give it* — **is the definition of a delegated permission.** They are asking for the
thing already being requested.

Microsoft's own framing: application permissions *"allow the app to access data on its own"* and
*"carry the most privacy risks as they allow access to data without a signed-in user"*; delegated
permissions let the app *"act on behalf of the signed-in user"*.
([permissions overview](https://learn.microsoft.com/en-us/graph/permissions-overview),
[best practices](https://learn.microsoft.com/en-us/graph/best-practices-graph-permission))

---

## Draft reply

Hi [name],

That's a fair concern and you're right to separate those two things — but I think we're talking
about different halves of the same feature, so let me be precise.

An app registration can hold two kinds of permission, and they behave in opposite ways:

**Application permissions** (Microsoft calls them app roles) are the ones you're describing. The
app gets its own identity and a secret or certificate, runs with no user involved, and reaches
every mailbox the permission covers across the tenant. Microsoft's own guidance says these
"carry the most privacy risks as they allow access to data without a signed-in user." You're
right to push back on those.

**Delegated permissions** (scopes) are what I asked for. The app can only get a token after I
personally sign in, and the access it gets is the overlap of what the app was granted and what
I'm already allowed to see. It cannot reach anything I couldn't open in Outlook myself.

**I've requested five delegated permissions and zero application permissions.** The registration
will have nothing at all under Application permissions, and no client secret or certificate —
which also means there's nothing for it to authenticate as on its own, even in principle.

**And you keep the veto permanently.** Only an administrator can consent to an application
permission. So even if I added one to the manifest tomorrow, it would sit there as "not granted"
and do nothing until you approved it. That's not a promise from me — it's how consent works. I
can't give myself app-only access at any point, now or later.

**You can verify all of this in about ten seconds rather than taking my word for it.** Once the
registration exists:

- **API permissions** → the Type column reads `Delegated` on all five rows, and there are no
  rows under Application permissions
- **Certificates & secrets** → empty
- **Sign-in logs** → entries show as interactive user sign-ins by me, not as service principal
  sign-ins

One thing I want to be straight about rather than have you find it: the app does hold a refresh
token so it isn't prompting me to sign in every hour — exactly the way Outlook on my laptop
does. That token is issued to me, expires, is revocable by you, and gets re-evaluated against
Conditional Access. It runs while I'm signed in to Windows, not independently of me. But it's
not literally a fresh prompt per request, and I'd rather say so than have it look like I glossed
over it.

If it would help, I'm happy for you to create the registration yourself and keep ownership of
it — I only need the client ID. That way the permission list is entirely under your control and
I can't change it.

Robert

---

## If they push further

**"Could you add a secret later?"** A client secret on its own buys nothing. The client
credentials flow only returns a token for *application* permissions, and those need admin
consent. A secret plus delegated-only permissions is still a user-bound app.

**"What about the refresh token — isn't that unattended access?"** It's user-bound, it expires,
it's revocable from Enterprise applications, and revoking the user's sessions kills it. It's the
same mechanism every Microsoft 365 client on a managed device uses. The meaningful distinction
is that there is no credential belonging to the *app*, so there's nothing to steal and replay
independently of the user.

**"Why does it need a registration at all, then?"** Because no OAuth flow issues a token to a
client it can't identify. Tools that appear to need no registration — Graph Explorer, the Graph
PowerShell module, Azure CLI — are themselves registered applications published by Microsoft.
The only way to skip a registration is to borrow one of those client IDs, which would make the
traffic unattributable in the logs and impossible to revoke without breaking the real app.

**Offer them ownership.** If IT creates and owns the registration, Robert can't alter the
permission list at all. It costs nothing and it converts the whole conversation from trust into
configuration.
