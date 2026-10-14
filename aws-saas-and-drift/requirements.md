# Requirements Document — v0.2

**Owner:** Robert Sinclair, SaaS Team
**Status:** Draft. Incorporates decisions D1–D17. Open items listed in `open-questions.md`.
**Supersedes:** `requirements-v0.1-baseline.md`
**Updated:** 2026-10-14

---

## Introduction

A Python web application providing a browser-based interface for inspecting AWS resources
across multiple accounts, and for detecting drift between a declared operational state and
what is actually running.

Operations engineers and developers use it to check resource state, read logs, inspect
load balancer routing and certificate expiry, and take two bounded write actions — create
an EBS snapshot, and register or deregister a load balancer target — without AWS Console
access or CLI expertise.

The app lives in `aws-saas-and-drift/` in the `roberts-ai-tools` repository. It runs on a
long-lived VM with persistent disk. The browser UI is built in a separate project; this
document specifies backend behaviour and leaves UI design to that project.

## Glossary

- **Web_App** — the application providing the interface and the scheduled evaluator.
- **AWS_Client** — the backend component that communicates with AWS via boto3.
- **Account_Context** — the `(account, region)` pair an AWS call is made against. Every
  call carries one. Global services are a distinct case; see Requirement 2.
- **Actor** — the identity performing an action. Established by the UI project; required
  by the backend on every write.
- **Client_Tag** — the resource tag carrying client identity. Shared AWS accounts hold
  more than one client's resources; this tag is how they are told apart.
- **Client_Scope** — the client currently selected, which filters every listing.
- **Task** — a discrete operation performed against AWS.
- **Write_Action** — one of the three operations that change AWS state: `CreateSnapshot`,
  `RegisterTargets`, `DeregisterTargets`. Everything else is read-only.
- **Snapshot_Set** — the group of EBS snapshots produced by one snapshot action against
  one instance, covering all its attached volumes, sharing a set ID tag.
- **Drift_Rule** — a declared expectation about the operational state of one resource.
- **Drift_Finding** — a live mismatch between a Drift_Rule and actual state.
- **Suppression** — a maintenance window or an expiring acknowledgement that stops a
  Drift_Finding alerting.
- **Audit_Record** — an append-only record of one Write_Action or configuration change.

---

## Requirement 1: Web interface

**User story:** As a developer, I want a browser interface for AWS tasks, so that I can
perform common lookups without CLI access or the Console.

1. THE Web_App SHALL serve a browser-accessible interface on a configurable port.
2. WHEN a User navigates to the root URL, THE Web_App SHALL display a dashboard listing
   available Tasks.
3. THE Web_App SHALL render results in a readable, structured format.
4. THE Web_App SHALL clearly distinguish Write_Actions from read-only Tasks in every place
   they are offered.

> UI design is owned by the separate UI project. The backend/frontend contract — server
> rendered or JSON API — is unresolved; see section D of `open-questions.md`.

## Requirement 2: AWS authentication and account context

**User story:** As an operations engineer, I want the tool to reach every account our
clients run in, so that one tool covers the whole estate.

1. THE AWS_Client SHALL obtain base credentials from the standard boto3 chain, preferring
   the VM's instance profile.
2. THE AWS_Client SHALL maintain a registry of target accounts, each with a role ARN to
   assume and the regions it operates in.
3. WHEN a Task is performed, THE AWS_Client SHALL assume the registered role for the
   selected account via `sts:AssumeRole` and use the resulting credentials.
4. THE AWS_Client SHALL cache assumed credentials per role and refresh them before STS
   expiry.
5. THE AWS_Client SHALL classify each AWS service as regional, global, or
   regional-with-a-fixed-home, and SHALL NOT assume a region applies to every call.
6. THE AWS_Client SHALL treat Route 53 as global.
7. THE AWS_Client SHALL retrieve ACM certificates from `us-east-1` in addition to the
   selected region, because certificates used by CloudFront must reside there and would
   otherwise be missed.
8. IF credentials are missing, invalid, or the role cannot be assumed, THEN THE Web_App
   SHALL display an error naming the account and the failure.

## Requirement 3: Actor identity and authorization

**User story:** As an operations engineer, I want every change attributable to a person, so
that the audit log is worth keeping.

1. THE Web_App SHALL require an Actor on every Write_Action and every configuration change.
2. IF no Actor is supplied, THEN THE Web_App SHALL refuse the action.
3. THE Web_App SHALL route every Write_Action through a single authorization checkpoint,
   with a permissive default implementation, so that the UI project's identity model can be
   applied in one place.

