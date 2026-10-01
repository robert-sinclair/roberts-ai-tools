#!/usr/bin/env python3
"""Generate the Process Docs index from the inventory captured via the SharePoint connector."""
from datetime import date, datetime, timezone

TODAY = date(2026, 10, 1)
STALE_CUTOFF = date(2024, 10, 1)   # older than 2 years
INDEXED_THROUGH = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
FOLDER_URI = ("file:///b!1aWbwLjUHke3ywyMg6GfPtd8g5c8I4VLnyVqPk_ezCnaE-2HtPeLRJPZ5Di_SVS7"
              "/Team/Process Docs")

# (name, folder, modified or None, note)
PROCESS = [
 ("Add database replication and known issues.docx","/", "2024-07-04","Body says \"To be Completed: Update this part with screenshots\" — incomplete"),
 ("Add saas Admin's IP to whitelist.docx","/", "2021-06-02",""),
 ("AMI Training Regen Guide.docx","/", "2026-08-24",""),
 ("AWS SSO from command line.docx","/", "2025-04-21",""),
 ("Babelfish-AD-Group-Access-Runbook.docx","/", "2026-10-01","Newest doc. Carries an author byline and date in the body — closest thing to a header block"),
 ("BC_Kubernetes_Troubleshooting_Runbook.docx","/", "2026-09-09",""),
 ("BM - Log analysis in SaaS Environments with Bravura Monitor.docx","/", "2025-04-21",""),
 ("Bravura Cloud First Sign In.docx","/", "2025-05-06",""),
 ("BSF - Software patching process.docx","/", "2024-09-18",""),
 ("BCloud - Creating alerts and examples.docx","/", None,""),
 ("BCloud - Using BCloud.docx","/", None,""),
 ("Configuring a Linux VM for SaaS Environment.docx","/", "2026-06-09","Points at bitbucket.org/bravura-security/saas-automation"),
 ("Configuring a Windows VM for SaaS environment.docx","/", "2025-07-09","Points at gitlab.hitachi-id.com — its Linux twin points at Bitbucket. One of the two is wrong"),
 ("Configuring mobproxy in the SaaS environment.docx","/", "2021-01-19",""),
 ("Connecting to SaaS environment.docx","/", "2026-07-07",""),
 ("Create dynamic proxy for hitachi-id.com.docx","/", None,""),
 ("Create Monthly Training VMs.docx","/", "2026-08-25","States requester (Training team) and performer (SaaS) in the body"),
 ("Creating a windows service.docx","/", None,""),
 ("crowdstrike-upgrade-procedure.docx","/", "2026-03-12","Has a \"Last Updated\" line and names applicable clusters"),
 ("Disconnecting logged in users before idmsuite upgrades.docx","/", None,""),
 ("ELK - Users - Create Readonly accounts.docx","/", "2021-09-16",""),
 ("Email - Add Domain to AWS SES Domains.docx","/", "2026-03-25",""),
 ("Fixing a production issue when UAT and the git master has undeployed code.docx","/", "2021-04-14",""),
 ("Guide - Enable Client IP Forwarding & IIS Log Delivery for BSF Behind ALB.docx","/", "2026-05-19",""),
 ("How to access MGMT1 when VPN is not working.docx","/", "2025-08-29","RECOVERY — needed when normal access is down"),
 ("How to backup and restore reports.docx","/", None,""),
 ("How to retain Sesmon_ SMON _ Session monitoring data when rebuilding privilege instances.docx","/", "2024-04-03",""),
 ("How to use BCP utility - When you have to backup and restore a big data set.docx","/", None,""),
 ("Idtrack threshold validation error.docx","/", "2025-10-17","Overlaps \"WIP - IDTrack Threshold violation process\""),
 ("Ignore unwanted Handshake errors for AWS loadbalancer healthchecks.docx","/", None,""),
 ("Long Run Script as Scheduled Task.docx","/", "2026-06-22","Depends on References/LongRunScheduleTask.xml. Body hardcodes a personal admin account"),
 ("MailEnable - Fix custom header failure cause by install.docx","/", "2026-09-16",""),
 ("MailEnable.docx","/", None,""),
 ("Mailenable Domain whitelisting.docx","/", None,""),
 ("Make Client DB backups Using Ansible.docx","/", "2025-02-13","Uses WHAT / WHO / HOW headings — the closest existing doc to a roles header"),
 ("MobProxy Troubleshooting doc.docx","/", "2024-05-09","Content is client-specific (Konica) despite the generic title"),
 ("Pagerduty - Schedule Maintenance Period.docx","/", None,"Suppresses alerting during planned work"),
 ("Portal UI psadmin update fix.docx","/", None,""),
 ("Procedure for sanitizing client data.docx","/", None,"Likely audit-relevant — worth confirming it is current"),
 ("Process_ Restoring a Snapshot in AWS.docx","/", "2024-09-04","RECOVERY"),
 ("RDS - Migrate DB to RDS (No App Node Rebuild).docx","/", "2025-03-26","Overlaps \"RDS Migration steps\""),
 ("RDS Instance resize and parameter group change.docx","/", None,""),
 ("RDS Migration steps.docx","/", "2025-03-18","Overlaps the above. Body opens mid-word (\"pipRebuilding\")"),
 ("ReCaptcha.docx","/", None,""),
 ("Recover an ec2 when we lose RDP access.docx","/", "2024-04-19","RECOVERY"),
 ("Removing SQL Express after RDS Migration.docx","/", "2023-05-26",""),
 ("Renew RDGW certificates.docx","/", "2024-04-29","Cert expiry work — staleness here has a deadline attached"),
 ("Renew Self signed certificates.docx","/", "2021-10-04","Cert expiry work"),
 ("RTMS Real Time Monitoring Server_ Config + PagerDuty + ZD integration.docx","/", "2022-08-09","Points at a personal repo on gitlab.hitachi-id.com"),
 ("SAAS Domain - Reset a users password.docx","/", "2025-10-10","Names its intake channel (Jira service desk portal 5)"),
 ("SaaS Alternative Management Pathways.docx","/", "2025-11-18",""),
 ("SaaS deployment processes.docx","/", "2025-07-16","One of four overlapping environment/release docs"),
 ("SAAS Environment Management.docx","/", "2025-07-15","Body says \"revision July 2021\" — the 2025 modified date is misleading"),
 ("SaaS projects - collaboration with HIDS project teams.docx","/", "2021-01-28","Unfinished: \"<Add a flow chart>\", x/y/z placeholders, pasted chat transcript, Google Drive links"),
 ("SES - Convert Email IAM user secret key to region specific credentials for email.docx","/", "2026-03-25",""),
 ("SES Bounces rate commands and processes.docx","/", None,""),
 ("Training - SaaS env - process.docx","/", "2024-09-24","Names an individual as the requester — the requester field already exists informally"),
 ("Training environments trakcing.docx","/", "2022-04-05","Typo in filename. Links to a Google Sites policy page"),
 ("Update maintenance page during deployments or outage.docx","/", None,""),
 ("Upgrade Scripts.docx","/", None,""),
]

