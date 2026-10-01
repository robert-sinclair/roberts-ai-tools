# SaaS Process Docs — Index

**Covers:** `GRP SaaS (Bravura Security)-Team > Documents > Team > Process Docs`, including sub-folders
**Compiled:** 2026-10-01
**Owner:** Robert Sinclair, SaaS Team
**Method:** Assembled with Claude Code from a read-only listing and search through the Microsoft 365 connector. Nothing in the library was changed.
**Status:** Draft for team review

---

## How to read this

This index lists what is in the library and what can be determined about each file from
the outside — name, location, modified date, and whichever first lines the search index
returned. It is a map, not a verdict.

**Owner** and **Verified** are deliberately blank. They cannot be derived from file
metadata; a person has to supply them. Filling those two columns is the work that turns
this index into something the team can rely on.

**Modified dates are weak evidence.** Opening a document in Word and saving it bumps the
date without changing a word. `SAAS Environment Management.docx` is the clearest example:
it last saved in 2025 but still says "revision July 2021" in its own text. Treat "current"
below as "recently touched", not "known good".

Dates were harvested through content search, which did not return every file. 32 of
124 rows show no date rather than a guessed one.

**Age** is mechanical: `stale` means last modified before 2024-10-01 (over two
years ago), `current` means after.

---

## Summary

| | Count |
|---|---|
| Items indexed | 124 |
| Modified within 2 years | 47 |
| Older than 2 years | 45 |
| No date captured | 32 |
| Marked WIP | 13 |
| Templates | 5 |
| Completed execution records | 9 |
| Client-specific one-offs | 7 |
| Policy and reference | 5 |
| Superseded or housekeeping | 5 |
| In sub-folders | 21 |

---

## What stands out

**13 documents are marked WIP, and five of them are recovery or access-control procedures.**
Appstate stuck RBAC recovery, instance snapshot/revert with RDS, client superuser access
creation, client decommissioning, and the on-call OpsGenie runbook. These get reached for
under pressure, by whoever is on call, often at the worst possible moment. WIP is the
riskiest state for exactly these docs, because someone will follow one during an incident
and find the gap then.

**Four documents describe environment and release management, and they disagree.**
`SAAS Environment Management`, `WIP - SaaS Environment Management and Release Management`,
`SaaS deployment processes`, and `SaaS Alternative Management Pathways`. The first two are
near-duplicates; the WIP copy has empty Test and Prod headings under its deploy-process
section and an unresolved note about expert-services tracking. A new engineer has no way to
tell which to follow.

**Three copies of the Superuser Access policy.** A `.docx` marked wip, a PDF dated
2025-March-14, and an undated PDF from 2025-03-06. This one goes to clients, so the
"which is current" question has a customer-facing answer.

**Two templates share one body.** `Docusign Deploy Document Template.docx` and
`Release Notes Template - docusign_release_YYYYMMDD.docx` both open with the same
release-notes template text. One of the two filenames is lying about its contents.

**The two VM configuration docs point at different source repositories.** The Linux one
points at Bitbucket; the Windows one points at `gitlab.hitachi-id.com`. Both describe the
same automation step. At least one is out of date, and the GitLab host is pre-Bravura
branding.

**Nine files are completed execution records, not procedures.** Six dated minor-upgrade
checklists for UOregon and Upfield, two DocuSign release records, and a Vericast go-live.
Worth keeping as evidence; they just make search noisier while they sit beside the
procedures.

**Some docs already capture roles — informally.** `Bravura Monitor - Setting up a New
Instance` opens with a request form capturing Internal Owner/Trustee and Customer
Owner/Trustee. `Make Client DB backups Using Ansible` uses WHAT / WHO / HOW headings.
`Create Monthly Training VMs` names the requesting team and the performing team in its
first two sentences. `Deployment document template - SaaS` has "Changed requested by".
The instinct is already in the library; it is just not consistent.

**Two docs name a single person as the dependency.** `Demo environment - cheat sheet` says
"Sole surviving author: JohnN" in its own text. `Training - SaaS env - process` routes
requests through a named individual rather than a role.

