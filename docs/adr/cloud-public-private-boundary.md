# ADR: Public Nostos product and private hosted-service boundary

- **Status:** Accepted; implemented by the public product seam and #462 cutover work
- **Tracking:** [#438](https://github.com/Christian-Gennari/Nostos/issues/438), [#462](https://github.com/Christian-Gennari/Nostos/issues/462)
- **Related:** #258 Nostos Cloud programme; paid hosting/database productionization now lives in the private Nostos-Cloud launch-readiness work
- **Operational gate:** isolated private staging parity/security evidence is required before hosted traffic is moved

## Decision

Nostos is one product. `Christian-Gennari/Nostos` owns the canonical product,
domain model and the one customer frontend. The public repository is a complete,
usable SelfHosted application and has no dependency on the private repository.

`Christian-Gennari/Nostos-Cloud` owns the official hosted composition and its
commercial/provider/operator implementation. It consumes public `Nostos.Product`,
`Nostos.Shared` and Angular source from one exact pinned public commit.

```text
private Nostos-Cloud host
        │ pinned public commit
        ▼
public Nostos product
```

Reusable product, domain, capability-contract and frontend changes land in public
Nostos first. The private repository moves its submodule pointer after that public
commit is merged. No source-copy, sync, subtree or private package dependency is
allowed in the public build.

## Public ownership

The public repository keeps:

- canonical Library, Brain, Reader, Studio, Notes, Concepts and Writing behavior;
- `Nostos.Product`, `Nostos.Shared`, domain models and provider-neutral contracts;
- the SelfHosted ASP.NET host, SQLite persistence and schema bootstrap;
- local filesystem media and local backup/restore;
- Ask Nostos orchestration with customer-configured BYOK providers;
- the `.nostos` portable archive format and generic recovery/export contracts;
- deployment capability contracts and the single Angular frontend;
- product tests and documentation that run without private source or secrets.

The public executable always composes SelfHosted. Cloud capability values remain
in the provider-neutral deployment contract because the same frontend and product
API are consumed by the official hosted executable.

## Private ownership

The private repository owns the hosted implementations and operations, including
hosted auth, account/control-plane state, PostgreSQL tenant provisioning and
migrations, commercial entitlements and Paddle, managed AI adapters and metering,
B2/S3 storage, operator recovery and workers, and hosted release/staging workflows.
Those implementations are not compiled or shipped by public Nostos.

## Project composition

```text
public Nostos
  Nostos.Shared       shared DTOs and capability contracts
  Nostos.Product      canonical model, services, API and portable archives
  Nostos.Backend      SelfHosted host: SQLite, local storage, backup, BYOK
  Nostos.Frontend     the one Angular customer application

private Nostos-Cloud
  upstream/Nostos   pinned, read-only public source
  Nostos.Cloud.Hosting      hosted implementations
  Nostos.Cloud.Host         official hosted executable
```

The private host composes the public product through `AddNostosProduct(...)` and
`MapNostosProductEndpoints(...)`. It records both its own SHA and the pinned public
SHA in release metadata. A pin is the exact public commit, never a branch.

## Public acceptance and CI

Public CI must work from a fresh public checkout with no private-repository
credential. It enforces the boundary and verifies:

- public restore/build and SelfHosted startup;
- SQLite bootstrap and local media;
- local backup/restore and portable archive import/export;
- public backend/product regressions and BYOK orchestration;
- Angular checks, tests and production build;
- PostgreSQL compatibility of the canonical product model in a disposable test
  database.

Private CI verifies the exact submodule/pin pair, blocks copied product source,
builds the host against that public commit and emits both SHAs. Reusable product
changes must already be reachable from public `main` before a private pin update.

## Cutover and rollback

The private artifact must pass isolated provider-backed staging parity and security
checks before it receives hosted traffic. Keep the last known-good public Cloud
artifact available during the transition. Public cleanup removes hosted
implementation from the public build after that gate; Git history retains the
rollback source. This repository split does not migrate the production database or choose a hosted provider. The current private-host decision keeps PostgreSQL on Neon and productionizes it in place; provider-specific launch work belongs in Nostos-Cloud.
