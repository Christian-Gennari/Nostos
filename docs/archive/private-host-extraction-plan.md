# Public product and private hosted-service handoff

> **Archived 2026-11-13.** The #438/#462 cutover is complete. The live boundary
> is `docs/adr/cloud-public-private-boundary.md` and
> `docs/cloud/deployment-modes.md`; this plan is kept as the record of the
> handoff.

Tracking: #438 and #462. The repository boundary is recorded in the
[public/private boundary ADR](../adr/cloud-public-private-boundary.md).

## Current ownership

`Nostos` is the canonical public product repository. It owns
`Nostos.Product`, the SelfHosted SQLite/local-filesystem host, local backup and
restore, the BYOK assistant path, portability, capability contracts and the one
Angular frontend.

`Nostos-Cloud` is the private official hosted composition. It pins a public commit
and supplies hosted identity, PostgreSQL tenant persistence, commercial
entitlements/billing, managed AI, hosted object storage, operator recovery and
release operations. It does not contain copied Library, Brain, Reader, Studio,
Notes, domain or Angular source.

The public SelfHosted build has no dependency on the private repository. The hosted
capability values remain part of the public product contract so the same frontend
can present the correct controls when served by the private host.

## Change order

1. Make reusable product, domain, frontend and provider-neutral contract changes
   in public Nostos and merge them first.
2. Update the private repository's submodule pointer to the exact merged public
   commit; record and verify the pin pair.
3. Keep hosted implementations and operations in `Nostos-Cloud`.
4. Run public SelfHosted acceptance without private credentials and private
   boundary/host CI for every relevant change.

Public CI rejects private project, package, source and workflow dependencies. It
also runs SelfHosted/SQLite and portability regressions. Private CI validates the
exact public pin and rejects copied canonical product source outside its submodule.

## Cutover state

#462 carried the private host authority promotion. Isolated provider-backed
staging parity/security evidence was the prerequisite before the hosted release
was operationally promoted, and the last known-good hosted artifact stayed
available for rollback during that transition. That work did not include
production infrastructure changes. The private host now owns those decisions;
the paid-production direction keeps PostgreSQL on Neon rather than depending on
a later provider migration.
