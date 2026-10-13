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

## D5 — Drift config lives in SQLite on the VM, edited through the app

**Closes:** B4 · **Decided:** 2026-10-13

The app owns the drift configuration in a SQLite database on the VM's persistent disk.
Req 11.2's editing UI is kept. Req 11.1 — a git-tracked JSON file in the project
directory — is dropped.

**Consequences:**
- Every config change is a row with an actor, a timestamp and a prior value. Revision
  history comes from the schema, not from git.
- `schema_version` from the first migration. Use a migration tool rather than ad-hoc
  `ALTER TABLE`.
- Concurrency stops being a silent clobber: writes are transactional, and a stale-read
  edit can be rejected rather than overwriting.
- **Backup is now required.** Persistent disk is not backup. The database needs a
  scheduled dump to somewhere off the VM — S3 with versioning is the obvious target, and
  it is a read-only concern for the tool itself since the dump is written by the platform,
  not by the app's AWS client.
- The audit log (C1) lands in the same database, in its own append-only table.

## D6 — Drift is evaluated on a schedule and raises OpsGenie alerts

**Closes:** A5 · **Decided:** 2026-10-13

A background job evaluates drift rules on an interval. Findings raise an OpsGenie alert.
Req 11.8's page-load-only evaluation is dropped; the page reads the last stored
evaluation instead of calling AWS on render.

**Consequences:**
- The evaluator runs independently of any browser session, so it needs its own AWS
  credentials path and its own account+region iteration. It is the heaviest AWS consumer
  in the app and the main reason caching and backoff (C4) matter.
- Findings are persisted with first-seen and last-seen timestamps, so the dashboard can
  show history and OpsGenie can deduplicate rather than re-alerting every interval.
- **Suppression is now mandatory, not optional (C6).** An instance stopped for planned
  maintenance must be mutable without editing the rule, or on-call will mute the
  integration and the feature dies. Per-rule acknowledgement with an expiry.
- Alert payloads need a stable dedup key per rule, and a severity. Both still open.
- The OpsGenie API key is a secret the app holds. It is the first non-AWS credential in
  the design.

## D7 — Snapshot tagging

**Partially closes:** C3 · **Decided:** 2026-10-13

Snapshots created by the tool are tagged with:
- `SourceInstanceId` — the instance the volume was attached to at snapshot time. This is
  the exact-match lookup key that fixes the Name-tag mislinking in B3.
- Cost allocation tags identifying the client and the environment.

Snapshot lookup therefore has two paths, and the UI must distinguish them: exact match on
`SourceInstanceId` for snapshots this tool created, and exact match on the Name tag for
legacy snapshots, shown as "matched by name, unverified". Substring matching is
prohibited.

**Still open:** the exact tag keys for client and environment must match whatever the
platform already uses — not invented here. Multi-volume behaviour and retention are
unresolved; see C3a and C3b in `open-questions.md`.

## D8 — Add ACM certificate expiry and Route 53 records

**Partially closes:** C8 · **Decided:** 2026-10-13

Added to the resource list. Auto Scaling group state and EBS volumes as first-class
objects were considered and not taken.

**Consequences:**
- **Both are awkward under the D1 (account, region) session model.** Route 53 is a global
  service with no regional endpoint. ACM is regional, but certificates used by CloudFront
  must live in `us-east-1` regardless of where everything else runs, so an ACM inventory
  that only reads the configured region will miss them and report healthy.
- The client layer needs an explicit notion of global vs. regional services, rather than
  assuming every call takes a region.
- ACM expiry is a natural drift rule — "expires within N days" — and fits D4's
  operational-state scope, since a certificate's remaining life is not something
  `terraform plan` reports on. Worth adding as a rule type.
