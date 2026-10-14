# Requirements Document

**Status:** v0.1 baseline — imported as-is from the original intake document, pending review.
**Owner:** Robert Sinclair, SaaS Team
**Imported:** 2026-10-13

> This file is the *baseline*. Review findings and unresolved questions are tracked in
> `open-questions.md`. Do not treat anything here as settled until the matching question
> in that file is closed.

---

## Introduction

A Python web application that provides a browser-based interface for performing non-intrusive (read-only and safe operational) tasks in AWS. The application enables operations engineers and developers to quickly inspect AWS resources, view logs, check service health, and retrieve configuration details without requiring direct AWS Console access or CLI expertise. The app lives in the `aws-saas-and-drift` folder of the `roberts-ai-tools` repository.

## Glossary

- **Web_App**: The Python web application providing the browser-based interface for AWS tasks
- **User**: An authenticated person interacting with the Web_App through a browser
- **AWS_Client**: The backend component that communicates with AWS APIs using boto3
- **Task**: A discrete, non-intrusive operation performed against AWS (e.g., listing resources, viewing logs)
- **Task_Result**: The structured output returned to the User after a Task completes
- **Non_Intrusive_Task**: An AWS operation that does not create, modify, or delete resources (read-only or describe actions)
- **EBS_Snapshot**: A point-in-time backup of an EBS volume created via the CreateSnapshot API
- **Snapshot_Name_Format**: The naming convention applied to snapshots when created through the Web_App (placeholder — format to be defined later)
- **Log_Stream**: A sequence of log events from the same source within a CloudWatch log group
- **Target_Group**: An AWS Elastic Load Balancing resource that routes traffic to registered targets (instances, IPs, or Lambda functions)
- **Target_Health**: The health status of a registered target as reported by the target group's health checks
- **Defined_Config**: A server-side JSON file defining the expected state of AWS resources for configuration drift detection. This is optional per resource — only resources with a defined configuration are checked.
- **Config_Alarm**: A visual alert displayed when the live state of an AWS resource does not match its Defined_Config

## Requirements

### Requirement 1: Web Interface

**User Story:** As a developer, I want a web-based interface for AWS tasks, so that I can perform common AWS lookups without needing CLI access or navigating the AWS Console.

#### Acceptance Criteria

1. THE Web_App SHALL serve a browser-accessible interface on a configurable port
2. WHEN a User navigates to the root URL, THE Web_App SHALL display a dashboard listing all available Tasks
3. THE Web_App SHALL render Task_Results in a readable, structured format within the browser

### Requirement 2: AWS Authentication

**User Story:** As a developer, I want the application to securely authenticate with AWS, so that I can access resources in my AWS account.

#### Acceptance Criteria

1. THE AWS_Client SHALL authenticate using standard AWS credential sources (environment variables, shared credentials file, or IAM role)
2. WHEN AWS credentials are missing or invalid, THE Web_App SHALL display a clear error message indicating the authentication failure
3. THE Web_App SHALL support configuring the target AWS region

### Requirement 3: EC2 Instance Listing

**User Story:** As a developer, I want to list EC2 instances and manage snapshots of their attached volumes, so that I can quickly see what instances are running, their status, and create or review backups.

#### Acceptance Criteria

1. WHEN a User requests the EC2 instance list, THE AWS_Client SHALL retrieve all EC2 instances in the configured region
2. THE Web_App SHALL display instance ID, name tag, instance type, state, and private IP for each instance
3. WHEN no EC2 instances exist in the region, THE Web_App SHALL display an informational message indicating no instances were found
4. WHEN a User requests a snapshot of an instance's attached EBS volume, THE AWS_Client SHALL create an EBS_Snapshot using the CreateSnapshot API and apply the Snapshot_Name_Format as the Name tag (note: this is an explicit exception to the read-only guarantee — see Requirement 9)
5. WHEN a User views an instance's snapshots, THE AWS_Client SHALL retrieve all snapshots associated with that instance by matching snapshots whose volume ID corresponds to any currently attached volume of the instance
6. WHEN a User views an instance's snapshots, THE AWS_Client SHALL also retrieve snapshots whose Name tag matches the instance identifier or name, to capture snapshots from previously detached volumes
7. THE Web_App SHALL display all associated snapshots (from both volume ID linkage and Name tag matching) with snapshot ID, volume ID, start time, state, and description for each snapshot

### Requirement 4: CloudWatch Log Viewing

**User Story:** As a developer, I want to view recent CloudWatch log entries, so that I can troubleshoot application issues without switching to the AWS Console.

#### Acceptance Criteria

