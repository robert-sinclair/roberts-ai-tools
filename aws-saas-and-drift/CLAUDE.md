# aws-saas-and-drift — working rules

A Python web application for inspecting AWS resources across multiple accounts and
detecting drift between a declared operational state and what is actually running.

**Status: requirements only. No code yet.**

## Read these first, in this order

1. `requirements.md` — v0.2, the current requirements. 23 requirements in EARS form.
2. `decisions.md` — D1–D17. Every settled decision, with what it commits us to and what
   it ruled out. Read this before proposing an approach; most of the obvious questions
   are already answered here, including the ones where the answer was "no".
3. `open-questions.md` — review history in sections A–E with per-item status, and the
   live open list in section F.

`requirements-v0.1-baseline.md` is the original intake document. Kept for reference only.
It contains known errors that v0.2 corrects. Do not work from it.

## How this project works

**Decisions go in `decisions.md`, not in chat.** A decision that only exists in a
conversation is lost. Each entry names the question it closed, what was decided, and the
consequences — including accepted risks. When a decision is made, write it down before
moving on.

**Requirements are EARS-shaped.** `THE X SHALL`, `WHEN … THE X SHALL`, `IF … THEN THE X
SHALL`. Match the existing style when adding to `requirements.md`.

**Do not quietly reopen settled decisions.** If one looks wrong, say so plainly and
explain why, then let it be decided. Don't route around it in the implementation.

**AWS facts get cited.** Service behaviour, IAM action names, API limits — link the AWS
doc. The v0.1 baseline failed review on two IAM errors that a citation would have caught.
Don't answer from memory on anything load-bearing.

## Technical constraints

- **Python.** boto3 for AWS. `moto` for tests — no live AWS account in CI.
- The platform this tool observes is built with Terraform and deployed with Ansible. This
  tool does not replace either. Drift covers operational state only, never what Terraform
  declares (D4).
- The browser UI is built in a **separate project** that is not ready for team work. Make
  no large UI design decisions here. The backend/frontend contract is open (F7) and blocks
  the HTTP layer, not the client layer.

## The write surface — treat as load-bearing

The tool performs exactly three AWS write actions: `CreateSnapshot`, `RegisterTargets`,
`DeregisterTargets`. Everything else is read-only.

This is enforced in two places and both must stay true:
- An explicit allowlist in the client layer that raises on any operation not in it, with a
  test asserting its contents (Requirement 20).
- An IAM policy held in this repo, scoped as described in Requirement 20.

Adding a fourth write action is a decision for `decisions.md`, not a commit.

## The risk to keep in mind

AWS accounts are **shared between clients**, separated by resource tag, and client
scoping is enforced in the application rather than in IAM (D13). So the tool's credentials
permit acting on any client's resources, and the application filter is the whole boundary.

That means the scoping filter is a single chokepoint every listing and every write passes
through, and the negative case gets a test: a request scoped to client A must not return
or act on a client B resource. This was raised, the risk was accepted, and it is recorded
in D13 with what it obliges in return.

## Not in scope here

This project publishes nothing to SharePoint. Documentation lives in this repo.
