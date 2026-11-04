# Migrations

Raw, versioned PostgreSQL scripts for the `routego_identity` database. The application **never**
applies them — not at startup, not with a flag. There is no DbUp package anywhere in the solution.

## Naming and order

- `V####__snake_case_description.sql` — applied **once**, in numeric order, each recorded in the
  journal table `jobs.schema_versions`. Every script carries its own `BEGIN; … COMMIT;`.
- `R__*.sql` — **RunAlways**: applied on every run, after every `V####` script, never journaled.
  `R__audit_partition_runway.sql` tops the audit partition runway up to twelve months on each deploy.

Forward-only: a breaking change is add → backfill → switch code → remove in a later script.

## Area → schema

| Spec area (`data-model/…`) | Postgres schema |
| --- | --- |
| `authentication` | `authentication` |
| `operator-onboarding` | `operator_onboarding` |
| `signing-keys` | `signing_keys` |

## How they are applied

The contract any runner must follow (the future Phase 10 pipeline's DbUp step included):

1. Journal table `jobs.schema_versions`; ensure the `jobs` schema exists first (DbUp creates the
   table, not the schema).
2. `V####` scripts in numeric order, each once.
3. `R__*.sql` scripts as RunAlways, after every `V####`.
4. **No wrapping transaction** — each script manages its own, and a `CREATE INDEX CONCURRENTLY`
   script cannot run inside one.
5. **Variable substitution disabled** — scripts contain `$…$` PL/pgSQL bodies that must reach
   Postgres verbatim.
6. Fail loudly when the folder holds no scripts; zero scripts applied is never success.

**Until the pipeline exists:**

- Local: `docker compose up -d`, then `./scripts/apply-migrations.ps1`.
- AWS (manual runbook): run the same scripts against RDS from a Session Manager session on
  `routego-core`, in the order above, recording each `V####` in `jobs.schema_versions`.

Tests apply the whole folder to a throwaway database on every run
(`RouteGo.Identity.Api.Tests/Database/MigrationScripts.cs`).