1. WHEN a User selects a log group, THE AWS_Client SHALL retrieve the most recent log streams for that group
2. WHEN a User selects a log stream, THE AWS_Client SHALL retrieve the most recent log events from that stream
3. THE Web_App SHALL display log events with their timestamp and message content
4. IF a log group or stream does not exist, THEN THE Web_App SHALL display an error message indicating the resource was not found

### Requirement 5: S3 Bucket Listing

**User Story:** As a developer, I want to list S3 buckets and their contents, so that I can quickly check what storage resources exist.

#### Acceptance Criteria

1. WHEN a User requests the S3 bucket list, THE AWS_Client SHALL retrieve all S3 buckets accessible to the authenticated credentials
2. WHEN a User selects a bucket, THE AWS_Client SHALL list the top-level objects and prefixes in that bucket
3. THE Web_App SHALL display bucket names, creation dates, and object counts where available
4. IF a bucket access is denied, THEN THE Web_App SHALL display a permission error for that specific bucket

### Requirement 6: Lambda Function Listing

**User Story:** As a developer, I want to view Lambda functions, their configurations, and recent invocation logs, so that I can check deployment status, runtime settings, and troubleshoot issues.

#### Acceptance Criteria

1. WHEN a User requests the Lambda function list, THE AWS_Client SHALL retrieve all Lambda functions in the configured region
2. THE Web_App SHALL display function name, runtime, memory size, timeout, and last modified date for each function
3. WHEN no Lambda functions exist in the region, THE Web_App SHALL display an informational message indicating no functions were found
4. WHEN a User requests logs for a selected Lambda function, THE AWS_Client SHALL retrieve the most recent Log_Stream from the /aws/lambda/<function-name> CloudWatch log group
5. WHEN the latest Log_Stream is retrieved, THE AWS_Client SHALL fetch all log events from that stream and THE Web_App SHALL display them with timestamp and message content inline or in a detail view
6. IF the CloudWatch log group for a Lambda function does not exist, THEN THE Web_App SHALL display a message indicating no logs are available for that function

### Requirement 7: RDS Instance Status

**User Story:** As a developer, I want to check RDS instance status, so that I can verify database availability and configuration.

#### Acceptance Criteria

1. WHEN a User requests the RDS instance list, THE AWS_Client SHALL retrieve all RDS instances in the configured region
2. THE Web_App SHALL display instance identifier, engine, status, endpoint, and allocated storage for each RDS instance
3. WHEN no RDS instances exist in the region, THE Web_App SHALL display an informational message indicating no instances were found

### Requirement 8: Error Handling

**User Story:** As a developer, I want clear error feedback when AWS operations fail, so that I can understand what went wrong and take corrective action.

#### Acceptance Criteria

1. IF an AWS API call fails due to a network error, THEN THE Web_App SHALL display a connectivity error message with the service name
2. IF an AWS API call fails due to insufficient permissions, THEN THE Web_App SHALL display the required permission and the denied action
3. IF an AWS API call exceeds a 30-second timeout, THEN THE Web_App SHALL cancel the request and display a timeout message
4. THE Web_App SHALL log all errors with timestamps and request details to a local log file

### Requirement 9: Non-Intrusive Operation Guarantee

**User Story:** As an operations engineer, I want assurance that the tool only performs read-only operations (with a defined exception for non-destructive snapshot creation), so that I can use it safely in production environments.

#### Acceptance Criteria

1. THE AWS_Client SHALL only invoke AWS API actions that are classified as read-only (Describe, List, Get operations), with the sole exceptions of the CreateSnapshot action for EBS volumes, and the RegisterTargets and DeregisterTargets actions for Elastic Load Balancing target groups
2. THE Web_App SHALL not expose any interface elements that could trigger resource creation, modification, or deletion, except for the explicit EBS snapshot creation action described in Requirement 3 and the target registration/deregistration actions described in Requirement 10
3. THE AWS_Client SHALL use an IAM policy reference that restricts permissions to read-only actions plus the ec2:CreateSnapshot, ec2:CreateTags, elasticloadbalancingv2:RegisterTargets, and elasticloadbalancingv2:DeregisterTargets permissions
4. THE Web_App SHALL clearly label the snapshot creation action and the target register/deregister actions as write operations to inform the User that they modify resources, distinguishing them from all other read-only Tasks
5. Reading and writing the local Defined_Config JSON file is a local filesystem operation and does not constitute an AWS API call; THE Web_App SHALL treat Defined_Config file access as outside the scope of the non-intrusive AWS operation guarantee


### Requirement 10: Target Group Management

