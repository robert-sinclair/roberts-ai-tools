# aws-saas-and-drift

A Python web application for inspecting AWS resources and detecting configuration drift
against a declared expected state.

**Status:** requirements gathering. No code yet.

## Contents

| File | What it is |
|---|---|
| `requirements.md` | v0.1 baseline, imported from the original intake document. Not settled. |
| `open-questions.md` | Review findings and unresolved questions against that baseline. |
| `decisions.md` | Settled decisions, with what each one commits us to. |

## Where things stand

The baseline covers EC2, S3, Lambda, RDS, CloudWatch Logs and ELB target groups, plus an
optional per-resource expected-state file with drift alarms.

Four of the five blocking decisions are settled — see `decisions.md`. The tool is
multi-account via AssumeRole, runs on a long-lived VM with persistent disk, and the drift
feature covers operational state only, not what Terraform already declares. App
authentication is deferred to the external UI project; the backend still carries an actor
identity through every write.

Drift config lives in SQLite on the VM and is edited through the app. Drift is evaluated
on a schedule and raises OpsGenie alerts. Snapshots are tagged with `SourceInstanceId`
plus client and environment cost tags. ACM certificate expiry and Route 53 records join
the resource list.

Still open before code: multi-volume snapshot behaviour, snapshot retention, the drift
evaluation interval and suppression model, whether an AWS account maps to one client, and
how global services fit the per-region client layer. Section E of `open-questions.md`.

## UI

The UI is being built in a separate project that is not ready for team work. No UI design
decisions are being made here. The only open UI-adjacent item is the backend/frontend
contract, parked in section D of `open-questions.md`.

## Conventions

Python. Platform is Terraform-built and Ansible-deployed; anything this tool declares as
expected state should not duplicate what Terraform already owns.
