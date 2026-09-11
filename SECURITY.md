# Security policy

Isaza Labs LLC welcomes responsible reports that help protect Nashira and its users.

## Reporting a vulnerability

Do not open a public issue, discussion or pull request for a suspected vulnerability, exposed secret or active security incident.

Use GitHub's private vulnerability reporting feature for this repository:

1. Open the repository's **Security** page.
2. Select **Advisories**.
3. Select **Report a vulnerability**.

If that option is unavailable, contact a repository maintainer through a private channel published in the owning GitHub organization's profile. Do not include vulnerability details in a public message; use the initial contact only to request a private reporting channel.

Include, when available:

- the affected version, commit or deployment mode;
- a concise description and potential impact;
- reproducible steps or a minimal proof of concept;
- relevant logs with tokens, credentials, personal data and customer data removed;
- any known exploitation or public disclosure;
- a safe way to contact the reporter.

## What belongs in a security report

Examples include authentication or authorization bypass, tenant isolation failures, secret disclosure, unsafe workflow execution, command or code injection, audit-chain integrity failures, vulnerable dependencies with a reachable attack path, and material weaknesses in an integration owned by Nashira.

Ordinary defects, documentation errors and feature requests belong in the public issue tracker. Problems in third-party services should be reported to their owners unless Nashira's implementation creates the exposure.

## Response targets

These are operating targets, not contractual service levels:

- acknowledge a complete report within three business days;
- provide an initial assessment or request for missing information within seven business days;
- provide periodic updates while a validated issue remains under active remediation;
- coordinate disclosure after a fix or mitigation is available, considering user risk.

Complex reports, incomplete evidence and dependencies on third parties can require more time.

## Coordinated disclosure

Please allow reasonable time to investigate and remediate before public disclosure. Do not access data that is not yours, degrade service, use social engineering, persist in a system, or exceed the minimum testing needed to demonstrate the issue.

Isaza Labs LLC will evaluate good-faith research performed within these limits and will not deliberately mischaracterize it as malicious. This statement does not authorize testing against systems or data owned by customers or third parties and is not a waiver of applicable law.

## Supported versions

The latest published Nashira release receives security fixes. The active development branch is reviewed on a best-effort basis and is not a supported production release. Older releases are unsupported unless a written support agreement states otherwise.
