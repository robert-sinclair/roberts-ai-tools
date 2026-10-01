# SaaS Process Documentation Standard

**Document owner:** Robert Sinclair, SaaS Team
**Status:** Draft for team review
**Version:** 0.1 — 2026-10-01
**Location:** GRP SaaS (Bravura Security)-Team > Documents > Team > Process Docs
**Next review:** 2027-04-01

---

## 1. Purpose

This document defines how a procedure becomes a written process doc in the SaaS
Process Docs library. It exists so that recurring work is executed the same way
by any engineer, and so that the answer to "who asks for this and who does it"
is written down rather than carried in someone's head.

This is the one process doc you read before writing any other process doc.

## 2. Scope

### Write a process doc when the work is:

- Performed more than once, by more than one person.
- Client-impacting, or touches Production.
- Required for audit, contractual, or security evidence.
- Currently known by only one engineer (single point of knowledge).
- A recovery or rollback path that will be needed under pressure.

### Do not write a process doc for:

- A one-time migration or project task. Use the deploy document or the ticket.
- Work fully automated by Terraform or Ansible with no human decision points.
  Document the *invocation* and the *decision to invoke*, not the steps the
  automation performs.
- Client-specific configuration. That belongs in the client's own
  documentation, not the process library.

## 3. Roles

Every process doc names these four roles. Name **roles**, not people, except
for Document Owner.

| Role | Definition |
|---|---|
| **Requester** | Who is entitled to ask for this work, and through which channel. If anyone can ask, say so. If only a named role can ask, say that — it is an access control. |
| **Performer** | Who executes the steps. The role that must hold the prerequisites and access listed in the doc. |
| **Owning team** | The team accountable for the outcome. One team only. If you cannot name one, the process is not ready to document — resolve the ownership question first. |
| **Document owner** | The named individual who keeps the doc current and signs off changes. Not a team. A doc with no named owner is unmaintained. |

## 4. Team routing

Use these teams. They match the responsibility split in `SaaS engagements
RACI.xlsx` and `SAAS Environment Management.docx`.

| Team | Owns |
|---|---|
| **SaaS Team** | Platform infrastructure, all environments, Test and Production deploys, patching, monitoring, backup and restore, certificates, Production superuser access. |
| **Project / Implementation Team** | Solution configuration, components, local and Dev environment work, preparing the stage/master branch and the deploy document. |
| **Engineering** | Product builds, product defect resolution, connector packs. |
| **Customer Success / CSM / AM** | Client communication, commercial scope, client approvals, change-control alignment. |
| **Support** | First-line intake and triage, Zendesk ownership, escalation into SaaS or Engineering. |
| **Client / Partner** | Their own change control, their validation sign-off, their on-premise dependencies. |

Record intake channels explicitly — Zendesk ticket, Jira SAASK, Salesforce
case, PagerDuty or OpsGenie alert, deploy ticket, or Teams request. "Someone
messaged me" is not an intake channel and should be corrected, not documented.

## 5. Workflow

### Step 1 — Trigger

Someone performs work that meets the Scope test in Section 2, or a request
arrives with no documented process behind it.

### Step 2 — Classify ownership

Answer one question: **does the SaaS team perform the work?**

| Answer | Action |
|---|---|
| **Yes — SaaS performs it** | SaaS-owned. Continue to Step 3. The doc lives in this library. |
| **No — another team performs it** | Not SaaS-owned. Do not write their procedure. Write a one-page handoff record instead: the trigger, which team to route to, the intake channel, and what SaaS must supply or be told. Link to their doc if one exists; if it does not, raise that with the owning team. |
| **Shared** | SaaS-owned for the SaaS portion only. Document the SaaS steps in full and name every handoff point explicitly: what SaaS hands over, to whom, and what SaaS needs back before continuing. |

### Step 3 — Search for an existing doc

Before writing anything, check the library:

1. Search the Process Docs folder by keyword, including likely synonyms
   (`upgrade` / `patch` / `deploy`; `restore` / `recover` / `revert`).
2. Check for a `WIP -` prefixed version. The library carries several.
3. Check the sub-folders: `Bravura Monitor`, `Bravura OneAuth`, `ELK`,
   `References`, `SAFE`.

| Finding | Action |
|---|---|
| No doc exists | Create one from the template. |
| A doc exists and is accurate | Do not create a second doc. Add a step or a note to the existing doc and bump its version. |
| A doc exists and is stale or WIP | Update and finish it. Do not fork a parallel copy. Finishing a WIP doc is preferred over creating a new one. |
| Several overlapping docs exist | Consolidate into one and mark the others `Deprecated - see <doc name>`. Do not silently delete; other docs and tickets may link to them. |

### Step 4 — Write it