WIP = [
 ("Appstate Stuck RBAC recovery - WIP.docx","/", None,"RECOVERY — WIP"),
 ("Client SU Access Creation - WIP.docx","/", "2026-03-05","Access control — WIP"),
 ("Decommissioning SaaS Clients - WIP.docx","/", "2025-09-25","Body warns \"Decommissioning is not easily reversed\" — WIP"),
 ("Deploying Minor upgrades using ansible - WIP.docx","/", "2026-07-13",""),
 ("How to setup BC-Alloy agent - WIP.docx","/", "2026-09-14","Titled as a 2024.5.0 upgrade guide — title and content disagree"),
 ("On Call Ops Genie Doc - WIP.docx","/", "2026-05-28","On-call paging — WIP"),
 ("WIP - Best practices for SaaS Projects.docx","/", "2025-10-28",""),
 ("WIP - Create and Deploy SaaS Client Nodes.docx","/", "2025-09-25","Terraform + Ansible client build — WIP"),
 ("WIP - IDTrack Threshold violation process - SaaS MA process.docx","/", "2026-01-01","Overlaps \"Idtrack threshold validation error\""),
 ("WIP - SaaS Environment Management and Release Management.docx","/", "2025-07-16","Near-duplicate of \"SAAS Environment Management\". Has empty Test/Prod headings and an open TODO"),
 ("WIP_ Instance snapshot_revert process with RDS backend.docx","/", "2026-09-07","RECOVERY — WIP"),
 ("WIP_ Profiler for effeincy tracking.docx","/", None,"Typo in filename"),
 ("WIP_ Semi-Automatic Patching via AWS Systems Manager.docx","/", None,""),
]

