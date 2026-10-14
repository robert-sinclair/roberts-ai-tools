# aws-saas-and-drift

A Python web application for inspecting AWS resources and detecting configuration drift
against a declared expected state.

**Status:** requirements gathering. No code yet.

## Contents

| File | What it is |
|---|---|
| `requirements.md` | v0.2 — current requirements, incorporating D1–D17. |
| `requirements-v0.1-baseline.md` | The original intake document, kept for reference. |
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

v0.2 is written and covers 23 requirements. Seventeen decisions are recorded in
`decisions.md`, including the ones that shaped the architecture: multi-account via
AssumeRole, SQLite on the VM for drift config and the audit log, scheduled evaluation
raising OpsGenie alerts, and shared AWS accounts with clients separated by tag.

The tenancy decision is the one to re-read before building. Accounts are shared, client
separation is enforced in the application rather than in IAM, so a defect in the scoping
filter permits a cross-client write with nothing outside the app to stop it. That risk is
accepted and recorded in D13, along with what it obliges in return.

Still open: section F of `open-questions.md`. Nothing there blocks starting the AWS client
layer; F7 blocks the HTTP layer.

## UI

The UI is being built in a separate project that is not ready for team work. No UI design
decisions are being made here. The only open UI-adjacent item is the backend/frontend
contract, parked in section D of `open-questions.md`.

## Conventions

Python. Platform is Terraform-built and Ansible-deployed; anything this tool declares as
expected state should not duplicate what Terraform already owns.
