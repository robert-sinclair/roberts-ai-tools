# Open questions and review findings

**Against:** `requirements-v0.1-baseline.md`. Current requirements are `requirements.md` v0.2.
**Raised:** 2026-10-13
**Status:** D1-D17 in `decisions.md` close most of A-E. A2 deferred to the UI project.
Section F below is the live open list.

Each item has my read and a recommendation so it can be answered quickly. Answer, and
the baseline gets amended. UI questions are parked — see section D.

---

## A. Blocking — these change the architecture, decide before any code

### A1. Is this single-account/single-region, or multi-account? — CLOSED, see D1

The baseline assumes one set of credentials and one configurable region (Req 2.3). A SaaS
platform almost never is. If the tool has to reach more than one client account, it needs
an account registry plus `sts:AssumeRole` per account, a role-chaining credential cache,
and account selection threaded through every call. Retrofitting that later means touching
every page and every client method.

**Recommendation:** decide now. If multi-account is even likely, build the client layer
around "session for (account, region)" from day one even if the registry starts with one
entry.

### A2. Who is allowed to use the app, and can they all do the writes? — DEFERRED, see D2

Req 2 covers how the app authenticates to *AWS*. Nothing covers how a *person*
authenticates to the app. The glossary says "an authenticated person" and then no
requirement delivers it.

This matters more than usual: the app holds credentials that can deregister production
targets. Anyone who reaches the URL inherits those credentials. That is a privilege
escalation path, and in a PAM shop it will not survive review.

Two sub-questions:
- What is the login mechanism? (Entra ID / SSO, or something local?)
- Is there a role split — read-only viewer vs. operator who can snapshot and
  register/deregister? Or is everyone who can log in trusted with the writes?

**Recommendation:** SSO, and two roles. Default every user to read-only.

### A3. Where does it run? — CLOSED, see D3

Not stated anywhere. This determines the credential model (instance profile vs. env vars),
whether the Defined_Config file survives a restart (see B4), whether it needs TLS, and
whether it is internet-reachable.

**Recommendation:** needs an answer before the config storage question can be closed.

### A4. Does drift detection overlap with Terraform, and is that deliberate? — CLOSED, see D4

The platform's desired state already lives in Terraform. Req 11 adds a second,
hand-maintained JSON file describing expected state. That is two sources of truth that
will disagree.

There is a defensible version of this: Terraform owns *provisioned* state, while the
drift file covers *operational* state Terraform does not manage — which targets are
registered right now, which instances are expected to be running after a failover.
Target group membership during an incident is exactly that.

**Recommendation:** scope Req 11 to operational state only and say so explicitly in the
requirement. Anything Terraform already owns should be caught by `terraform plan`, not
re-declared here. If the intent is broader, say so and the file needs a different design.

### A5. Is drift only checked when someone opens a page? — CLOSED, see D6

Req 11.8 fires the comparison on page load. That means drift is detected only if a human
happens to look. For a detection feature that is close to useless — the value is being
told, not finding out.

**Recommendation:** add a scheduled evaluation and a notification path. Which one is the
question — OpsGenie (already in use per the on-call runbook), Teams, or email.

---

## B. Defects in the baseline — these are wrong or will break

### B1. `elasticloadbalancingv2` is not an IAM service prefix (Req 9.3)

