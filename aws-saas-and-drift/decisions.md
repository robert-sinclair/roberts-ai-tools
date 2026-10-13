# Decisions

Settled requirements decisions. Each entry names the question it closed in
`open-questions.md` and what it commits us to. Reopen by raising a new question, not by
editing an entry.

---

## D1 — Multi-account via AssumeRole

**Closes:** A1 · **Decided:** 2026-10-13

The tool reaches more than one AWS account. The client layer is built around a session
scoped to an `(account, region)` pair, with an account registry and `sts:AssumeRole`,
from the first commit — even if the registry starts with a single entry.

**Consequences:**
- Every AWS call carries an account+region context. No module-level default client.
- Credential caching is per assumed role, honouring STS expiry.
- The IAM work splits in two: a trust policy on the role in each target account, and a
  policy on the calling identity permitting `sts:AssumeRole` to those roles.
- Account selection has to appear in the backend/UI contract (section D of
  `open-questions.md`).
- Req 2.3 in the baseline, "configurable target AWS region", is superseded.

## D2 — App authentication deferred to the external UI project

**Closes:** nothing. **Defers:** A2 · **Decided:** 2026-10-13

How a person authenticates to the app, and whether there is a viewer/operator role split,
is owned by the UI project and is not decided here.

**What the backend must still do, so this stays deferrable:**
- Every write path takes an **actor identity** as a required argument and records it in
  the audit log (C1). The backend does not care how the identity was established; it
  refuses to perform a write without one.
- Authorisation is a single checkpoint the backend calls before any write, with a
  permissive default implementation. When the UI project lands a real identity model, that
  checkpoint is where it plugs in — not scattered across request handlers.

**Open risk, carried deliberately:** until the UI supplies identity, anything that can
reach the backend can trigger production writes with the tool's AWS credentials. Not an
objection to the deferral — a note so it is not forgotten at deploy time.

## D3 — Runs on a long-lived VM with persistent disk

**Closes:** A3 · **Decided:** 2026-10-13

**Consequences:**
- Local state survives restarts, so a file or SQLite database on disk is viable for the
  drift config and the audit log. (Which one is still open — B4.)
- AWS credentials come from an instance profile where the VM allows it, falling back to
  the standard boto3 chain. No static keys in config files.
- The VM's own role is the identity that assumes the per-account roles in D1.
- Persistent disk is not backup. Whatever holds the drift config needs to be recoverable.

## D4 — Drift covers operational state only

**Closes:** A4 · **Decided:** 2026-10-13

The drift feature covers state that changes between Terraform applies and that Terraform
does not manage: target group membership, registered-vs-expected targets, healthy target
counts, instance running state after a failover, RDS availability.

It does **not** re-declare provisioned configuration. Lambda runtime, memory and timeout,
and S3 bucket existence, are Terraform's to own and `terraform plan` catches them.

**Consequences:**
- Baseline Req 11.6 (Lambda runtime/memory/timeout) and 11.7 (S3 bucket existence) are
  dropped.
- Req 11.2's claim that every supported resource type gets a drift config narrows to
  target groups, EC2 running state, and RDS availability.
- The feature gets a sharper name than "Config_Alarm" (B9), which also removes the
  collision with AWS Config and CloudWatch Alarms.
