# <Subject> - <Action>

> Template. Replace every angle-bracket placeholder. Delete this line and all
> italic guidance before publishing. If any field in the header reads "TBD",
> the doc is not ready to publish.

---

## Document control

| Field | Value |
|---|---|
| **Process name** | <Subject - Action> |
| **Status** | Draft / WIP / Approved / Deprecated |
| **Version** | 0.1 |
| **Document owner** | <named person>, <team> |
| **Created** | <YYYY-MM-DD> |
| **Last reviewed** | <YYYY-MM-DD> |
| **Next review** | <YYYY-MM-DD — 12 months, or 6 if Production or security> |
| **Validated by** | <person who followed it end to end>, <YYYY-MM-DD> |
| **Supersedes** | <doc name, or None> |

## Who is involved

| Field | Value |
|---|---|
| **Requester** | <role entitled to ask — e.g. Project Team Lead, CSM, Support, on-call engineer, client. Say "any internal team member" if that is true.> |
| **Intake channel** | <Zendesk / Jira SAASK / Salesforce case / PagerDuty / OpsGenie / deploy ticket / Teams> |
| **Performer** | <role that executes the steps — e.g. SaaS engineer, on-call engineer> |
| **Owning team** | <one team only: SaaS / Project / Engineering / Customer Success / Support> |
| **Other teams involved** | <team — what they do and when. "None" is a valid answer.> |
| **Handoff points** | <what is handed to whom, and what must come back before continuing. "None" if entirely within one team.> |
| **Approval required from** | <role, and for what — e.g. client change control for Production, SaaS lead for infra change. "None" is valid.> |

## Scope and risk

| Field | Value |
|---|---|
| **Purpose** | <one or two sentences: what this achieves and why> |
| **Applies to** | <environments: Dev / Test / Production; all clients or specific> |
| **Does not cover** | <adjacent things people will wrongly expect here, with a pointer to the right doc> |
| **Client impact** | <none / read-only degradation / outage — and expected duration> |
| **Maintenance window required** | <yes/no — and which> |
| **Estimated duration** | <active time, plus any waiting> |
| **Rollback owner** | <role responsible if this must be reversed> |

## Prerequisites

*Everything the performer needs before step 1. If they cannot tick all of these,
they cannot start.*

- **Access:** <accounts, roles, group membership, superuser, console access>
- **Network:** <VPN, jump host, bastion, IP allowlisting>
- **Tooling and versions:** <CLI tools, Ansible, Terraform, Python version>
- **Information to gather first:** <client name, instance name, ticket number,
  current version>
- **Approvals in hand:** <which approvals must already be granted>
- **Pre-checks:** <what must be true about the environment — e.g. no discovery
  job running, replication healthy>

## Procedure

*Numbered steps. Full commands with clearly marked placeholders. State the
expected result of any step whose success is not obvious. A reader with this
document, no network, and no AI tooling must be able to execute it.*

### 1. <Step name>

<What to do.>

```
<exact command, with [placeholders] marked>
```

**Expected result:** <what success looks like>

**If this fails:** <the specific recovery or escalation for this step>

### 2. <Step name>

<Repeat the pattern. For a UI step, name the control in text and attach a
screenshot — do not rely on the screenshot alone.>

### 3. <Step name>

<For a decision point, state the criteria explicitly:>

| Condition | Action |
|---|---|
| <observable condition> | <go to step N / do X> |
| <observable condition> | <go to step N / do X> |

## Validation

*How the performer proves the work succeeded. Specific and checkable — not
"confirm it works".*

- [ ] <check — e.g. superuser can log in at https://[client_name].hitachi-id.net/[instance_name]>
- [ ] <check — e.g. all services report online in the service status view>
- [ ] <check — e.g. replication lag under N seconds across all nodes>
- [ ] <check — e.g. no new tracebacks in the component load log>

## Rollback

*Complete reversal steps. Written to be followed under time pressure by someone
who did not plan the change.*

1. <step>
2. <step>
3. <step>

**Point of no return:** <the step after which rollback is no longer possible,
and what the alternative is. State "none" only if genuinely reversible throughout.>

## Post-completion

- **Notify:** <who gets told, through which channel>
- **Record:** <where the outcome is logged — ticket, deploy doc, checklist>
- **Cleanup:** <snapshots to retain or delete, temporary access to revoke,
  maintenance page to disable>

## Known issues and gotchas

*Things that have gone wrong before. This section is usually the most valuable
part of the doc — add to it every time this process is run and something
surprises you.*

- <symptom> — <cause> — <what to do>

## References

*Named documents and specific sections, not bare links. The reader must know
what to look for even without network access.*

- `<Document name>.docx` — <which section, and why you would read it>
- <Jira or Zendesk ticket> — <what it records>

## Change history

| Version | Date | Author | Change |
|---|---|---|---|
| 0.1 | <YYYY-MM-DD> | <name> | Initial draft |
