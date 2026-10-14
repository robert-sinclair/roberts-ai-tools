# aws-saas-and-drift

A Python web application for inspecting AWS resources across multiple accounts, and for
detecting drift between a declared operational state and what is actually running.

Built for operations engineers and developers who need to check resource state, read logs,
inspect load balancer routing and certificate expiry, and take two bounded write actions —
snapshot an instance, and move a load balancer target — without AWS Console access or CLI
expertise.

**Status: requirements complete at v0.2. No code yet.**

## Contents

| File | What it is |
|---|---|
| `requirements.md` | v0.2 — the current requirements. 23 requirements in EARS form. |
| `decisions.md` | D1–D17. Settled decisions, each with its consequences and accepted risks. |
| `open-questions.md` | Review history (sections A–E, with status) and the live open list (section F). |
| `requirements-v0.1-baseline.md` | The original intake document. Reference only — it contains errors v0.2 corrects. |
| `CLAUDE.md` | Working rules for this repository. |
| `kickoff-prompt.md` | Starting prompt for the first working session. Delete once used. |

## What the tool does

Reads EC2, S3, Lambda, RDS, CloudWatch Logs, ELB target groups, ACM certificates and
Route 53 across multiple AWS accounts reached by `sts:AssumeRole`.

Writes exactly three things: `CreateSnapshot`, `RegisterTargets`, `DeregisterTargets`.
Nothing else. That boundary is enforced by an allowlist in code and by IAM policy, and
both are tested.

Evaluates drift rules on a schedule and raises OpsGenie alerts. Drift covers operational
state — target group membership, expected healthy counts, instance running state,
certificate expiry — and deliberately not the provisioned configuration Terraform already
declares.

## Shape of the design

| Area | Decision |
|---|---|
| Accounts | Multi-account via `sts:AssumeRole`, session scoped per `(account, region)` |
| Services | Classified global / regional / regional-with-fixed-home — Route 53 is global, CloudFront certs pin to `us-east-1` |
| Runtime | Long-lived VM with persistent disk, instance profile for base credentials |
| State | SQLite on the VM for drift rules and the audit log, backed up off the VM |
| Tenancy | Shared AWS accounts, clients separated by resource tag, scoping enforced in the app |
| Alerting | Scheduled evaluation into OpsGenie, with suppression that always expires |
| Secrets | AWS Secrets Manager |
| UI | Built in a separate project. No UI design decisions made here. |

## Before building

Read `decisions.md`. Most obvious design questions are already answered there, including
the ones answered "no".

D13 is the one to read carefully. Clients share AWS accounts and separation is enforced in
the application, not in IAM — so the scoping filter is the entire boundary between one
client's resources and another's. The risk was raised, accepted, and recorded with what it
obliges in return.

## Open

Section F of `open-questions.md`. Nothing there blocks the AWS client layer. F7 — the
backend/frontend contract — blocks the HTTP layer and waits on the UI project.