Use `Process-Doc-Template.md` (or the `.docx` equivalent in the library).
Complete the header block in full. "TBD" in the Requester, Performer, or
Owning team fields means the doc is not ready to publish.

Write it while the work is fresh — ideally during execution, capturing commands
and screenshots as you go.

### Step 5 — Validate

Someone who did not write the doc follows it end to end on a non-Production
environment and confirms it works as written. Record who validated it and when
in the header.

For a recovery or rollback process, this step is not optional. An unvalidated
recovery doc is worse than none, because it will be trusted during an incident.

### Step 6 — Publish and review

1. Save to Process Docs using the naming convention in Section 7.
2. Set status to `Approved`.
3. Set the next review date — 12 months, or 6 months if the process touches
   Production or security controls.
4. Announce it in the SaaS Teams channel so the team knows it exists.

### Step 7 — Keep it current

- The Document Owner reviews on the review date and either re-dates it or
  retires it.
- Anyone who finds the doc wrong fixes it or tells the owner. Following a
  broken doc without reporting it is the failure mode this standard exists to
  prevent.
- When a process is retired, mark it `Deprecated` with the date and reason.
  Do not leave it looking current.

## 6. Standalone requirement

**Every process doc must be executable by a trained engineer with no AI tooling,
no internet search, and no access to the person who wrote it.**

AI assistance is fine for *writing* the doc. It must not be a dependency for
*following* it.

Concretely:

- **Give the actual commands.** Full syntax, with placeholders clearly marked
  (`[client_name]`, `[instance_name]`). Never "generate the command for the
  environment" or "ask the assistant for the syntax".
- **No external reasoning steps.** If a step requires judgement, state the
  decision criteria in the doc. "Determine whether the node is healthy" is not
  a step; the health check and its pass/fail threshold is.
- **Screenshots for UI work**, with the control named in text as well. UIs
  change; the text survives a re-skin better than the image.
- **Links support, never carry, a step.** If a step depends on another document,
  name the document, the section, and the specific step — so a reader with the
  file and no network still knows what to do.
- **Prerequisites are self-contained.** List required access, accounts, VPN,
  tooling, and versions up front. The reader should know before step 1 whether
  they can finish.
- **Validation and rollback live in the doc**, not in a ticket or someone's
  memory.
- **Print test:** if the doc were printed and handed to a new engineer, could
  they do the work? If not, it is not standalone.

## 7. Naming and status conventions

**Filename:** `<Subject> - <Action>.docx`

Lead with the subject so related docs sort together.

- Good: `RDS - Migrate DB to RDS.docx`, `Certificates - Renew RDGW.docx`
- Avoid: `How to do the thing.docx`, `Process_ Restoring a Snapshot in AWS.docx`

**File format:**

| Artifact | Format | Why |
|---|---|---|
| A process doc | `.docx` | It is read by people, often printed or opened offline, and needs screenshots. It is what the library already uses. |
| The library index | `.md` | It is read by people *and* by AI tooling. Markdown parses cleanly, diffs properly, and carries machine-readable frontmatter. |
| A checklist with per-run columns | `.xlsx` | Matches the existing minor-upgrade checklist pattern. |

Do not publish a process doc as Markdown. Do not publish the index as `.docx` —
it is generated, and a binary format breaks both the rebuild and the parsing.

**Status, recorded in the header — not the filename:**

| Status | Meaning |
|---|---|
| `Draft` | Being written. Not safe to follow. |
| `WIP` | Partially complete, usable with care. Must name what is missing. |
| `Approved` | Validated per Step 5. Safe to follow. |
| `Deprecated` | Superseded or retired. States the replacement or the reason. |

Keeping status in the header rather than the filename means promoting a doc
does not break links to it.

## 8. The library index

`SaaS-Process-Docs-Index.md` lists every file in the library, what can be
determined about it from the outside, and which subject each covers. It is
maintained by hand; there is no generator.

Its first line is the timestamp of the last indexing run. That timestamp is a
contract: anything added or modified in SharePoint after it is not in the index.
Anyone — person or AI tool — consulting the index to answer "do we have a process
for X?" must check for files newer than that timestamp before answering, or they
will report a process as undocumented when it was written last week.

Refresh the index after any batch of changes to the library, and at minimum
whenever a new process doc is published. Refreshing means re-reading the folder
and updating the tables — never moving the timestamp on its own, since it only
means anything if a real read sits behind it.

The index does not state whether a document is correct. It reports when a file was
last saved, which is not the same thing — saving a file in Word without editing it
moves that date. Only the Verified field in a doc's own header speaks to accuracy.

## 9. Exceptions

A process doc may skip Step 5 validation only when the process cannot be
rehearsed outside Production. Record the exception and the reason in the header,
and have a second engineer review the steps instead.

Nothing in this standard permits publishing a doc with no named Requester,
Performer, Owning team, or Document Owner.