TEMPLATES = [
 ("Deployment document template - SaaS.docx","/", "2024-11-12","Already has a \"Changed requested by\" field"),
 ("Docusign Deploy Document Template.docx","/", "2024-09-26","Body is the release-notes template, not a deploy document — same content as the row below"),
 ("Release Notes Template - docusign_release_YYYYMMDD.docx","/", "2024-04-11","Same body as the row above. One of the two names is wrong"),
 ("Minor upgrade checklist - template.xlsx","/", "2023-09-07","Has Primary / Secondary #1 / Secondary #2 columns — a performer field in spreadsheet form"),
 ("BC-Alloy-Agent-Import-Template.json","/", "2025-11-25","Config artifact used by the BC-Alloy doc"),
]

RECORDS = [
 ("Bundle only Release handoff example.docx","/", "2025-04-03","A 2022 DocuSign release kept as an example"),
 ("docusign_release_20210817 - V12 Upgrade.docx","/", "2025-05-13","A 2021 release record"),
 ("Minor upgrade checklist - Uoregon 2023-04-05 12.2.0.29390.xlsx","/", "2023-04-10",""),
 ("Minor upgrade checklist - UOregon 2023-04-13.xlsx","/", "2023-04-14",""),
 ("Minor upgrade checklist - UOregon 2023-05.12.xlsx","/", "2023-05-15",""),
 ("Minor upgrade checklist - Upfield - 2024-01-18.xlsx","/", "2024-01-19",""),
 ("Minor upgrade checklist - Upfield - 2024-02-15.xlsx","/", "2024-02-16",""),
 ("Minor upgrade checklist - Upfield - 2024-08-01.xlsx","/", "2024-08-01",""),
 ("Vericast last minute go live .docx","/", None,"Trailing space in the filename"),
]

CLIENT = [
 ("Connecting to Citizens bank.docx","/", None,""),
 ("How to fix BCBSNC NLB Target healthcheck failure.docx","/", None,""),
 ("ONFS - MSSQL Standard to Express migration.docx","/", "2020-12-24","Oldest document in the library"),
 ("Test ONFS saml.docx","/", None,""),
 ("Unable to connect to the AWS Workspaces in AWS SaaS test.docx","/", "2022-07-22","Hardcodes a specific AWS directory ID"),
 ("Workaround - Reinstalling Components in UoO 12.2.0 - issue with ExtDB.docx","/", None,"Tied to a version that is several releases old"),
 ("ZD-12517_ Workaround to fix saml authentication for Mob proxy.docx","/", "2022-10-17","Named after a Zendesk ticket"),
]

POLICY = [
 ("SaaS Client Superuser Access Client Policy.pdf","/", "2025-03-06","Three versions of this policy exist. Nothing on the filenames says which is current"),
 ("SaaS Client Superuser Access Client Policy 2025-March-14.pdf","/", "2025-03-18","Dated version"),
 ("SaaS Client Superuser Access Client Policy - wip.docx","/", "2025-03-18","The editable source, marked wip"),
 ("SaaS engagements RACI.xlsx","/", None,"The authority for the team split. Letters only, no team labels — hard to read cold"),
 ("Demo environment - cheat sheet.docx","/", "2022-08-19","States \"Sole surviving author: JohnN\" — the bus factor is written into the doc"),
]