**Housekeeping.** A folder named `delete these`, an `Untitled spreadsheet.xlsx`, a
`Document.docx` under Bravura Monitor/Watchers, one doc with a trailing space in its
filename, and three filenames with typos (`procress`, `trakcing`, `effeincy`). A single
292 MB training video accounts for most of the library's total size.

---

## Suggested next step

Take the 13 WIP rows first and decide, for each, one of three things: finish it,
demote it to a named sub-folder for drafts, or delete it. That is a short meeting, it
addresses the highest-risk items, and it needs no new process to carry out.

Owner and Verified can then be filled in a pass over the ~59 live procedures.

---

## Processes and runbooks (59)

| Document | Modified | Age | Owner | Verified | Notes |
|---|---|---|---|---|---|
| Add database replication and known issues.docx | 2024-07-04 | stale | | | Body says "To be Completed: Update this part with screenshots" — incomplete |
| Add saas Admin's IP to whitelist.docx | 2021-06-02 | stale | | |  |
| AMI Training Regen Guide.docx | 2026-08-24 | current | | |  |
| AWS SSO from command line.docx | 2025-04-21 | current | | |  |
| Babelfish-AD-Group-Access-Runbook.docx | 2026-10-01 | current | | | Newest doc. Carries an author byline and date in the body — closest thing to a header block |
| BC_Kubernetes_Troubleshooting_Runbook.docx | 2026-09-09 | current | | |  |
| BM - Log analysis in SaaS Environments with Bravura Monitor.docx | 2025-04-21 | current | | |  |
| Bravura Cloud First Sign In.docx | 2025-05-06 | current | | |  |
| BSF - Software patching process.docx | 2024-09-18 | stale | | |  |
| Cloud - Creating alerts and examples.docx | — | not captured | | |  |
| Cloud - Using BCloud.docx | — | not captured | | |  |
| Configuring a Linux VM for SaaS Environment.docx | 2026-06-09 | current | | | Points at bitbucket.org/bravura-security/saas-automation |
| Configuring a Windows VM for SaaS environment.docx | 2025-07-09 | current | | | Points at gitlab.hitachi-id.com — its Linux twin points at Bitbucket. One of the two is wrong |
| Configuring mobproxy in the SaaS environment.docx | 2021-01-19 | stale | | |  |
| Connecting to SaaS environment.docx | 2026-07-07 | current | | |  |
| Create dynamic proxy for hitachi-id.com.docx | — | not captured | | |  |
| Create Monthly Training VMs.docx | 2026-08-25 | current | | | States requester (Training team) and performer (SaaS) in the body |
| Creating a windows service.docx | — | not captured | | |  |
| crowdstrike-upgrade-procedure.docx | 2026-03-12 | current | | | Has a "Last Updated" line and names applicable clusters |
| Disconnecting logged in users before idmsuite upgrades.docx | — | not captured | | |  |
| ELK - Users - Create Readonly accounts.docx | 2021-09-16 | stale | | |  |
| Email - Add Domain to AWS SES Domains.docx | 2026-03-25 | current | | |  |
| Fixing a production issue when UAT and the git master has undeployed code.docx | 2021-04-14 | stale | | |  |
| Guide - Enable Client IP Forwarding & IIS Log Delivery for BSF Behind ALB.docx | 2026-05-19 | current | | |  |
| How to access MGMT1 when VPN is not working.docx | 2025-08-29 | current | | | RECOVERY — needed when normal access is down |
| How to backup and restore reports.docx | — | not captured | | |  |
| How to retain Sesmon_ SMON _ Session monitoring data when rebuilding privilege instances.docx | 2024-04-03 | stale | | |  |
| How to use BCP utility - When you have to backup and restore a big data set.docx | — | not captured | | |  |
| Idtrack threshold validation error.docx | 2025-10-17 | current | | | Overlaps "WIP - IDTrack Threshold violation process" |
| Ignore unwanted Handshake errors for AWS loadbalancer healthchecks.docx | — | not captured | | |  |
| Long Run Script as Scheduled Task.docx | 2026-06-22 | current | | | Depends on References/LongRunScheduleTask.xml. Body hardcodes a personal admin account |
| MailEnable - Fix custom header failure cause by install.docx | 2026-09-16 | current | | |  |
| Mailenable Domain whitelisting.docx | — | not captured | | |  |
| MailEnable.docx | — | not captured | | |  |
| Make Client DB backups Using Ansible.docx | 2025-02-13 | current | | | Uses WHAT / WHO / HOW headings — the closest existing doc to a roles header |
| MobProxy Troubleshooting doc.docx | 2024-05-09 | stale | | | Content is client-specific (Konica) despite the generic title |
| Portal UI psadmin update fix.docx | — | not captured | | |  |
| Procedure for sanitizing client data.docx | — | not captured | | | Likely audit-relevant — worth confirming it is current |
| Process_ Restoring a Snapshot in AWS.docx | 2024-09-04 | stale | | | RECOVERY |
| RDS - Migrate DB to RDS (No App Node Rebuild).docx | 2025-03-26 | current | | | Overlaps "RDS Migration steps" |
| RDS Instance resize and parameter group change.docx | — | not captured | | |  |
| RDS Migration steps.docx | 2025-03-18 | current | | | Overlaps the above. Body opens mid-word ("pipRebuilding") |
| ReCaptcha.docx | — | not captured | | |  |
| Recover an ec2 when we lose RDP access.docx | 2024-04-19 | stale | | | RECOVERY |
| Removing SQL Express after RDS Migration.docx | 2023-05-26 | stale | | |  |
| Renew RDGW certificates.docx | 2024-04-29 | stale | | | Cert expiry work — staleness here has a deadline attached |
| Renew Self signed certificates.docx | 2021-10-04 | stale | | | Cert expiry work |
| RTMS Real Time Monitoring Server_ Config + PagerDuty + ZD integration.docx | 2022-08-09 | stale | | | Points at a personal repo on gitlab.hitachi-id.com |
| SaaS Alternative Management Pathways.docx | 2025-11-18 | current | | |  |
| SaaS deployment processes.docx | 2025-07-16 | current | | | One of four overlapping environment/release docs |
| SAAS Domain - Reset a users password.docx | 2025-10-10 | current | | | Names its intake channel (Jira service desk portal 5) |
| SAAS Environment Management.docx | 2025-07-15 | current | | | Body says "revision July 2021" — the 2025 modified date is misleading |
| SaaS projects - collaboration with HIDS project teams.docx | 2021-01-28 | stale | | | Unfinished: "<Add a flow chart>", x/y/z placeholders, pasted chat transcript, Google Drive links |
| SES - Convert Email IAM user secret key to region specific credentials for email.docx | 2026-03-25 | current | | |  |
| SES Bounces rate commands and processes.docx | — | not captured | | |  |
| Training - SaaS env - process.docx | 2024-09-24 | stale | | | Names an individual as the requester — the requester field already exists informally |
| Training environments trakcing.docx | 2022-04-05 | stale | | | Typo in filename. Links to a Google Sites policy page |
| Update maintenance page during deployments or outage.docx | — | not captured | | |  |
| Upgrade Scripts.docx | — | not captured | | |  |

