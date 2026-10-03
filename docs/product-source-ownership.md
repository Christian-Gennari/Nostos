# Product source ownership inventory

Historical inventory baseline: `c22a8b42600123e45cdedfeb837997ca00e97cf8`

This document preserves the evaluated source-ownership snapshot and the 151-row
migration manifest created at that baseline. The row-level manifest is
[`docs/product-source-ownership.csv`](product-source-ownership.csv); it remains
historical provenance, not a live source allowlist or a count that current topology
must continue to match.

At current public main `d69e7dfab978475ad26f5750115b3925942a4302`, batches
02.2 through 02.5 have completed. All 151 manifest source blobs have been moved to
their recorded `Nostos.Product` destinations, the temporary Product `Compile
Include`/`Link` and Backend `Compile Remove` scaffolding is exhausted, and the
existing namespaces and assembly identity remain intentionally unchanged.
The evaluated graphs at that finishing point are Product 166 / Backend 97 with
zero linked Product items and no Product/Backend source overlap.

Ongoing proof is topology-based rather than manifest-count-based.
`scripts/guard-product-source-ownership.py` evaluates the current `Compile` items
for both projects with `dotnet msbuild -getItem:Compile` and rejects missing
physical inputs, Compile items outside their owning project root, duplicate paths,
case-fold collisions, and exact or case-fold Product/Backend overlap. Because it
uses resolved filesystem paths from the evaluated graph, retained
`Nostos.Backend.*` namespaces do not affect the check and future source additions
do not require updating the historical 151-row manifest.

`Nostos.Backend.Tests/Architecture/ProductBoundaryTests.cs` remains complementary
runtime/assembly evidence: representative canonical types are still owned by
`Nostos.Product`, Product does not reference the public host assembly, and hosted
implementation namespaces remain excluded.

## Historical evaluated Compile graph at the inventory baseline

The inventory is based on evaluated

```text
dotnet msbuild -getItem:Compile
```

data for the exact baseline above.

The evaluated graph reports:

- `Nostos.Product`: 166 `Compile` items;
- 151 of those Product `Compile` items are physically located under
  `Nostos.Backend`;
- `Nostos.Backend`: 97 `Compile` items;
- there are no shared `FullPath` entries between the evaluated Product and Backend
  `Compile` item sets;
- there are no target-file collisions in the proposed 151-row destination map.

These numbers describe the evaluated Compile graph at this baseline. They are
evidence that the linked files have one compilation owner today and that the
proposed destination paths are collision-free. They are not evidence that all
future physical moves have succeeded, that `Nostos.Product` has achieved some
broader notion of future build independence, or that provider, migration,
activation, recovery, or hosted operational behavior has been accepted.

A Debug build of the current public host has passed in a separate worktree at the
same baseline. That is useful same-baseline host evidence, but it is not private
consumer evidence for future source-path moves. Full private compatibility evidence
is still required for each physical move batch before that batch can be treated as
accepted.

## Historical ownership model at the inventory baseline

At this baseline, physical path and compilation ownership intentionally differ.

`Nostos.Product/Nostos.Product.csproj` includes 151 files from historical
`Nostos.Backend/...` paths using `Compile Include` and `Link`. The corresponding
rules in `Nostos.Backend/Nostos.Backend.csproj` remove those same files from the
host's default `Compile` items.

Therefore:

- the physical source files still live under `Nostos.Backend`;
- the 151 files listed in the CSV are compiled by `Nostos.Product`;
- `Nostos.Backend` references `Nostos.Product` rather than compiling a second copy;
- the proposed `destination` column records a future mechanical physical path, not
  a path that exists today;
- physical file names are preserved;
- existing namespaces are preserved;
- assembly/compilation ownership remains `Nostos.Product`;
- no database or serialized-data schema change is implied by this inventory.

`Nostos.Backend.Tests/Architecture/ProductBoundaryTests.cs` separately checks the
important assembly boundary: representative canonical types such as
`NostosDbContext` are owned by the `Nostos.Product` assembly, Product does not
reference `Nostos.Backend`, and hosted implementation namespaces are absent from
the Product assembly.

## Classification

Every one of the 151 linked source files appears exactly once in the CSV and is
assigned to one of four sequential physical-move batches.

