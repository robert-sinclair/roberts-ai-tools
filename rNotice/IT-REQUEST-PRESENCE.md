# Draft email to IT: add Presence.Read

Short follow-up to the original rNotice registration. Send as a reply on that thread so the
context is there.

---

**Subject:** rNotice — one more delegated permission: `Presence.Read`

---

Hi [name],

Thanks again for setting up the rNotice registration — all five permissions are working.

I'd like to add **one more delegated Microsoft Graph permission** to it: **`Presence.Read`**.

**Why:** rNotice alerts me before each meeting and shows which meetings are under way. Right now
it can only tell I've joined a meeting if I click Join inside rNotice; if I join from Teams or
Outlook directly, it still shows the meeting as "not joined". Reading my own Teams presence lets
it see that I'm in a call during the meeting's time and mark it correctly.

**What it reads:** only **my own** presence — `GET /me/presence`. Not anyone else's: that would
be `Presence.Read.All`, which I'm not asking for. Microsoft's description of the permission says
the data includes availability, activity, status note, out-of-office message, time zone and
location. **rNotice only uses `activity`** (for example *InACall* / *InAMeeting*) and stores
none of it — it's checked, used to set the meeting's state, and dropped.

**How often:** only while one of my meetings is in progress, once a minute — not around the
clock.

| | |
|---|---|
| App | `rNotice` (existing registration) |
| Client ID | `2923549f-f444-48f7-8084-c8b7799db2e0` |
| Permission | Microsoft Graph → **Delegated** → `Presence.Read` ("Read user's presence information") |
| Permission ID | `76bc735e-aecd-4a1d-8b4c-2b915deabb79` |
| Admin consent | Not required by Microsoft's classification; needs your grant only because our tenant restricts user consent |

Same principles as before: **delegated only, no `.All`, no application permissions, read only.**
It acts as me and sees only what I can already see.

If you'd rather not add it, no problem — rNotice already works without it, and the only
difference is that meetings I join outside rNotice show as "not joined".

Thanks,
Robert