---

## Marked WIP (13)

| Document | Modified | Age | Owner | Verified | Notes |
|---|---|---|---|---|---|
| Appstate Stuck RBAC recovery - WIP.docx | — | not captured | | | RECOVERY — WIP |
| Client SU Access Creation - WIP.docx | 2026-03-05 | current | | | Access control — WIP |
| Decommissioning SaaS Clients - WIP.docx | 2025-09-25 | current | | | Body warns "Decommissioning is not easily reversed" — WIP |
| Deploying Minor upgrades using ansible - WIP.docx | 2026-07-13 | current | | |  |
| How to setup BC-Alloy agent - WIP.docx | 2026-09-14 | current | | | Titled as a 2024.5.0 upgrade guide — title and content disagree |
| On Call Ops Genie Doc - WIP.docx | 2026-05-28 | current | | | On-call paging — WIP |
| WIP - Best practices for SaaS Projects.docx | 2025-10-28 | current | | |  |
| WIP - Create and Deploy SaaS Client Nodes.docx | 2025-09-25 | current | | | Terraform + Ansible client build — WIP |
| WIP - IDTrack Threshold violation process - SaaS MA process.docx | 2026-01-01 | current | | | Overlaps "Idtrack threshold validation error" |
| WIP - SaaS Environment Management and Release Management.docx | 2025-07-16 | current | | | Near-duplicate of "SAAS Environment Management". Has empty Test/Prod headings and an open TODO |
| WIP_ Instance snapshot_revert process with RDS backend.docx | 2026-09-07 | current | | | RECOVERY — WIP |
| WIP_ Profiler for effeincy tracking.docx | — | not captured | | | Typo in filename |
| WIP_ Semi-Automatic Patching via AWS Systems Manager.docx | — | not captured | | |  |

