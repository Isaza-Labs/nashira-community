# Security incident response

## Purpose

This procedure governs suspected or confirmed security events affecting Nashira source code, releases, build systems, credentials, hosted project services or user data controlled by Isaza Labs LLC. It is distinct from the public defect register and from ordinary operational troubleshooting.

The procedure follows the lifecycle of preparation, detection, analysis, containment, eradication, recovery and improvement. It is designed to align with the risk-management approach in NIST SP 800-61 Rev. 3 without claiming certification or compliance.

## Reporting and records

- External reporters use the private route in [SECURITY.md](../../SECURITY.md).
- Team members immediately notify the incident lead through an approved private channel.
- Do not place secrets, exploit details, personal data or customer data in public issues.
- Maintain one private incident record with an identifier, owner, severity, timeline, evidence locations, decisions and actions.
- Preserve original evidence read-only when feasible and record hashes for material files.

## Severity

| Level | Description | Examples |
|---|---|---|
| SEV-1 Critical | Active exploitation or material compromise with broad impact | tenant-boundary bypass, signing-key compromise, remote execution with production reach |
| SEV-2 High | Exploitable weakness or contained compromise with significant impact | credential disclosure, authorization bypass, malicious workflow execution within a tenant |
| SEV-3 Medium | Limited exposure requiring correction | security control weakness with substantial prerequisites or constrained impact |
| SEV-4 Low | Hardening or defense-in-depth issue | non-sensitive information leak, low-impact misconfiguration |

Severity can change as facts develop. The incident lead records the reason for each change.

## Roles

- **Incident lead:** owns severity, decisions, coordination and closure.
- **Technical lead:** investigates, contains, removes the cause and validates recovery.
- **Recorder:** maintains the timeline, evidence and decision log.
- **Communications owner:** coordinates reporter, customer and public communications.
- **Legal and privacy reviewer:** assesses notification, contractual and regulatory duties when applicable.

One person can hold more than one role in a small team, but the incident record must state who performed each function. A primary and backup person for every role remain to be assigned.

## Response procedure

### 1 Triage

1. Acknowledge receipt privately.
2. Protect the report and remove secrets from routine collaboration systems.
3. Determine affected assets, versions, tenants and data.
4. Assess exploitability, current activity and safety of continued operation.
5. Assign severity, incident lead and technical lead.

### 2 Containment

Choose the least disruptive action that reduces immediate risk. Options include revoking credentials, disabling a vulnerable integration or feature, blocking a route, isolating a workload, pausing releases, rotating signing material or taking an affected service offline.

Record what was changed, by whom, when, why and how it can be reversed. Preserve evidence before destructive cleanup when safety allows.

### 3 Eradication and correction

- identify the root cause and affected code or configuration;
- remove unauthorized access and persistence;
- rotate exposed secrets and invalidate sessions;
- patch the issue and add a regression test;
- review similar components and historical logs for the same weakness;
- prepare release and deployment instructions.

### 4 Recovery

- validate the fix in an isolated environment;
- verify authentication, authorization, tenant isolation, audit integrity and affected workflows;
- deploy in controlled stages when possible;
- monitor for recurrence and secondary effects;
- confirm that temporary containment can be removed safely.

### 5 Communications and notification

Only the communications owner or an authorized company representative issues external statements. Communications must distinguish confirmed facts, current uncertainty, user actions and the next update.

Legal and privacy review determines whether a regulator, customer, insurer, law-enforcement body or affected person must be notified and under what deadline. No universal notification period is asserted by this repository policy.

### 6 Closure and learning

Close an incident only after containment, corrective action, recovery validation, notification decisions and ownership of follow-up actions are documented. Produce a blameless post-incident review for SEV-1 and SEV-2 incidents and for repeated lower-severity incidents.

The review records impact, detection, timeline, contributing conditions, what worked, what failed and dated corrective actions. Public advisories must omit operational details that would create further risk.

## Repository operating model

- GitHub private vulnerability reporting is the official intake channel.
- The receiving repository security manager or administrator acts as incident lead until another lead is assigned.
- A private GitHub security advisory is the default coordination record for a repository vulnerability. Operational incidents use an access-controlled company record.
- The incident lead assigns the technical, recording and communications functions and adds a second administrator when one is available.
- Evidence is retained without destructive alteration through investigation, remediation, disclosure and any legal or privacy review.
- The acknowledgement and assessment targets in `SECURITY.md` apply to external repository reports.
- The release owner can authorize an emergency patch after security validation and must document any normal release control that was abbreviated.
- Notification duties are assessed for the affected deployment, data, customers and jurisdictions; they are not inferred solely from severity.

Operators of independent self-managed deployments remain responsible for their own incident response, legal duties and recovery unless a separate written support agreement assigns responsibility to Isaza Labs LLC.