Req 9.3 names `elasticloadbalancingv2:RegisterTargets` / `DeregisterTargets`. ELBv2 (ALB
and NLB) uses the `elasticloadbalancing` prefix in IAM — the ARNs are
`arn:aws:elasticloadbalancing:us-west-2:...:targetgroup/...` in the AWS SDK examples.
Written as-is, the policy grants nothing and every register/deregister returns
AccessDenied. Confirm against the
[Service Authorization Reference](https://docs.aws.amazon.com/service-authorization/latest/reference/reference.html)
when the policy is written.

### B2. `ec2:CreateTags` as written is far too broad (Req 9.3)

Unscoped `ec2:CreateTags` lets the tool retag any EC2 resource in the account — instances,
volumes, security groups. That is a write capability well beyond snapshots and it breaks
the Req 9 guarantee in spirit.

AWS documents the exact pattern for this: scope `CreateTags` to `snapshot/*` with an
`ec2:CreateAction: CreateSnapshot` condition, so tagging is only possible in the context
of the snapshot call ([Example IAM policies for Amazon
EBS](https://docs.aws.amazon.com/ebs/latest/userguide/security_iam_id-based-policy-examples.html)).

**Recommendation:** adopt that pattern verbatim, and add the IAM policy to the repo as a
reviewable artifact rather than leaving it as prose.

### B3. Snapshot lookup by Name tag will mislink snapshots (Req 3.6)

"Matching the instance identifier or name" is undefined. If it is a substring match,
`client-app-01` matches `client-app-01-dr`, and an engineer sees another host's backups
listed under theirs. Under incident pressure that is a restore of the wrong volume.

**Recommendation:** two changes. (1) Write an explicit `SourceInstanceId` tag at creation
time, making lookup exact for anything this tool creates. (2) For legacy snapshots,
specify exact-match-only on the Name tag and label those results as
"matched by name, unverified" in the output.

### B4. Defined_Config as a git-tracked file the app writes to itself (Req 11.1 + 11.2) — CLOSED, see D5

Req 11.1 wants the file version-controlled in the project directory. Req 11.2 wants users
editing it through the UI. Those fight each other:

- The app writes to its own working tree, so someone has to commit out of band or the
  change is invisible to git.
- A redeploy from the repo overwrites edits made through the UI.
- In a container, the file is gone on restart.
- Two users editing at once silently clobber each other — no locking, no revision.
- No schema version, so the first format change breaks every existing file.

**Now unblocked by D3** — the VM has persistent disk, so both models below are viable.

**Recommendation:** pick one of two models and drop the other.
- **Git-native:** the file is edited in the repo by PR, and the app reads it read-only.
  Change review comes free. The UI shows drift but does not edit config.
- **App-owned:** the config lives in a real datastore (SQLite is enough) with a revision
  history, and git is not involved.

Both are defensible. The current blend is not. Either way the file/record needs a
`schema_version` field from the first commit.

### B5. "Fetch all log events from that stream" (Req 6.5)

A busy Lambda log stream can be hundreds of MB. Combined with the 30-second timeout in
Req 8.3, this requirement fails on exactly the functions worth investigating.

**Recommendation:** bound it — last N events or last N minutes, newest first, with
explicit paging for more.

### B6. S3 object counts (Req 5.3)

S3 has no cheap object count. Counting means walking the bucket; the alternative is the
CloudWatch `NumberOfObjects` metric, which is published roughly daily and is therefore
stale.

**Recommendation:** use the CloudWatch metric, label it with its timestamp, and drop any
expectation that it is live. Also cap Req 5.2's object listing — "top-level objects"
in a bucket with a flat key space is still unbounded.

### B7. Pagination is not required anywhere

No requirement mentions it, but EC2, Lambda, RDS, S3, CloudWatch and ELB all paginate.
A first pass that forgets this works in a dev account and silently truncates in
production — the worst possible failure mode for an inspection tool.

**Recommendation:** make it a cross-cutting requirement: every listing paginates to
completion or states plainly that the result is truncated. Never silently partial.

### B8. Register/deregister is treated as equivalent to snapshotting (Req 9)

Req 9 groups all three write actions under one "explicit exception". They are not
comparable. CreateSnapshot is additive and reversible — the worst case is a storage bill.
DeregisterTargets removes capacity from a live load balancer immediately, and
deregistering the last healthy target is an outage.

**Recommendation:** split the exception into two tiers with different controls. Also add a
guard: refuse to deregister if it would drop the healthy target count below a floor
(the Defined_Config expected healthy count is the natural source for that number — the
two features fit together well).

### B9. "Config_Alarm" collides with two AWS product names — OPEN, naming not yet chosen

AWS Config and CloudWatch Alarms are both in use on the platform. A third thing called a
"Config Alarm" will cause confusion in incident channels.

**Recommendation:** rename to drift — `Drift_Rule`, `Drift_Finding`. The folder name
already uses the word.

---

## C. Gaps — missing requirements

### C1. No audit trail of user actions

Req 8.4 logs *errors*. Nothing records that a named user created a snapshot or
deregistered a target, when, against which resource, and whether it succeeded. For a tool
that performs production writes this is not optional, and it is the first thing an auditor
asks for.

**Recommendation:** a structured append-only audit log of every write attempt — actor,
timestamp, action, target ARN, parameters, outcome — separate from the error log.

### C2. No client/tenant scoping — REOPENED and escalated by D12; tags are not a boundary

Nothing in the baseline mentions which client's resources are in view. If the platform is
multi-tenant and resources are tagged by client, the tool needs filtering at minimum, and
possibly a hard boundary so an operator working on client A cannot deregister a target
belonging to client B.

### C3. Snapshot lifecycle is undefined — PARTLY CLOSED by D7; C3a and C3b below remain

Snapshots can be created and never deleted — deletion is correctly out of scope. So they
accumulate and cost money forever.

Open: `Snapshot_Name_Format` is still a placeholder. What tags go on for cost allocation
and retention? Is there an existing DLM or retention policy these should inherit? What
happens when an instance has several attached volumes — snapshot all of them as a set, or
make the user pick one?

### C4. Throttling and caching

A dashboard that fans out describe calls on every page load will hit AWS API throttling,
and with several engineers on it during an incident it will hit it faster. Nothing in the
baseline mentions caching, backoff, or rate limiting.

**Recommendation:** short-TTL cache on listings, explicit refresh, retries with
exponential backoff on `Throttling` / `RequestLimitExceeded`.

### C5. The non-intrusive guarantee has no enforcement mechanism

Req 9.1 says the client "SHALL only invoke" read-only actions. As written that is a
promise, not a control — one careless call in a future PR breaks it silently.

**Recommendation:** make it testable. A hard allowlist of permitted API operations in the
client layer that raises on anything else, plus a test asserting the allowlist contents.
Then the guarantee is enforced in two places (IAM and code) and a reviewer can see it.

### C6. Suppression / maintenance windows for drift — now mandatory under D6, model not chosen

An instance deliberately stopped for maintenance will alarm on every page load with no way
to silence it. Alarms you cannot mute get ignored, which kills the feature.

**Recommendation:** per-rule acknowledgement with an expiry.

### C7. Testing approach — defaulting to moto unless objected to

Not mentioned. Python + boto3 means deciding between `moto`, `botocore.stub.Stubber`, or
recorded fixtures. It affects how the client layer is structured, so it is a now decision,
not a later one.

### C8. Resource coverage — is the list complete? — PARTLY CLOSED by D8

Baseline covers EC2, S3, Lambda, RDS, CloudWatch Logs, target groups. Worth asking what
actually gets looked up during an incident that is missing: ACM certificate expiry, Route
53 records, ECS/EKS, Secrets Manager metadata, EBS volumes as first-class objects, Auto
Scaling group state.

Not a request to add them — a request to confirm the list is deliberate.

### C9. Non-functional requirements absent

No targets for concurrent users, page load time, session timeout, or browser support.
Concurrency and page load shape the architecture (sync vs. async, background refresh), so
they are worth a rough number now. The rest can wait.

---

## D. Deferred — UI

UI is being built in a separate project that is not ready for team work. No design
decisions are being made here.

What this still needs from the UI side, eventually, is only the contract:
- Where does the backend end and the UI begin — server-rendered templates, or a JSON API
  the other project consumes?
- If it is an API, that shape needs agreeing before the backend is written.

Parked until the other project is ready. Noting it so it is not forgotten.

---

## E. Raised by the first two rounds of decisions

### C3a. Multi-volume instances — snapshot all, or make the user pick? — CLOSED, see D9

Not answered by D7. An instance with a data volume separate from its root volume is the
normal case on this platform. Snapshotting one of them and calling it a backup is a
restore that fails when someone needs it.

**Recommendation:** snapshot every attached volume as a set, sharing a set ID tag, so one
action captures the instance. Asking an engineer to choose a volume mid-incident is where
mistakes get made.

### C3b. Retention — nothing deletes these — CLOSED, see D10

Deletion is correctly out of scope for the tool, so snapshots created through it
accumulate and are billed forever. D7 did not take the "inherit existing DLM tags" option,
which leaves no cleanup path at all.

Either the snapshots carry whatever tag an existing Data Lifecycle Manager policy keys on
so they age out automatically, or someone owns deleting them by hand, or storage grows
without bound.

**Recommendation:** inherit the existing DLM tag scheme. It costs one tag and removes the
problem entirely.

### E1. Drift evaluation interval

D6 settled that evaluation is scheduled. How often is not settled. Target group membership
during a failover wants minutes; certificate expiry wants daily. These probably want
different intervals per rule type rather than one global number.

### E2. OpsGenie alert shape

Dedup key, priority per rule type, and what closes an alert — does a resolved finding
auto-close the OpsGenie alert, or does on-call close it manually?

### E3. Does account equal client? — CLOSED, see D12 (no)

D1 made the tool multi-account. If each client's environment is its own AWS account, then
account selection *is* tenant scoping and C2 is largely answered — the blast radius of a
mistake is bounded by which role you assumed.

If accounts are shared between clients and separation is by tag, C2 is still open and
needs real filtering, because an operator working on client A can reach client B's
resources with the same credentials.

### E4. Global vs. regional services in the client layer

Raised by D8. Route 53 is global. ACM is regional but CloudFront certificates are pinned
to `us-east-1`. IAM and S3 bucket listing are global too. The `(account, region)` session
key from D1 does not describe these correctly.

**Recommendation:** the client layer carries a service classification — global, regional,
or regional-with-a-fixed-home — rather than assuming a region applies to everything. Cheap
now, invasive later.

### E5. Backup for the SQLite database

Required by D5. Persistent disk is not backup, and the database will hold the drift config
and the audit log — the audit log especially, since losing it defeats the point of having
one. Scheduled dump to versioned S3 is the obvious answer; it needs an owner and a
restore test.

---

## F. Still open after v0.2

Sections A–E above are the review history, with each item's status marked. This section is
the live list — what v0.2 does not answer.

### F1. Do write paths re-verify the Client_Tag at execution time?

D13 made application filtering the whole boundary. A target identifier arriving from the
browser is caller-supplied input, so verifying the tag when listing and trusting it on the
subsequent write leaves a gap that does not need a malicious actor to exploit — a stale
browser tab is enough.

**Recommendation:** re-read the resource's tag inside the write path and compare against
the Client_Scope before calling AWS. One extra describe per write, which is nothing against
the cost of a cross-client deregistration.

### F2. Evaluation interval per rule type (was E1)

Target group membership during a failover wants minutes. Certificate expiry wants daily.
A single global interval serves neither well.

### F3. OpsGenie alert shape (was E2)

Priority per rule type, and whether a resolved Drift_Finding auto-closes its alert or
on-call closes it manually.

### F4. Who owns the snapshot expiry enforcement job?

D10 and D15 put an expiry tag on every snapshot and left deletion to a separate platform
job. Until that job exists and has an owner, the tag describes a policy that nothing
enforces — which is worse than no retention at all, because it reads as handled.

### F5. Exact tag keys

The Client_Tag key, the environment tag, and the expiry tag must match what the platform
already uses. Not invented here. Needs confirming against the Terraform.

### F6. Concurrency and performance targets (was C9)

How many engineers on it at once, and what page load is acceptable. These shape whether
the evaluator and the request path share a process. Worth a rough number before the first
structural commit.

### F7. Backend/UI contract

Server-rendered templates or a JSON API the UI project consumes. Parked until that project
is ready, but it blocks the first HTTP-layer commit.

### F8. Retained from the original review, not yet decided

- B5/B6/B7 bounds are specified in v0.2 but the actual limits are placeholders.
- Request timeout value (Requirement 18.3) replaced the baseline's fixed 30 seconds and
  needs a number.
- Cache TTL (Requirement 19.3) needs a number.