HOUSEKEEPING = [
 ("Old - Bravura Monitor Quick start guide - Informal.docx","/", "2024-07-05","Superseded by \"BM - Log analysis in SaaS Environments\""),
 ("Untitled spreadsheet.xlsx","/", None,"Unnamed"),
 ("delete these/ (folder, 387 KB)","/", None,"Folder literally named \"delete these\""),
 ("Ansible upgrade procress - Video.mp4","/", "2024-09-19","57.9 MB. Typo in filename"),
 ("UpfieldProductionDeployment.mp4","/", "2020-12-15","35.0 MB"),
]

SUBFOLDERS = [
 ("Bravura Monitor - ECE Setup.docx","Bravura Monitor","2023-02-23","Says it is automated in Terraform up to step 4"),
 ("Bravura Monitor - Saas changes after creation.docx","Bravura Monitor","2024-05-15",""),
 ("Bravura Monitor - Setting up a New Instance.docx","Bravura Monitor","2024-06-20","Opens with a Request form capturing Internal Owner/Trustee and Customer Owner/Trustee — a requester block already exists here"),
 ("Bravura Monitor Backup locations.xlsx","Bravura Monitor",None,""),
 ("Document.docx","Bravura Monitor/Watchers","2024-08-23","Filename is \"Document.docx\". Contents are watcher JSON"),
 ("HYPR Configuration.url","Bravura OneAuth",None,"A shortcut. Duplicates SAFE/HYPR Configuration.docx"),
 ("Hypr_tenants.xlsx","Bravura OneAuth",None,""),
 ("logo.png","Bravura OneAuth","2023-02-23","Not a process doc"),
 ("create_index_template.txt","ELK","2021-03-09",""),
 ("external_filebeat.yml","ELK","2021-03-09",""),
 ("Filebeat setup from external networks.docx","ELK","2021-03-08",""),
 ("metricbeat.yml","ELK","2021-02-26",""),
 ("system_windows.yml","ELK","2021-02-25",""),
 ("LongRunScheduleTask.xml","References",None,"Referenced by \"Long Run Script as Scheduled Task\""),
 ("Bravura Safe Onboarding Checklist.xlsx","SAFE","2025-04-25","Contains a client tenant list"),
 ("Change 2FA for SAFE users to Email.docx","SAFE","2025-02-28",""),
 ("Hids Safe Manual Steps.docx","SAFE","2024-10-17","Titled \"Manual things that we did\" — working notes, not a procedure"),
 ("HYPR Configuration.docx","SAFE","2025-06-12",""),
 ("Safe migration between regions.docx","SAFE","2026-06-12","Opens \"Findings as of 2025-06-27 / What did I do so far\" — working notes"),
 ("Safe_ Create a temporary Bravura Safe bootstrap User.docx","SAFE","2024-04-11",""),
 ("KT - How to provision Safe instances - 2024-March-4.mp4","SAFE","2024-04-11","292 MB — roughly 80% of the whole library by size"),
]