---

## Templates (5)

| Document | Modified | Age | Owner | Verified | Notes |
|---|---|---|---|---|---|
| BC-Alloy-Agent-Import-Template.json | 2025-11-25 | current | | | Config artifact used by the BC-Alloy doc |
| Deployment document template - SaaS.docx | 2024-11-12 | current | | | Already has a "Changed requested by" field |
| Docusign Deploy Document Template.docx | 2024-09-26 | stale | | | Body is the release-notes template, not a deploy document — same content as the row below |
| Minor upgrade checklist - template.xlsx | 2023-09-07 | stale | | | Has Primary / Secondary #1 / Secondary #2 columns — a performer field in spreadsheet form |
| Release Notes Template - docusign_release_YYYYMMDD.docx | 2024-04-11 | stale | | | Same body as the row above. One of the two names is wrong |

---

## Completed execution records (9)

Records of work already done. Evidence, not procedure.

| Document | Modified | Age | Owner | Verified | Notes |
|---|---|---|---|---|---|
| Bundle only Release handoff example.docx | 2025-04-03 | current | | | A 2022 DocuSign release kept as an example |
| docusign_release_20210817 - V12 Upgrade.docx | 2025-05-13 | current | | | A 2021 release record |
| Minor upgrade checklist - Uoregon 2023-04-05 12.2.0.29390.xlsx | 2023-04-10 | stale | | |  |
| Minor upgrade checklist - UOregon 2023-04-13.xlsx | 2023-04-14 | stale | | |  |
| Minor upgrade checklist - UOregon 2023-05.12.xlsx | 2023-05-15 | stale | | |  |
| Minor upgrade checklist - Upfield - 2024-01-18.xlsx | 2024-01-19 | stale | | |  |
| Minor upgrade checklist - Upfield - 2024-02-15.xlsx | 2024-02-16 | stale | | |  |
| Minor upgrade checklist - Upfield - 2024-08-01.xlsx | 2024-08-01 | stale | | |  |
| Vericast last minute go live .docx | — | not captured | | | Trailing space in the filename |

---

## Client-specific and one-off troubleshooting (7)

Tied to one client or one incident. Useful history; not general procedure.

| Document | Modified | Age | Owner | Verified | Notes |
|---|---|---|---|---|---|
| Connecting to Citizens bank.docx | — | not captured | | |  |
| How to fix BCBSNC NLB Target healthcheck failure.docx | — | not captured | | |  |
| ONFS - MSSQL Standard to Express migration.docx | 2020-12-24 | stale | | | Oldest document in the library |
| Test ONFS saml.docx | — | not captured | | |  |
| Unable to connect to the AWS Workspaces in AWS SaaS test.docx | 2022-07-22 | stale | | | Hardcodes a specific AWS directory ID |
| Workaround - Reinstalling Components in UoO 12.2.0 - issue with ExtDB.docx | — | not captured | | | Tied to a version that is several releases old |
| ZD-12517_ Workaround to fix saml authentication for Mob proxy.docx | 2022-10-17 | stale | | | Named after a Zendesk ticket |

---

## Policy and reference (5)

| Document | Modified | Age | Owner | Verified | Notes |
|---|---|---|---|---|---|
| Demo environment - cheat sheet.docx | 2022-08-19 | stale | | | States "Sole surviving author: JohnN" — the bus factor is written into the doc |
| SaaS Client Superuser Access Client Policy - wip.docx | 2025-03-18 | current | | | The editable source, marked wip |
| SaaS Client Superuser Access Client Policy 2025-March-14.pdf | 2025-03-18 | current | | | Dated version |
| SaaS Client Superuser Access Client Policy.pdf | 2025-03-06 | current | | | Three versions of this policy exist. Nothing on the filenames says which is current |
| SaaS engagements RACI.xlsx | — | not captured | | | The authority for the team split. Letters only, no team labels — hard to read cold |

