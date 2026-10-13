# aws-saas-and-drift

A Python web application for inspecting AWS resources and detecting configuration drift
against a declared expected state.

**Status:** requirements gathering. No code yet.

## Contents

| File | What it is |
|---|---|
| `requirements.md` | v0.1 baseline, imported from the original intake document. Not settled. |
| `open-questions.md` | Review findings and unresolved questions against that baseline. |

## Where things stand

The baseline covers EC2, S3, Lambda, RDS, CloudWatch Logs and ELB target groups, plus an
optional per-resource expected-state file with drift alarms.

Before any code gets written, the blocking items in section A of `open-questions.md` need
answers — multi-account, user authentication, where it runs, and how drift relates to
Terraform. Those decisions change the shape of the client layer, so they are not
deferrable.

## UI

The UI is being built in a separate project that is not ready for team work. No UI design
decisions are being made here. The only open UI-adjacent item is the backend/frontend
contract, parked in section D of `open-questions.md`.

## Conventions

Python. Platform is Terraform-built and Ansible-deployed; anything this tool declares as
expected state should not duplicate what Terraform already owns.