# Topic -> documents. This is the lookup that makes the index fast to consult.
TOPICS = [
 ("access, VPN down, MGMT1, bastion, jump host", ["How to access MGMT1 when VPN is not working.docx", "Connecting to SaaS environment.docx", "SaaS Alternative Management Pathways.docx"]),
 ("AWS CLI, SSO, console credentials", ["AWS SSO from command line.docx"]),
 ("certificates, RDGW, self-signed, expiry", ["Renew RDGW certificates.docx", "Renew Self signed certificates.docx"]),
 ("RDS, database migration, SQL Express", ["RDS - Migrate DB to RDS (No App Node Rebuild).docx", "RDS Migration steps.docx", "Removing SQL Express after RDS Migration.docx", "RDS Instance resize and parameter group change.docx"]),
 ("database replication", ["Add database replication and known issues.docx"]),
 ("backup, restore, BCP, reports", ["Make Client DB backups Using Ansible.docx", "How to backup and restore reports.docx", "How to use BCP utility - When you have to backup and restore a big data set.docx"]),
 ("snapshot, revert, rollback, instance recovery", ["Process_ Restoring a Snapshot in AWS.docx", "WIP_ Instance snapshot_revert process with RDS backend.docx", "How to retain Sesmon_ SMON _ Session monitoring data when rebuilding privilege instances.docx"]),
 ("EC2 lost RDP, node unreachable", ["Recover an ec2 when we lose RDP access.docx", "Appstate Stuck RBAC recovery - WIP.docx"]),
 ("patching, minor upgrade, build upgrade", ["BSF - Software patching process.docx", "Deploying Minor upgrades using ansible - WIP.docx", "WIP_ Semi-Automatic Patching via AWS Systems Manager.docx", "Upgrade Scripts.docx", "Disconnecting logged in users before idmsuite upgrades.docx", "Minor upgrade checklist - template.xlsx"]),
 ("CrowdStrike, endpoint agent", ["crowdstrike-upgrade-procedure.docx"]),
 ("deployment, release, code promotion, branches", ["SaaS deployment processes.docx", "SAAS Environment Management.docx", "WIP - SaaS Environment Management and Release Management.docx", "Deployment document template - SaaS.docx", "Fixing a production issue when UAT and the git master has undeployed code.docx"]),
 ("new client, node build, Terraform, Ansible provisioning", ["WIP - Create and Deploy SaaS Client Nodes.docx", "Configuring a Linux VM for SaaS Environment.docx", "Configuring a Windows VM for SaaS environment.docx"]),
 ("client offboarding, decommission", ["Decommissioning SaaS Clients - WIP.docx"]),
 ("superuser access, client_su, admin access policy", ["Client SU Access Creation - WIP.docx", "SaaS Client Superuser Access Client Policy 2025-March-14.pdf", "Add saas Admin's IP to whitelist.docx"]),
 ("password reset, SaaS domain account", ["SAAS Domain - Reset a users password.docx"]),
 ("email, SES, MailEnable, bounces, DKIM", ["Email - Add Domain to AWS SES Domains.docx", "SES - Convert Email IAM user secret key to region specific credentials for email.docx", "SES Bounces rate commands and processes.docx", "MailEnable.docx", "Mailenable Domain whitelisting.docx", "MailEnable - Fix custom header failure cause by install.docx"]),
 ("logs, log analysis, Bravura Monitor, ELK, filebeat", ["BM - Log analysis in SaaS Environments with Bravura Monitor.docx", "Bravura Monitor - Setting up a New Instance.docx", "Bravura Monitor - Saas changes after creation.docx", "Bravura Monitor - ECE Setup.docx", "Filebeat setup from external networks.docx", "ELK - Users - Create Readonly accounts.docx"]),
 ("alerting, on-call, paging, OpsGenie, PagerDuty", ["On Call Ops Genie Doc - WIP.docx", "RTMS Real Time Monitoring Server_ Config + PagerDuty + ZD integration.docx", "Pagerduty - Schedule Maintenance Period.docx", "BCloud - Creating alerts and examples.docx"]),
 ("IDTrack threshold breach", ["Idtrack threshold validation error.docx", "WIP - IDTrack Threshold violation process - SaaS MA process.docx"]),
 ("mobproxy, mobile proxy, SAML on proxy", ["Configuring mobproxy in the SaaS environment.docx", "MobProxy Troubleshooting doc.docx", "ZD-12517_ Workaround to fix saml authentication for Mob proxy.docx"]),
 ("Kubernetes, EKS, Bravura Cloud, BC-Alloy", ["BC_Kubernetes_Troubleshooting_Runbook.docx", "How to setup BC-Alloy agent - WIP.docx", "BCloud - Using BCloud.docx", "Bravura Cloud First Sign In.docx"]),
 ("Bravura Safe, HYPR, 2FA, bootstrap user", ["Safe_ Create a temporary Bravura Safe bootstrap User.docx", "Change 2FA for SAFE users to Email.docx", "HYPR Configuration.docx", "Safe migration between regions.docx", "Bravura Safe Onboarding Checklist.xlsx"]),
 ("training environments, training VMs, AMI", ["Create Monthly Training VMs.docx", "Training - SaaS env - process.docx", "AMI Training Regen Guide.docx", "Training environments trakcing.docx"]),
 ("maintenance page, outage banner", ["Update maintenance page during deployments or outage.docx"]),
 ("load balancer, ALB, NLB, client IP, health checks", ["Guide - Enable Client IP Forwarding & IIS Log Delivery for BSF Behind ALB.docx", "Ignore unwanted Handshake errors for AWS loadbalancer healthchecks.docx", "How to fix BCBSNC NLB Target healthcheck failure.docx"]),
 ("client data sanitization", ["Procedure for sanitizing client data.docx"]),
 ("team split, who owns what, RACI, project team handoff", ["SaaS engagements RACI.xlsx", "SAAS Environment Management.docx", "SaaS projects - collaboration with HIDS project teams.docx"]),
 ("scheduled tasks, long-running scripts, Windows services", ["Long Run Script as Scheduled Task.docx", "Creating a windows service.docx"]),
 ("reCAPTCHA, portal UI", ["ReCaptcha.docx", "Portal UI psadmin update fix.docx"]),
 ("demo environment", ["Demo environment - cheat sheet.docx"]),
]

