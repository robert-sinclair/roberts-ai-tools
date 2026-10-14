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

## D9 — Snapshot every attached volume as a set

**Closes:** C3a · **Decided:** 2026-10-13

"Snapshot this instance" captures every currently attached volume. All snapshots in one
action share a set ID tag so the set can be identified as a unit.

**Consequences:**
- Snapshot listing groups by set, not by individual snapshot. A partial set — some
  snapshots succeeded, some failed — must be visibly marked, because a set that looks
  complete but isn't is a restore that fails later.
- The create path is now multiple API calls that can partially fail. It needs a defined
  outcome for partial failure: report which volumes succeeded, do not silently claim the
  instance was snapshotted.

## D10 — Retention by explicit expiry tag, deletion owned elsewhere

**Closes:** C3b · **Decided:** 2026-10-13

Snapshots carry an explicit expiry date tag. The tool never deletes anything; a separate
platform job enforces the expiry.

**Consequences:**
- The tool stays read-only-plus-create. No deletion permission is added to the IAM policy.
- **This is only a retention policy if the enforcing job exists.** Without it the outcome
  is identical to no retention at all, except that the tags imply otherwise, which is
  worse than being honest about it. The job needs a named owner before this decision is
  real.
- Default retention period, and whether the user can override it per snapshot, are open.
- The UI should show expiry on each snapshot, so an engineer can see that the backup they
  are relying on disappears in three days.

## D11 — Suppression: scheduled maintenance windows plus ad-hoc acknowledgement

**Closes:** C6 · **Decided:** 2026-10-13

Two mechanisms. A maintenance window declared ahead of time against a resource stops its
rules evaluating inside that window. An ad-hoc acknowledgement mutes a specific live
finding for a set duration.

**Consequences:**
- Acknowledgements expire. A permanent mute is a disabled rule nobody remembers disabling,
  so there is no "forever" option.
- Both are audited like any other write: who suppressed what, when, and until when. A
  suppressed finding during an incident is a question someone will ask afterwards.
- The dashboard must show suppressed findings as suppressed, not hide them. Hidden
  suppression is how a real outage gets missed.
- Windows and acknowledgements can overlap, so finding state needs to be computed rather
  than stored as a flag.

## D12 — Shared AWS accounts, clients separated by tag

**Closes:** E3, reopens C2 as a first-class problem · **Decided:** 2026-10-13

An AWS account holds more than one client. Client identity is carried on resource tags.

**Consequences, and this is the most significant risk in the design so far:**

- **A tag is not a security boundary by default.** If scoping is implemented only as a
  filter in the app, then an operator working on client A can still deregister client B's
  target — the credentials permit it and only a UI filter stood in the way. Under D1 the
  assumed role no longer bounds blast radius, because the role spans clients.

  The boundary has to be enforced in IAM with `aws:ResourceTag` conditions on the write
  actions, so the API itself refuses a cross-client write. App-side filtering is then a
  convenience layer over a real control rather than the control itself. Confirm per action
  that the condition key is supported before relying on it.

- **Write actions must re-check the tag at execution time.** A target ARN arriving from a
  browser is user input. Verifying the client tag on the listing and trusting it on the
  subsequent write is a time-of-check/time-of-use gap and, more simply, a forgeable
  parameter.

- **Untagged resources are invisible to tag queries.** `GetResources` in the Resource
  Groups Tagging API explicitly does not return untagged resources
  ([GetResources](https://docs.aws.amazon.com/resourcegroupstagging/latest/APIReference/API_GetResources.html)).
  An untagged production target group would therefore belong to no client and appear in no
  scoped view. The app needs a deliberate "untagged" view so these surface rather than
  hide, and drift rules should be able to flag untagged resources as a finding in their
  own right.

- **Tag lookup is not uniform across services.** EC2 supports `tag:` filters natively on
  its describe calls. RDS's describe filters do not cover tags — tags come from
  `ListTagsForResource` per resource, which is one call per instance and a throttling risk
  under D6's scheduled evaluation. The Resource Groups Tagging API is the better primitive
  for cross-service tenant queries: it filters by tag and resource type in one regional,
  paginated call, though it carries its own rate limit.

- The client tag key must be the one the platform already uses. It is not invented here,
  and it is the same key as the cost allocation tag in D7.

- Drift rules gain a client dimension, so findings can be filtered and alerts routed per
  client.

## D13 — Client separation is enforced by app-side filtering

**Closes:** C2 · **Decided:** 2026-10-13

Scoping to a client is implemented in the application. No `aws:ResourceTag` conditions are
added to the IAM policy for this purpose.

**Accepted risk, recorded deliberately:** the tool's credentials permit acting on any
client's resources in a shared account. The application filter is the whole boundary, so
a defect in that filter permits a cross-client write with nothing outside the app to stop
it. This was raised and the decision stands.

**What this obliges instead:**
- The filter is a single chokepoint every listing and every write passes through, not
  logic repeated per handler. One place to get right, one place to test.
- Tests assert the negative case directly: a request scoped to client A must not return or
  act on a client B resource.
- The audit log (C1) carries the client scope in force at the time of each write, so a
  cross-client action is at least detectable afterwards.
- Revisiting this is a policy change, not a rewrite: the IAM conditions could be added
  later without changing application structure. Worth reconsidering before the tool is
  used on Production.

**Open:** whether write paths re-verify the client tag at execution time rather than
trusting the identifier supplied by the caller. See F1.

## D14 — Untagged resources are visible and writable

**Closes:** part of D12's fallout · **Decided:** 2026-10-13

Resources carrying no client tag appear in a dedicated untagged view, and write actions
against them are permitted.

**Consequences:**
- Untagged production resources stay operable, which is the point.
- A write against an untagged resource is unattributable to a client. The audit log must
  record the absence of a tag explicitly rather than leaving the field blank, so the gap
  is visible in a review.
- Drift should be able to flag untagged resources as a finding, so the untagged set shrinks
  over time instead of becoming permanent.

## D15 — Default snapshot retention is 30 days, overridable

**Closes:** part of D10 · **Decided:** 2026-10-13

Snapshots are tagged with an expiry 30 days out by default. The user may override per
snapshot. Deletion remains owned by the separate platform job named in D10.

## D16 — Secrets live in AWS Secrets Manager

**Closes:** the secrets question raised by D6 · **Decided:** 2026-10-13

The OpsGenie API key, and any later secret, are stored in AWS Secrets Manager and read at
runtime.

**Consequences:**
- Adds `secretsmanager:GetSecretValue` to the IAM policy. A read action, so it does not
  widen the write surface, but it should be scoped to the specific secret ARNs rather than
  granted broadly.
- Secrets are fetched in the account the VM's own identity lives in, not in the assumed
  per-client roles. Two distinct credential paths in the same process.
- Startup now depends on AWS reachability. A Secrets Manager failure must produce a clear
  error, not a silent start with alerting disabled.
- Rotation is available but only useful if the app re-reads rather than caching for the
  lifetime of the process.

## D17 — Drift naming

**Closes:** B9 · **Decided:** 2026-10-14

`Config_Alarm` is replaced throughout by `Drift_Rule` (the declared expectation) and
`Drift_Finding` (a live mismatch). Removes the collision with AWS Config and CloudWatch
Alarms. Adopted in v0.2; say if you want different terms.
