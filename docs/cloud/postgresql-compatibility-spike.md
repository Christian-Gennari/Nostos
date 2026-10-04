# PostgreSQL compatibility for the Nostos product model

Issue #393 checks that the canonical relational model and representative product
queries remain portable across supported relational providers. This is a
compatibility test only; the public SelfHosted executable uses SQLite.

## Test coverage

`PostgreSqlCompatibilitySpikeTests` runs the real `NostosDbContext` against a
disposable PostgreSQL database and checks:

- schema creation and UUID / timestamp mappings;
- book type inheritance and product relationships;
- command receipts and singleton library state;
- filtered unique ISBN/ASIN indexes, including multiple NULL values;
- transactions, rollback, representative LINQ translation, and restrictive
  collection relationships.

The test asserts generated SQL and creates the schema through the same
bootstrap path as the product (`DatabaseBootstrapService.EnsureReadyAsync`:
the model-generated create script plus the migration-history baseline). Public
CI can start an ephemeral PostgreSQL service without production credentials.
The ordinary SelfHosted suite does not require this database and leaves
`NOSTOS_POSTGRES_SPIKE_CONNECTION` unset.

## Provider ownership

The product owns the EF model and provider-neutral queries. Public runtime
composition remains SQLite-only: SQLite migrations, `DatabaseBootstrapService`,
and same-installation backup/restore. A host may compose another relational
provider behind the same product model without adding that provider to the
SelfHosted executable.

The compatibility package belongs only to `Nostos.Backend.Tests`. Keep its EF
Core major/minor version aligned with the product's EF packages, then run the
public SQLite suite and the disposable PostgreSQL compatibility workflow.
