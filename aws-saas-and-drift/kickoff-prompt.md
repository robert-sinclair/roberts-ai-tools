# Kickoff prompt

Paste this into the first session in this repository. Delete this file once used.

---

I'm starting work on `aws-saas-and-drift`, a Python web application for inspecting AWS
resources across multiple accounts and detecting operational drift. Requirements gathering
is done; no code exists yet.

Read these before doing anything, in this order:

1. `CLAUDE.md` — how this project works.
2. `requirements.md` — v0.2, 23 requirements in EARS form. This is the spec.
3. `decisions.md` — D1 through D17. Every settled decision with its consequences and the
   risks that were accepted knowingly.
4. `open-questions.md` — section F is the live open list. Sections A–E are the review
   history with per-item status; read them for context on why things are the way they are,
   not as open work.

Ignore `requirements-v0.1-baseline.md` except as history. It contains two IAM errors and a
self-contradictory storage model that v0.2 corrects.

## Context you need

The platform this tool observes is built with Terraform and deployed with Ansible. Python
is our primary language. I'm a DevOps / Cloud Solutions Architect at Bravura Security — a
cybersecurity company working in PAM and IGA — so the security properties of this tool
matter more than they would elsewhere, and it will be reviewed on them.

The browser UI is being built in a separate project that isn't ready for team work. Don't
make large UI design decisions. The backend/frontend contract (F7) is open and blocks the
HTTP layer, not the client layer.

## What I want from you

Start with the AWS client layer. It's fully specified and nothing in section F blocks it.
Specifically:

- The account registry and `sts:AssumeRole` session management, cached per role and
  refreshed before STS expiry (Requirement 2).
- Service classification — global, regional, regional-with-a-fixed-home — so Route 53 and
  CloudFront certificates in `us-east-1` aren't missed (Requirement 2.5–2.7).
- The operation allowlist that raises on anything outside the three permitted write
  actions, plus the test asserting its contents (Requirement 20.1–20.3).
- The client-scoping chokepoint every listing and every write passes through, with the
  negative test: a request scoped to client A must not return or act on a client B
  resource (Requirement 4, and read D13 first).
- Pagination, backoff and caching as cross-cutting behaviour, not per-call (Requirement 19).

Propose a module layout and the dependency set before writing much code. I'd rather review
the shape than a finished thing I disagree with.

## How to work

Write decisions into `decisions.md` as they're made — a decision that only exists in our
conversation is lost. Keep requirements in EARS form when you add to them. Cite the AWS
docs for anything load-bearing about service behaviour, IAM action names or API limits;
the v0.1 baseline failed review on two IAM errors that a citation would have caught.

If a settled decision looks wrong to you, say so directly and tell me why. Don't route
around it in the implementation.

Tell me what's weak and why. Don't rewrite my voice out of anything I've written.