| Batch | Scope | Rows |
| --- | --- | ---: |
| `02.2 foundation` | configuration, canonical EF/data types and repositories, mapping, lexical/rank-fusion search helpers, serialization | 36 |
| `02.3 services-ai-assistant` | product services, Library, Notes/imports, portability, knowledge retrieval, provider-neutral AI and Ask Nostos orchestration | 67 |
| `02.4 providers` | provider contracts, registry, discovery, acquisition and public content-provider implementations | 30 |
| `02.5 endpoints-health` | product HTTP endpoint mappings/results and product health checks | 18 |
| **Total** |  | **151** |

The classification deliberately keeps Library, Notes, portability and knowledge
services in `02.3 services-ai-assistant`. Data, configuration, mapping, search and
serialization remain in `02.2 foundation`.

At the inventory baseline, the batches described the intended safe refactoring
order. Batches 02.2 through 02.5 are now complete; the CSV and row counts remain
the historical migration snapshot rather than an ongoing topology contract.

## Host-owned source that stays in `Nostos.Backend`

The evaluated Backend graph also makes the SelfHosted host boundary visible.
Files not present in the linked Product inventory remain host-owned unless a later
explicit design decision says otherwise.

Current host-owned composition and adapters include, among others:

- `Nostos.Backend/Program.cs`;
- `Configuration/DataProtectionRegistration.cs`;
- `Configuration/FileStorageOptions.cs`;
- `Configuration/McpOptions.cs`;
- `Configuration/PersistenceRegistration.cs`;
- `Data/DatabaseBootstrapService.cs`;
- `Endpoints/BackupEndpoints.cs`;
- `Integrations/Mcp/LibraryMcpTools.cs`;
- `Integrations/Mcp/McpAuthenticationMiddleware.cs`;
- `Providers/Acquisition/AcquisitionJobManager.cs`;
- `Providers/Acquisition/SelfHostedAcquisitionWorkingRootProvider.cs`;
- `Services/Ai/AiProviderSettingsService.cs`;
- `Services/Ai/NineRouterLlmProvider.cs`;
- `Services/Ai/NineRouterSttProvider.cs`;
- `Services/Ai/OpenAiCompatibleEmbeddingProvider.cs`;
- local backup settings/services;
- `Services/BookText/SelfHostedBookTextStorage.cs`;
- `Services/BookText/SqliteBookTextEmbeddingIndex.cs`;
- local file-storage services;
- SelfHosted workers for acquisition reconciliation, backups, book-text ingestion,
  library receipt retention and topic cleanup.

This separation is significant for the planned moves. Product-owned abstractions
and behavior can move physically while SelfHosted composition and concrete local
adapters remain in the host.

### SQLite migration ownership

The existing EF migration files under `Nostos.Backend/Migrations/`, including
`NostosDbContextModelSnapshot.cs`, remain host-owned in the current graph.

That placement is consistent with the current project definitions:
`Nostos.Backend` carries the EF Core design and SQLite provider dependencies,
including `Microsoft.EntityFrameworkCore.Design`,
`Microsoft.EntityFrameworkCore.Sqlite` and the native SQLite package override.
`Nostos.Product` carries the canonical `NostosDbContext` and relational model
dependencies but does not own the host's SQLite migration history.

Moving the canonical context source therefore does not imply moving migrations or
changing migration ownership. No schema change is part of batches 02.2–02.5.

## Historical path-dependent references

Physical source moves were not only filesystem operations. The following
subsections preserve the path-dependent constraints recorded at the inventory
baseline; the temporary mirror rules described here were removed as their batches
completed.

### Product/Backend project mirror rules

`Nostos.Product.csproj` currently contains the `Compile Include`/`Link` rules for
the 151 historical Backend paths. `Nostos.Backend.csproj` contains matching
`Compile Remove` rules.

For each physical move batch, those two rule sets must be changed together so that
the moved files continue to be compiled exactly once by `Nostos.Product`.
After the applicable files physically live under `Nostos.Product`, their temporary
historical link/remove entries should be removed rather than retained as stale
topology.

The evaluated `Compile` graph should be regenerated after each batch. A batch
should not be accepted if an item disappears, becomes host-compiled, is compiled
twice, or collides with another target path.

### Public/private boundary guard

