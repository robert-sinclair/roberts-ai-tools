# Drata evidence automation (SaaS)

Automate the Drata evidence the SaaS team owns, so items are renewed before they expire instead of by hand.

Starting point: the responsibility review for John Nelson (Nov 17, 2026). Robert Sinclair (SaaS Team Lead) is taking these over.

## Scope

Start with the 6 controls and 6 evidence items John owned. All of them are SaaS infrastructure.

| Control | Evidence item | Drata status (Nov 17) | Source system |
| --- | --- | --- | --- |
| DCF-152 Automated Security Updates | SSM Patching | **Past due** since Oct 17 | AWS SSM |
| DCF-72 Unique SSH | Standard Linux AD Join Config | **Past due** since Oct 17 | Linux hosts via Ansible |
| DCF-79 Logs Centrally Stored | Centralized Logs Stored in Bravura Cloud (loki and promtail) | **Past due** since Oct 29 | Loki API |
| DCF-86 Operational Audit | OpsGenie | Current; control has 2 open issues | OpsGenie API |
| DCF-92 Encrypted Remote Production Access | ZT Client Required for Production Access | Current to Aug 2, 2027 | Cloudflare API, EC2 security groups |
| DCF-11 Annual Access Control Review | Access Review | Current to Sep 25, 2027 | Jira (SAASK ticket link) |

Not in scope: DCF-88 WAF has no evidence item and passes on Drata's own tests. DCF-95's evidence belongs to someone else.

Later: list every Drata control whose tests or evidence point at the 2 SaaS AWS accounts and add any that nobody owns.

## Approach

Each evidence item gets one Python collector. A collector:

1. Pulls current data from the source system (read-only).
2. Writes the raw data (JSON/CSV) plus a one-page PDF summary with a timestamp.
3. Uploads the PDF as a new artifact on the existing Drata evidence item.

This replaces screenshots with system output. Confirm with the auditor that they accept it.

### Collectors

| Collector | What it collects | Calls |
| --- | --- | --- |
| `ssm_patching` | Maintenance windows, last 30 days of executions, per-instance patch compliance | `ssm:DescribeMaintenanceWindows`, `DescribeMaintenanceWindowExecutions`, `DescribeInstancePatchStates` |
| `linux_ad_join` | Per host: `realm list`, `sshd -T` showing `permitrootlogin no` | Ansible playbook across the prod inventory |
| `loki_logs` | Log line count per tenant/deployment, last 24h | Loki `/loki/api/v1/labels`, `/loki/api/v1/query_range` |
| `opsgenie` | Escalation policies, on-call schedules, last 30 days of alerts with ack times | OpsGenie REST API |
| `zt_access` | Cloudflare Access/Gateway policies; security groups showing mgmt ingress only from WARP / RD Gateway | Cloudflare API, `ec2:DescribeSecurityGroups` |
| `access_review` | Link to the yearly SAASK access-review ticket | Jira API (optional) |

### Drata upload

The v2 API uploads in two steps:

1. `POST /upload-urls` with purpose "evidence" to get a presigned URL and `fileKey`.
2. Create or update the Evidence Library item with that `fileKey`, `ownerId`, `filedAt` and `renewalScheduleType`.

v2 is labelled beta. The exact request fields are not yet verified against our tenant.

Known issue: the review found the API could not return evidence files until the tenant opts into the new Drata Experience. Reading metadata and uploading may still work. Step 1 below checks this.

### Owner report

A separate script reads controls, evidence, and tasks for an owner from the Drata API. It reports anything expired or due within 30 days. It runs weekly so nothing goes past due again.

## Where it runs

Decide once step 1 confirms how each source is reached. Current lean:

- AWS and API collectors: Lambda + EventBridge, deployed with Terraform, read-only IAM role, keys in Secrets Manager.
- Linux host checks: scheduled Ansible playbook on the existing control node, calling the same upload code.
- Schedule: monthly, and at least 2 weeks before each renewal date.

## Order of work

1. **Access check.** Get a Drata API key. Confirm the workspace ID, read evidence metadata, upload one test file. Confirm which of the 2 SaaS AWS accounts is production.
2. **Past-due items.** `ssm_patching`, `linux_ad_join`, `loki_logs`. These unblock DCF-152, DCF-72 and DCF-79.
3. **Owner report** and the due-soon digest. Read DCF-86's failing tests through the API.
4. **Remaining collectors.** `opsgenie`, `zt_access`, then `access_review` if worth it.

## Open questions

- Who in Drata can issue an API key, and can the tenant move to the new Drata Experience?
- Which of the 2 SaaS AWS accounts is production? Only `hitachi-id-saas-test` (011126347217) is confirmed so far.
- Will the auditor accept system-generated reports in place of screenshots?
- Transfer the 6 items' ownership in Drata from John to Robert.

## References

- [Drata API v2: Evidence Library](https://developers.drata.com/openapi/reference/v2/tag/Evidence-Library/)
- [Drata API v2: Uploads](https://developers.drata.com/openapi/reference/v2/tag/Uploads/)
- [Drata API v1: Evidence Library](https://developers.drata.com/openapi/reference/v1/tag/Evidence-Library/)
- [Drata help: Evidence overview (new experience)](https://help.drata.com/en/articles/13404035-evidence-overview-new-experience)