**User Story:** As an operations engineer, I want to view target groups and manage their registered targets, so that I can inspect load balancer routing and move traffic between healthy instances during operational events.

#### Acceptance Criteria

1. WHEN a User requests the target group list, THE AWS_Client SHALL retrieve all target groups in the configured region
2. THE Web_App SHALL display target group name, ARN, protocol, port, VPC, and target type for each Target_Group
3. WHEN a User selects a Target_Group, THE AWS_Client SHALL retrieve all registered targets with their Target_Health status (healthy, unhealthy, draining, unused, initial, or unavailable)
4. WHEN a User views a Target_Group's details, THE AWS_Client SHALL retrieve target group attributes including stickiness settings, deregistration delay, and health check configuration
5. WHEN a User initiates a register target action, THE AWS_Client SHALL retrieve all EC2 instances in the configured region and THE Web_App SHALL present them for selection
6. WHEN a User confirms registration of a selected instance, THE AWS_Client SHALL invoke the RegisterTargets API to add the instance to the Target_Group (note: this is an explicit exception to the read-only guarantee — see Requirement 9)
7. WHEN a User initiates a deregister target action, THE Web_App SHALL present the currently registered targets of the Target_Group for selection
8. WHEN a User confirms deregistration of a selected target, THE AWS_Client SHALL invoke the DeregisterTargets API to remove the target from the Target_Group (note: this is an explicit exception to the read-only guarantee — see Requirement 9)
9. THE Web_App SHALL require a confirmation step before executing register or deregister actions
10. THE Web_App SHALL clearly label the register and deregister actions as write operations to inform the User that they modify the Target_Group's routing configuration
11. WHEN no target groups exist in the region, THE Web_App SHALL display an informational message indicating no target groups were found

### Requirement 11: Defined Configuration and Config Alarms

**User Story:** As an operations engineer, I want to optionally define the expected state of individual AWS resources and be alerted when their live state drifts from that configuration, so that I can quickly detect unintended changes on the specific resources I choose to monitor.

#### Acceptance Criteria

1. THE Web_App SHALL store the Defined_Config as a JSON file on the Web_App's filesystem within the project directory, suitable for version control
2. THE Web_App SHALL provide a UI for Users to create and edit the Defined_Config for individual resources of each supported resource type (Target Groups, EC2, RDS, Lambda, S3)
3. WHEN a User defines a Defined_Config for a Target_Group, THE Web_App SHALL allow specifying the expected healthy target count and the expected list of registered target instance IDs for that Target_Group
4. WHEN a User defines a Defined_Config for an EC2 instance, THE Web_App SHALL allow specifying expected instances (by instance ID or Name tag) that should be in the "running" state
5. WHEN a User defines a Defined_Config for an RDS instance, THE Web_App SHALL allow specifying expected RDS instances that should be in the "available" state
6. WHEN a User defines a Defined_Config for a Lambda function, THE Web_App SHALL allow specifying expected functions and their expected configurations (runtime, memory, timeout)
7. WHEN a User defines a Defined_Config for an S3 bucket, THE Web_App SHALL allow specifying expected buckets that should exist and be accessible
8. WHEN a resource listing page is loaded and a Defined_Config exists for individual resources of that type, THE Web_App SHALL compare the live AWS state against the Defined_Config only for those resources that have a defined configuration
9. WHEN the live state of a resource does not match its Defined_Config, THE Web_App SHALL display a prominent Config_Alarm indicator (e.g., red/warning styling) next to that resource
10. THE Config_Alarm indicator SHALL clearly state what is out of spec (e.g., "Instance i-abc123 expected running, actual stopped")
11. WHEN fewer targets are healthy than the expected count in the Defined_Config for a Target_Group, THE Web_App SHALL display a Config_Alarm for that Target_Group
12. WHEN any expected instance ID has been removed from a Target_Group's registered targets, THE Web_App SHALL display a Config_Alarm for that Target_Group
13. WHEN an expected EC2 instance is not in the "running" state, THE Web_App SHALL display a Config_Alarm for that instance
14. WHEN an expected RDS instance is in a non-available state (stopped, maintenance, or other), THE Web_App SHALL display a Config_Alarm for that instance
15. WHEN a Lambda function's live configuration (runtime, memory, or timeout) differs from the expected configuration in the Defined_Config, THE Web_App SHALL display a Config_Alarm for that function
16. WHEN an expected S3 bucket is not found or not accessible, THE Web_App SHALL display a Config_Alarm for that bucket
17. WHEN no Defined_Config has been set for a resource, THE Web_App SHALL display that resource normally with no alarm checks