`scripts/guard-public-private-boundary.py` contains path-sensitive checks under
both `Nostos.Backend/` and `Nostos.Product/`, plus explicit forbidden historical
host paths.

The guard is part of the required repository topology and must continue to protect
the public product after physical moves. Any path-specific rule made stale by a
batch should be deliberately updated while preserving the same public/private
boundary. A source move must not weaken the hosted-implementation exclusions.

### Other references

Tests and current documentation contain `Nostos.Backend.*` namespaces and
historical `Nostos.Backend/...` source paths.

Namespaces are intentionally unchanged by this work, so `using
Nostos.Backend...` statements are not, by themselves, evidence of a stale
reference. They must not be mechanically rewritten to `Nostos.Product.*`.

Likewise, historical or explanatory documentation does not require blind path
replacement. A path should be edited only when the document is intended to
describe the current physical source location rather than historical context,
runtime ownership, or an earlier architecture state.

## Historical sequential move plan

Batches 02.2 through 02.5 have completed. The steps below are retained as migration
provenance and should not be treated as current instructions or as a live path
allowlist.

### 02.2 foundation

Move the 36 rows classified `02.2 foundation` to their listed Product destinations.

This establishes the low-level physical foundation first: configuration,
`NostosDbContext`, data contracts/models/repositories, mapping, search primitives
and serialization.

Verification should include:

1. compare the physical moves against the 36 CSV rows;
2. update the Product include and Backend remove rules for only this batch;
3. re-evaluate both projects' `Compile` items;
4. confirm each moved source is compiled exactly once by `Nostos.Product`;
5. confirm no target-path collision;
6. run Product/backend architecture and affected data/search tests;
7. build the public host at the exact resulting public commit;
8. run the public/private boundary guard;
9. obtain full private consumer build/test compatibility evidence against that
   exact public commit before accepting the batch.

Rollback is mechanical: revert the batch commit or restore the files to their
recorded `source` paths together with the corresponding project-file topology.
No schema rollback should be necessary because this batch must not change schema.

### 02.3 services-ai-assistant

After 02.2 is accepted, move the 67 service, AI and assistant rows.

This includes Library, Notes and highlight-import behavior, portability, knowledge
retrieval, provider-neutral AI contracts/implementations and Ask Nostos
orchestration.

Use the same Compile-graph, boundary, public build/test and private compatibility
gates. Add focused regressions for Library/Notes/portability/knowledge and
assistant behavior because this batch has a wider behavioral surface even though
the intended change remains path-only.

Rollback is the batch-level source/project-file revert. Do not combine behavioral
cleanup, namespace changes or provider substitutions with rollback or forward
migration.

### 02.4 providers

After 02.3 is accepted, move the 30 provider rows: provider contracts, registry,
acquisition/discovery infrastructure and the Gutenberg, LibriVox, Standard Ebooks
and Wikisource implementations listed in the manifest.

The move itself remains mechanical. Provider migrations, provider activation,
remote-service acceptance, production configuration and operational rollout are
outside this inventory's scope.

Verification should include the common graph/build/boundary/private-consumer
gates plus existing provider discovery and acquisition tests. Passing those checks
does not by itself establish production provider acceptance.

Rollback is the isolated 02.4 path/project topology revert.

### 02.5 endpoints-health

After 02.4 is accepted, move the final 18 endpoint/health rows.

This leaves HTTP surface mapping until the lower layers are already physically in
Product, minimizing simultaneous path churn across layers.

Verify endpoint mapping/regression tests, health checks, the evaluated Compile
graph, public host build, boundary guard and exact-commit private consumer
compatibility.

Rollback is the isolated 02.5 source/project topology revert.

## Out of scope

This manifest does not authorize or prove:

- namespace or assembly renames;
- database/schema or migration-history changes;
- provider migrations or production provider acceptance;
- activation, tenant/resource lifecycle, recovery or operational rollout work;
- hosted traffic changes;
- replacement of SelfHosted adapters;
- changes to the public/private product boundary;
- broad documentation rewrites based only on string matching;
- completion of the larger source-ownership workstream.

The intended invariant throughout 02.2–02.5 is narrower: change physical ownership
in small reviewable batches while preserving physical file names, namespaces,
`Nostos.Product` compilation ownership and observable behavior, with public and
private compatibility re-established at every batch boundary.