def age(d):
    if d is None:
        return "not captured"
    y, m, dd = (int(x) for x in d.split("-"))
    return "stale" if date(y, m, dd) < STALE_CUTOFF else "current"

def esc(s):
    return s.replace("|", "\\|")

def table(rows, show_folder=False):
    hdr = "| Document | Folder | Modified | Age | Owner | Verified | Notes |" if show_folder \
          else "| Document | Modified | Age | Owner | Verified | Notes |"
    sep = "|---|---|---|---|---|---|---|" if show_folder else "|---|---|---|---|---|---|"
    out = [hdr, sep]
    for name, folder, mod, note in sorted(rows, key=lambda r: r[0].lower()):
        m = mod or "—"
        a = age(mod)
        if show_folder:
            out.append(f"| {esc(name)} | {folder} | {m} | {a} | | | {esc(note)} |")
        else:
            out.append(f"| {esc(name)} | {m} | {a} | | | {esc(note)} |")
    return "\n".join(out)

def topic_table():
    out = ["| If the question is about | Look at |", "|---|---|"]
    for topic, docs in TOPICS:
        out.append(f"| {esc(topic)} | {esc('; '.join(docs))} |")
    return "\n".join(out)

allrows = PROCESS + WIP + TEMPLATES + RECORDS + CLIENT + POLICY + HOUSEKEEPING + SUBFOLDERS
total = len(allrows)
undated = sum(1 for r in allrows if r[2] is None)
stale = sum(1 for r in allrows if r[2] and age(r[2]) == "stale")
current = sum(1 for r in allrows if r[2] and age(r[2]) == "current")
undated_names = sorted((r[0] for r in allrows if r[2] is None), key=str.lower)

