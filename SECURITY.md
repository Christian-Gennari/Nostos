# Security Policy

This policy covers the public Nostos code, the SelfHosted application, and the
official Nostos Cloud service.

## Supported versions

Security fixes are delivered through the current code on `main` and updated
official SelfHosted images. If you use an older release or image, upgrade to
receive fixes; older versions do not receive security backports.

We also accept reports affecting the current official Nostos Cloud service.

## Reporting a vulnerability

Please [report vulnerabilities privately through GitHub](https://github.com/Christian-Gennari/Nostos/security/advisories/new).
A GitHub account is required to submit a report.

If you cannot use GitHub private reporting, email
[security@nostos.page](mailto:security@nostos.page).

Do not publish vulnerability details in public issues, discussions, or pull
requests. Ordinary bugs and feature requests can use
[GitHub Issues](https://github.com/Christian-Gennari/Nostos/issues).

Include what you can:

- The affected release, commit, or image tag/digest, and whether the issue affects
  SelfHosted or Cloud.
- A description of the issue, its impact, and any prerequisites.
- Reproduction steps and a minimal proof of concept using synthetic data.
- Relevant redacted logs or screenshots.

Do not include passwords, tokens, private keys, or customer data. If evidence
contains sensitive information, describe it first so we can agree on how to
handle it safely.

## Response and disclosure

We aim to acknowledge security reports within five business days. After triage,
we will discuss next steps, progress updates, and coordinated disclosure timing
with you. Repair timing depends on the issue; we do not promise a fixed deadline.

Please coordinate public disclosure with us so affected users can receive fixes
or mitigations. We can acknowledge your contribution with your consent. This
policy makes no commitment to financial rewards.

## Safe testing

Prefer a local SelfHosted installation with synthetic data. For Cloud, limit any
testing to accounts and data you control. This policy does not grant blanket
authorization to test production or third-party systems.

- Do not access or modify other users' accounts or data.
- Do not disrupt the service, perform denial-of-service testing, or extract data
  in bulk.
- Do not use social engineering against users, maintainers, or service providers.
- If you unexpectedly encounter another user's data, stop testing and report the
  issue privately without copying or sharing that data.

When unsure whether a test is appropriate, contact us through the private
reporting channel before proceeding.