> How a person authenticates, and whether a viewer/operator role split exists, is owned by
> the UI project (D2). Until it lands, anything that can reach the backend can trigger a
> Write_Action using the tool's AWS credentials. Recorded, not solved.

## Requirement 4: Client scoping

**User story:** As an operations engineer working in a shared account, I want to see one
client's resources at a time, so that I act on the right thing.

1. THE Web_App SHALL filter resource listings by Client_Scope, matched on the Client_Tag.
2. THE Web_App SHALL apply that filter at a single chokepoint through which every listing
   and every Write_Action passes.
3. THE Web_App SHALL provide a view of resources that carry no Client_Tag.
4. THE Web_App SHALL permit Write_Actions against untagged resources, and SHALL record the
   absence of a Client_Tag explicitly in the Audit_Record rather than leaving it blank.
5. THE AWS_Client SHOULD use the Resource Groups Tagging API for cross-service tag queries,
   because tag filtering support varies by service and per-resource tag lookups do not
   scale under scheduled evaluation.
6. THE Web_App SHALL NOT rely on tag queries alone to enumerate resources, because
   `GetResources` does not return untagged resources.

> Enforcement is application-side only (D13). The accepted risk and what it obliges are
> recorded there.

## Requirement 5: EC2 instance listing

1. WHEN a User requests the EC2 instance list, THE AWS_Client SHALL retrieve all instances
   in the Account_Context, paginating to completion.
2. THE Web_App SHALL display instance ID, Name tag, instance type, state, private IP, and
   Client_Tag for each instance.
3. WHEN no instances match, THE Web_App SHALL display an informational message.

## Requirement 6: EBS snapshots

**User story:** As an operations engineer, I want to snapshot an instance before I change
it, and find the snapshots again afterwards.

1. WHEN a User requests a snapshot of an instance, THE AWS_Client SHALL create one
   EBS snapshot per currently attached volume, as a single Snapshot_Set.
2. THE AWS_Client SHALL tag each snapshot with: a set ID shared across the Snapshot_Set,
   `SourceInstanceId`, the client and environment cost allocation tags, and an expiry date
   defaulting to 30 days out.
3. THE Web_App SHALL allow the User to override the expiry at creation time.
4. IF some volumes in a Snapshot_Set fail to snapshot, THEN THE Web_App SHALL report which
   succeeded and which did not, and SHALL mark the set as incomplete.
5. WHEN a User views an instance's snapshots, THE AWS_Client SHALL match on exact
   `SourceInstanceId`, and separately on exact Name tag for snapshots not created by this
   tool.
6. THE Web_App SHALL label Name-tag matches as "matched by name, unverified".
7. THE AWS_Client SHALL NOT use substring matching to associate snapshots with instances.
8. THE Web_App SHALL display snapshot ID, volume ID, set membership, start time, state,
   description, and expiry date.
9. THE AWS_Client SHALL NOT delete snapshots. Expiry is enforced by a separate platform
   job (D10).

## Requirement 7: CloudWatch log viewing

1. WHEN a User selects a log group, THE AWS_Client SHALL retrieve its most recent log
   streams.
2. WHEN a User selects a log stream, THE AWS_Client SHALL retrieve the most recent log
   events, bounded by a configurable event count or time window, newest first.
3. THE Web_App SHALL provide explicit paging for older events rather than fetching a whole
   stream.
4. THE Web_App SHALL display log events with timestamp and message.
5. IF a log group or stream does not exist, THEN THE Web_App SHALL say so.

## Requirement 8: S3 bucket listing

1. WHEN a User requests the bucket list, THE AWS_Client SHALL retrieve all accessible
   buckets.
2. WHEN a User selects a bucket, THE AWS_Client SHALL list top-level objects and prefixes,
   capped at a configurable limit, and SHALL state when the listing was truncated.
3. THE Web_App SHALL source object counts from the CloudWatch `NumberOfObjects` metric and
   display the metric's timestamp alongside it, because S3 provides no cheap live count.
4. IF access to a bucket is denied, THEN THE Web_App SHALL display a permission error for
   that bucket without failing the whole listing.

## Requirement 9: Lambda function listing

1. WHEN a User requests the function list, THE AWS_Client SHALL retrieve all functions in
   the Account_Context, paginating to completion.
2. THE Web_App SHALL display function name, runtime, memory size, timeout, and last
   modified date.
3. WHEN a User requests logs for a function, THE AWS_Client SHALL retrieve the most recent
   log stream from `/aws/lambda/<function-name>` and fetch events subject to the bounds in
   Requirement 7.