doc = f"""---
artifact: saas-process-docs-index
schema_version: 1
indexed_through: "{INDEXED_THROUGH}"
indexed_scope: "GRP SaaS (Bravura Security)-Team > Documents > Team > Process Docs (recursive)"
sharepoint_folder_uri: "{FOLDER_URI}"
index_method: "file listing + search excerpts; most documents not read in full"
items_indexed: {total}
items_without_captured_date: {undated}
owner: "Robert Sinclair, SaaS Team"
status: draft
rebuild: "python3 gen_index.py > SaaS-Process-Docs-Index.md"
---

# SaaS Process Docs — Index

**Last indexing run: {INDEXED_THROUGH}**

Everything below reflects the library as it stood at that moment. Any file added or
changed in SharePoint after that timestamp is **not** in this index.

---

## For AI agents — read this first

This file exists so you do not have to crawl every file in a {total}-item SharePoint library to answer
"do we have a process for X?". Use it as a first pass, not as the final word.

### The freshness contract

`indexed_through` in the frontmatter is the moment this index was built. It is a
hard boundary:

- Files **modified at or before** `indexed_through` → this index describes them.
- Files **modified after** `indexed_through` → this index knows nothing about them.
  A process may well be documented in one of them.

**Always run the gap check before concluding that a process is undocumented.**
Saying "there is no process for X" without it is wrong, because someone may have
added exactly that document since the last run.

### Gap check

Pass `indexed_through` as `afterDateTime` to the SharePoint search, scoped to this
folder:

```
sharepoint_search(
    query="<your topic>",
    folderName="Process Docs",
    afterDateTime="{INDEXED_THROUGH}"
)
```

Anything returned is newer than this index. Read it directly.

Search alone is not reliable here — its `folderName` filter leaks into other
drives and multi-term queries frequently return nothing. For an authoritative
listing, read the folder:

```
read_resource(uri="{FOLDER_URI}")
```

then compare against the tables below. Names present in the folder but absent
here were added after the last run.

### Answering a lookup

1. Check **Topic lookup** below for the subject.
2. Run the gap check above for anything added since.
3. Read the candidate documents before answering. The Notes column records what
   is visible from outside — filename, location, opening lines — not a content
   review.
4. Report the document's `Age` and `Verified` state alongside the answer. A
   document existing is not the same as a document being correct.

### What this index does not tell you

- **Whether a document is accurate.** `Age` is derived from the SharePoint
  modified date only. Opening a file in Word and saving it bumps that date
  without changing a word. `SAAS Environment Management.docx` last saved in 2025
  and still says "revision July 2021" in its own text.
- **Who owns a process, or when it was last verified.** Those columns are blank
  by design; the data does not exist yet. Do not infer an owner from the Notes.
- **Anything about {undated} files whose date was not captured** (listed under
  Limitations). For those, the gap check cannot tell you whether they changed,
  so treat them as unknown and read them directly if relevant.

### Rebuilding

`gen_index.py` in the `roberts-ai-tools` repo, under `process-docs/`. Re-running it
refreshes `indexed_through`. The inventory is held in the script as literal data, so
a rebuild needs the SharePoint listing re-read and the data lists updated first.

---

## Topic lookup

Subject to candidate documents. Ordered roughly by how often each comes up.
Names are exactly as they appear in SharePoint, including typos.

{topic_table()}

---

## Summary

| | Count |
|---|---|
| Items indexed | {total} |
| Modified within 2 years | {current} |
| Older than 2 years | {stale} |
| No date captured | {undated} |
| Marked WIP | {len(WIP)} |
| Templates | {len(TEMPLATES)} |
| Completed execution records | {len(RECORDS)} |
| Client-specific one-offs | {len(CLIENT)} |
| Policy and reference | {len(POLICY)} |
| Superseded or housekeeping | {len(HOUSEKEEPING)} |
| In sub-folders | {len(SUBFOLDERS)} |

`Age` is mechanical: `stale` means last modified before {STALE_CUTOFF.isoformat()}
(over two years before the indexing run), `current` means after. It measures when a
file was last saved, nothing more.

---

## What stands out

**{len(WIP)} documents are marked WIP, and five of them are recovery or access-control
procedures.** Appstate stuck RBAC recovery, instance snapshot/revert with RDS, client
superuser access creation, client decommissioning, and the on-call OpsGenie runbook.
These get reached for under pressure, by whoever is on call. WIP is the riskiest state
for exactly these docs, because someone will follow one during an incident and find the
gap then.

**Four documents describe environment and release management, and they disagree.**
`SAAS Environment Management`, `WIP - SaaS Environment Management and Release
Management`, `SaaS deployment processes`, and `SaaS Alternative Management Pathways`.
The first two are near-duplicates; the WIP copy has empty Test and Prod headings under
its deploy-process section. A new engineer has no way to tell which to follow.

**Three copies of the Superuser Access policy.** A `.docx` marked wip, a PDF dated
2025-March-14, and an undated PDF from 2025-03-06. This one goes to clients, so "which
is current" has a customer-facing answer.

**Two templates share one body.** `Docusign Deploy Document Template.docx` and
`Release Notes Template - docusign_release_YYYYMMDD.docx` both open with the same
release-notes text. One of the two filenames is wrong about its contents.

**The two VM configuration docs point at different source repositories.** The Linux one
points at Bitbucket; the Windows one at `gitlab.hitachi-id.com`. Both describe the same
automation step, and the GitLab host predates the Bravura rebrand.

**Nine files are completed execution records, not procedures.** Six dated minor-upgrade
checklists, two DocuSign release records, and a Vericast go-live. Worth keeping as
evidence; they make search noisier sitting beside the procedures.

**Some docs already capture roles, informally.** `Bravura Monitor - Setting up a New
Instance` opens with a request form capturing Internal Owner/Trustee and Customer
Owner/Trustee. `Make Client DB backups Using Ansible` uses WHAT / WHO / HOW headings.
`Create Monthly Training VMs` names requesting and performing teams in its first two
sentences. `Deployment document template - SaaS` has "Changed requested by". Four docs,
four shapes.

**Two docs name a single person as the dependency.** `Demo environment - cheat sheet`
says "Sole surviving author: JohnN" in its own text. `Training - SaaS env - process`
routes requests through a named individual rather than a role.

**Housekeeping.** A folder named `delete these`, an `Untitled spreadsheet.xlsx`, a
`Document.docx` under Bravura Monitor/Watchers, one filename with a trailing space, and
three with typos (`procress`, `trakcing`, `effeincy`). A single 292 MB training video is
most of the library's total size.

---

## Processes and runbooks ({len(PROCESS)})

{table(PROCESS)}

---

## Marked WIP ({len(WIP)})

{table(WIP)}

---

## Templates ({len(TEMPLATES)})

{table(TEMPLATES)}

---

## Completed execution records ({len(RECORDS)})

Records of work already done. Evidence, not procedure.

{table(RECORDS)}

---

## Client-specific and one-off troubleshooting ({len(CLIENT)})

Tied to one client or one incident. Useful history; not general procedure.

{table(CLIENT)}

---

## Policy and reference ({len(POLICY)})

{table(POLICY)}

---

## Superseded, media and housekeeping ({len(HOUSEKEEPING)})

{table(HOUSEKEEPING)}

---

## Sub-folders ({len(SUBFOLDERS)})

{table(SUBFOLDERS, show_folder=True)}

---

## Limitations

- Compiled from file metadata and the opening lines returned by search. Most documents
  were **not** opened and read in full. The Notes column flags what is visible from
  outside, not a content review.
- Modified dates come from SharePoint and reflect the last save, not the last meaningful
  change or the last time anyone confirmed the steps still work.
- Categories are judgements from filenames and opening lines. Some will be wrong, in
  particular where a generic title hides client-specific content — `MobProxy
  Troubleshooting doc` is the clearest example.
- Owner and Verified are empty throughout. Neither can be derived from metadata.
- Nothing in SharePoint was created, modified, moved or deleted to produce this index.

### Files with no captured date ({undated})

Content search did not return these, so no modified date was recorded. The files exist —
they are in the folder listing. The gap check above cannot tell you whether they have
changed since the last run, so read them directly when they are relevant.

{chr(10).join('- `' + n + '`' for n in undated_names)}
"""

print(doc)