---

## Superseded, media and housekeeping (5)

| Document | Modified | Age | Owner | Verified | Notes |
|---|---|---|---|---|---|
| Ansible upgrade procress - Video.mp4 | 2024-09-19 | stale | | | 57.9 MB. Typo in filename |
| delete these/ (folder, 387 KB) | — | not captured | | | Folder literally named "delete these" |
| Old - Bravura Monitor Quick start guide - Informal.docx | 2024-07-05 | stale | | | Superseded by "BM - Log analysis in SaaS Environments" |
| Untitled spreadsheet.xlsx | — | not captured | | | Unnamed |
| UpfieldProductionDeployment.mp4 | 2020-12-15 | stale | | | 35.0 MB |

---

## Sub-folders (21)

| Document | Folder | Modified | Age | Owner | Verified | Notes |
|---|---|---|---|---|---|---|
| Bravura Monitor - ECE Setup.docx | Bravura Monitor | 2023-02-23 | stale | | | Says it is automated in Terraform up to step 4 |
| Bravura Monitor - Saas changes after creation.docx | Bravura Monitor | 2024-05-15 | stale | | |  |
| Bravura Monitor - Setting up a New Instance.docx | Bravura Monitor | 2024-06-20 | stale | | | Opens with a Request form capturing Internal Owner/Trustee and Customer Owner/Trustee — a requester block already exists here |
| Bravura Monitor Backup locations.xlsx | Bravura Monitor | — | not captured | | |  |
| Bravura Safe Onboarding Checklist.xlsx | SAFE | 2025-04-25 | current | | | Contains a client tenant list |
| Change 2FA for SAFE users to Email.docx | SAFE | 2025-02-28 | current | | |  |
| create_index_template.txt | ELK | 2021-03-09 | stale | | |  |
| Document.docx | Bravura Monitor/Watchers | 2024-08-23 | stale | | | Filename is "Document.docx". Contents are watcher JSON |
| external_filebeat.yml | ELK | 2021-03-09 | stale | | |  |
| Filebeat setup from external networks.docx | ELK | 2021-03-08 | stale | | |  |
| Hids Safe Manual Steps.docx | SAFE | 2024-10-17 | current | | | Titled "Manual things that we did" — working notes, not a procedure |
| HYPR Configuration.docx | SAFE | 2025-06-12 | current | | |  |
| HYPR Configuration.url | Bravura OneAuth | — | not captured | | | A shortcut. Duplicates SAFE/HYPR Configuration.docx |
| Hypr_tenants.xlsx | Bravura OneAuth | — | not captured | | |  |
| KT - How to provision Safe instances - 2024-March-4.mp4 | SAFE | 2024-04-11 | stale | | | 292 MB — roughly 80% of the whole library by size |
| logo.png | Bravura OneAuth | 2023-02-23 | stale | | | Not a process doc |
| LongRunScheduleTask.xml | References | — | not captured | | | Referenced by "Long Run Script as Scheduled Task" |
| metricbeat.yml | ELK | 2021-02-26 | stale | | |  |
| Safe migration between regions.docx | SAFE | 2026-06-12 | current | | | Opens "Findings as of 2025-06-27 / What did I do so far" — working notes |
| Safe_ Create a temporary Bravura Safe bootstrap User.docx | SAFE | 2024-04-11 | stale | | |  |
| system_windows.yml | ELK | 2021-02-25 | stale | | |  |

---

## Limitations

- Compiled from file metadata and the first lines of each document as returned by search.
  Most documents were not opened and read in full, so the Notes column flags what is
  visible from outside, not a content review.
- Modified dates come from SharePoint and reflect the last save, not the last meaningful
  change or the last time anyone confirmed the steps still work.
- 32 rows have no date because content search did not return them. The files exist —
  they are in the folder listing — only the date is missing.
- Categories are judgements based on filename and opening lines. A few will be wrong, in
  particular where a generic title hides client-specific content, as with
  `MobProxy Troubleshooting doc`.
- Nothing in SharePoint was created, modified, moved or deleted to produce this index.