4. IF the log group does not exist, THEN THE Web_App SHALL indicate no logs are available.

## Requirement 10: RDS instance status

1. WHEN a User requests the RDS instance list, THE AWS_Client SHALL retrieve all instances
   in the Account_Context, paginating to completion.
2. THE Web_App SHALL display identifier, engine, status, endpoint, and allocated storage.
3. WHEN no instances match, THE Web_App SHALL display an informational message.

## Requirement 11: Target groups

**User story:** As an operations engineer, I want to inspect load balancer routing and move
traffic between instances during an operational event.

1. WHEN a User requests the target group list, THE AWS_Client SHALL retrieve all target
   groups in the Account_Context.
2. THE Web_App SHALL display name, ARN, protocol, port, VPC, and target type.
3. WHEN a User selects a target group, THE AWS_Client SHALL retrieve its registered targets
   with health status, and its attributes including stickiness, deregistration delay, and
   health check configuration.
4. WHEN a User initiates registration, THE AWS_Client SHALL retrieve candidate EC2
   instances for selection.
5. WHEN a User confirms registration, THE AWS_Client SHALL invoke `RegisterTargets`.
6. WHEN a User confirms deregistration, THE AWS_Client SHALL invoke `DeregisterTargets`.
7. THE Web_App SHALL require explicit confirmation before either action.
8. THE Web_App SHALL refuse a deregistration that would reduce the count of healthy targets
   below the expected healthy count declared in the target group's Drift_Rule, and SHALL
   explain the refusal.
9. IF no Drift_Rule declares an expected healthy count, THEN THE Web_App SHALL warn before
   deregistering the last healthy target.
10. WHEN no target groups match, THE Web_App SHALL display an informational message.

## Requirement 12: ACM certificates

1. WHEN a User requests the certificate list, THE AWS_Client SHALL retrieve certificates
   from the selected region and from `us-east-1`.
2. THE Web_App SHALL display domain name, status, expiry date, days remaining, renewal
   eligibility, and the region the certificate lives in.

## Requirement 13: Route 53

1. WHEN a User requests hosted zones, THE AWS_Client SHALL retrieve them without a region.
2. WHEN a User selects a hosted zone, THE AWS_Client SHALL list its record sets,
   paginating to completion.

## Requirement 14: Drift rules and their storage

**User story:** As an operations engineer, I want to declare what operational state should
look like, so that I am told when it changes.

1. THE Web_App SHALL store Drift_Rules in a SQLite database on the VM's persistent disk.
2. THE Web_App SHALL version the database schema and apply changes through migrations.
3. THE Web_App SHALL provide an interface for creating and editing Drift_Rules.
4. THE Web_App SHALL record an Actor, a timestamp, and the prior value for every change to
   a Drift_Rule.
5. THE Web_App SHALL reject an edit based on a stale read rather than overwriting a
   concurrent change.
6. Drift_Rules SHALL be optional per resource. A resource with no rule is displayed with no
   drift checks.
7. THE Web_App SHALL support these rule types:
   - target group: expected healthy target count, expected registered instance IDs
   - EC2 instance: expected to be in `running` state
   - RDS instance: expected to be in `available` state
   - ACM certificate: expected to be more than N days from expiry
   - resource tagging: expected to carry a Client_Tag
8. THE Web_App SHALL NOT provide rule types for state that Terraform declares, including
   Lambda runtime, memory and timeout, and S3 bucket existence (D4).
9. Reading and writing the Drift_Rule store is a local operation and is outside the AWS
   write guarantee in Requirement 19.

## Requirement 15: Drift evaluation and alerting

1. THE Web_App SHALL evaluate Drift_Rules on a schedule, independently of any browser
   session.
2. THE Web_App SHALL persist each Drift_Finding with first-seen and last-seen timestamps.
3. WHEN a resource page is loaded, THE Web_App SHALL display the most recent stored
   evaluation rather than calling AWS on render.
4. THE Web_App SHALL indicate the age of the evaluation being displayed.
5. WHEN a Drift_Finding is raised and is not suppressed, THE Web_App SHALL raise an
   OpsGenie alert carrying a dedup key stable across evaluations of the same rule.
6. THE Drift_Finding SHALL state what is out of spec in concrete terms, naming the
   resource, the expectation, and the actual value.
7. THE Web_App SHALL carry the Client_Scope on each Drift_Finding.

> Evaluation interval per rule type, alert priority, and whether a resolved finding
> auto-closes its OpsGenie alert are open — E1 and E2 in `open-questions.md`.

## Requirement 16: Suppression

