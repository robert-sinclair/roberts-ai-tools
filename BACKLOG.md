# Backlog — SaaS process documentation

Open threads from the process-documentation work. Nothing here is scheduled.

---

## 1. Automate the index rebuild as a Claude Routine

**Status:** flagged, not built
**Raised:** 2026-10-01

### The problem

`process-docs/gen_index.py` holds the library inventory as Python literals. Re-running
it refreshes `indexed_through` but does **not** re-read SharePoint. A casual re-run
therefore stamps a fresh timestamp on stale data, which is worse than an honestly old
index: the freshness contract tells every reader that anything older than that timestamp
is covered, and after a blind re-run that claim is false.

Today the inventory is refreshed by hand — a person runs a session, the connector is
read, the data lists in the script are updated, then the script runs.

### What a Routine would do

A scheduled trigger firing a fresh session with the Microsoft 365 connector attached:

1. Read the Process Docs folder recursively for the authoritative file listing.
2. Harvest modified dates. Single-term content searches worked; multi-term queries and
   the `folderName` filter are unreliable and leak across drives.
3. Diff against the committed index: new files, removed files, changed dates.
4. Regenerate the index with a genuine `indexed_through`.
5. Commit to the repo and open a PR, or report the diff if nothing changed.

Monthly is probably right. The library changes slowly — roughly a dozen documents
touched per year, judging by the dates captured in the current index.

### The blocker worth thinking about first

`CLAUDE.md` says nothing may be created or modified in SharePoint without an explicit
yes, per action. An unattended Routine cannot obtain one.

That is not fatal, because the rebuild only needs to **read** SharePoint, which the rule
already exempts. But it constrains where the output lands:

- **Repo only.** The Routine reads SharePoint, writes the index to the repo, opens a PR.
  No SharePoint write, no rule conflict. Anyone wanting the index in SharePoint uploads
  it themselves after review. This is the clean option.
- **SharePoint write.** Needs a narrow, deliberate exception to the rule, scoped to one
  filename in one folder. Only worth doing if people are actually reading the index in
  SharePoint rather than in the repo.

Decide which before building, because it determines whether the rule needs amending.

### Cheaper interim fix

Split the timestamp in two: `data_captured` (updated by hand when the inventory is
re-read) and `generated_at` (set at run time). Have the script fail, or print a loud
warning, when `data_captured` is older than some threshold. Roughly five lines, and it
removes the trap without any automation. Worth doing if the Routine slips.

### Done looks like

- The index is rebuilt on a schedule without anyone remembering to do it.
- `indexed_through` always reflects a real SharePoint read, never a blind re-run.
- A rebuild that finds no changes produces no noise.
- A rebuild that finds changes surfaces them for review before anything is published.

---

## 2. Triage the 13 WIP documents

**Status:** not started

Five are recovery or access-control procedures: Appstate stuck RBAC recovery, instance
snapshot/revert with RDS, client superuser access creation, client decommissioning, and
the on-call OpsGenie runbook. These get followed under pressure.

For each: finish it, move it to a drafts sub-folder, or delete it. Short meeting, highest
risk, needs no new process to carry out.

---

## 3. Fill Owner and Verified across the live procedures

**Status:** not started

Both columns are empty in the index because neither can be derived from metadata. Roughly
60 live procedures. Owner is quick. Verified means someone followed the doc end to end,
which is the slow and valuable part.

---

## 4. Capture dates for the 33 unindexed files

**Status:** not started

Content search did not return these, so the gap check cannot tell whether they have
changed. Opening them also replaces search excerpts with real content notes, which would
make the Notes column worth more than it currently is.

---

## 5. Convert the standard and template to .docx

**Status:** not started

`SaaS-Process-Documentation-Standard.md` and `Process-Doc-Template.md` are drafts in the
repo. Per the format rule they ship as `.docx`, built with the Bravura brand template.
Do this once the content has been reviewed, not before.

---

## 6. Resolve the contradictions the index found

**Status:** not started

- Four overlapping environment/release docs, two of them near-duplicates.
- Three copies of the client Superuser Access policy, no indication which is current.
  This one goes to clients.
- Two DocuSign templates sharing one body; one filename is wrong.
- Linux and Windows VM docs pointing at different source repositories; the GitLab host
  predates the Bravura rebrand.
- `RTMS Real Time Monitoring Server` points at a personal repo.