1. THE Web_App SHALL support maintenance windows declared in advance against a resource,
   during which its Drift_Rules do not alert.
2. THE Web_App SHALL support acknowledging a live Drift_Finding for a set duration.
3. Every acknowledgement SHALL expire. THE Web_App SHALL NOT offer a permanent suppression.
4. THE Web_App SHALL compute suppression state at evaluation time rather than storing it as
   a flag, because windows and acknowledgements can overlap.
5. THE Web_App SHALL display suppressed findings marked as suppressed, and SHALL NOT hide
   them.
6. THE Web_App SHALL write an Audit_Record for every suppression, naming the Actor, the
   scope, and the expiry.

## Requirement 17: Audit log

**User story:** As an operations engineer, I want every change this tool made recorded, so
that an incident review can reconstruct what happened.

1. THE Web_App SHALL write an Audit_Record for every Write_Action, every Drift_Rule change,
   and every suppression.
2. Each Audit_Record SHALL carry: Actor, timestamp, action, Account_Context, target
   resource identifier, Client_Scope in force, parameters, and outcome.
3. THE Web_App SHALL write the Audit_Record for an attempted action whether it succeeded or
   failed.
4. THE Audit_Record store SHALL be append-only. THE Web_App SHALL NOT offer an interface
   that edits or deletes Audit_Records.

## Requirement 18: Error handling

1. IF an AWS call fails on a network error, THEN THE Web_App SHALL display a connectivity
   error naming the service and the Account_Context.
2. IF an AWS call fails on permissions, THEN THE Web_App SHALL display the denied action and
   the identity that was denied.
3. IF an AWS call exceeds a configurable timeout, THEN THE Web_App SHALL cancel it and
   display a timeout message.
4. THE Web_App SHALL log errors with timestamps and request details.
5. THE Web_App SHALL NOT fail a whole listing because one resource within it errored.

## Requirement 19: Pagination, throttling and caching

1. THE AWS_Client SHALL paginate every listing to completion, or state plainly that the
   result is truncated. It SHALL NOT return a silently partial result.
2. THE AWS_Client SHALL retry throttled calls with exponential backoff and jitter.
3. THE Web_App SHALL cache listings for a short configurable TTL and offer explicit refresh.
4. THE scheduled evaluator SHALL be rate-limited so that it cannot starve interactive use.

## Requirement 20: Write action guarantee

**User story:** As an operations engineer, I want the tool's write surface to be small and
provable, so that I can run it against Production.

1. THE AWS_Client SHALL permit only read-only AWS operations plus `CreateSnapshot`,
   `CreateTags` scoped to snapshot creation, `RegisterTargets`, and `DeregisterTargets`.
2. THE AWS_Client SHALL enforce this with an explicit allowlist of permitted operations,
   raising on any operation not in it.
3. A test SHALL assert the contents of that allowlist, so that widening the write surface
   requires changing a test.
4. THE IAM policy SHALL use the `elasticloadbalancing` service prefix for target
   registration actions. `elasticloadbalancingv2` is not an IAM service prefix.
5. THE IAM policy SHALL scope `ec2:CreateTags` to `snapshot/*` with an
   `ec2:CreateAction: CreateSnapshot` condition, so that tagging is possible only in the
   context of creating a snapshot.
6. THE IAM policy SHALL be held in the repository as a reviewable artifact.
7. THE Web_App SHALL label the three Write_Actions as write operations wherever they appear.
8. Deregistration is operationally more severe than snapshot creation and carries the
   additional guard in Requirement 11.8.

## Requirement 21: Secrets

1. THE Web_App SHALL read secrets, including the OpsGenie API key, from AWS Secrets Manager.
2. THE IAM policy SHALL scope `secretsmanager:GetSecretValue` to specific secret ARNs.
3. THE Web_App SHALL fetch secrets using the VM's own identity, not an assumed per-client
   role.
4. IF a secret cannot be read at startup, THEN THE Web_App SHALL fail with a clear error
   rather than starting with alerting silently disabled.

## Requirement 22: Durability

1. THE SQLite database SHALL be backed up off the VM on a schedule. Persistent disk is not
   backup, and the database holds the audit log.
2. A restore SHALL be tested, not assumed.

## Requirement 23: Testing

1. AWS interactions SHALL be tested without a live AWS account, using `moto`.
2. Tests SHALL cover the negative client-scoping case: a request scoped to one client must
   not return or act on another client's resources.
3. Tests SHALL cover partial failure of a Snapshot_Set.
4. Tests SHALL assert the Requirement 20 allowlist.
