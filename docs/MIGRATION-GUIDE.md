# Flyway migrations with Lakebase

Flyway versioned SQL in `db/migrations` is the **only** schema ledger. EF Core remains the SampleApp ORM; it does not generate or apply migrations.

Author SQL on a Git feature branch, prove it on an isolated Dev `pr-N` branch, then promote by replaying the **same** files directly on each environment's stable branch at an immutable Git SHA.

Local feature apply uses `.env` with `lakebase db migrate`, authenticating as you. Trusted PR schema and promote workflows mint a database password and pass it to the same CLI via `DATABASE_URL` (never logged).

Lakebase has no cross-project or cross-workspace branch merge. Promotion therefore replays the Git migration ledger against whichever Lakebase target each GitHub environment configures.

## Baseline caveat

V1 (`V1__initial_create.sql`) is for **empty** databases. Dev/Staging/Prod stables were reported empty of application schema, so first promote can initialize them with V1.

If a database already has tables from EF Core (or a `public.__EFMigrationsHistory` table), **do not** run `flyway migrate` blindly. Baseline once, then continue with new Flyway versions:

```bash
# Only on a DB that already matches V1 objects and is not empty.
dotnet run --project src/Lakebase.Cli -- lakebase db flyway -- baseline -baselineVersion=1 -baselineDescription=initial_create
dotnet run --project src/Lakebase.Cli -- lakebase db validate
```

Rehearse baseline in Dev. Never baseline Production without a snapshot and an explicit DBA review.

## Workflow

### 1. Model + SQL change

Update EF models for the ORM, then add the matching Flyway file. SQL is authoritative; there is no `has-pending-model-changes` check.

```bash
# db/migrations/V2__add_provider_phone_number.sql
# ALTER TABLE "Providers" ADD COLUMN "PhoneNumber" text;
```

### 2. Feature branch + migrate

The trusted PR controller provisions the App SP with write membership and transfers
application table ownership to that SP on `pr-N` (COW keeps the parent's owner otherwise).
Locally you migrate as yourself (`DB_USERNAME` + your CLI login), so your Postgres role needs DDL rights on the branch's tables.

```bash
dotnet run --project src/Lakebase.Cli -- lakebase db setup

dotnet run --project src/Lakebase.Cli -- \
  lakebase platform branch create feature-add-provider-phone --ttl-hours 168

dotnet run --project src/Lakebase.Cli -- lakebase db lint
dotnet run --project src/Lakebase.Cli -- lakebase db migrate
dotnet run --project src/Lakebase.Cli -- lakebase db validate
```

CI fails the PR if Flyway filenames/order/checksums or the Flyway configuration are invalid. The trusted controller runs live `flyway validate` after migrating the isolated `pr-N` branch. It never executes PR application code.

### 3. Optional local rehearsal

```bash
dotnet run --project src/Lakebase.Cli -- \
  lakebase platform staging apply \
    --from feature-add-provider-phone \
    --name rehearsal-local \
    --recreate
```

This creates an ephemeral branch; CI does not require or preserve a `development` branch.

### 4. Optional local promote command

```bash
dotnet run --project src/Lakebase.Cli -- \
  lakebase platform promote \
    --from feature-add-provider-phone \
    --staging-name rehearsal-local
```

On failure, the `pre-migrate-*` snapshot is preserved and the rehearsal branch is not deleted.

GitHub: merge to `develop` promotes Dev stable; merge `develop` → `staging` promotes Staging; merge `staging` → `main` promotes Production. Each step checks out an **immutable SHA** and runs Flyway against that tree's `db/migrations`. Production additionally migrates a copy-on-write rehearsal branch of Production stable before it elevates and migrates stable. The `pre-migrate-*` snapshot is not migrated.

### 5. Cleanup

```bash
dotnet run --project src/Lakebase.Cli -- \
  lakebase platform branch delete feature-add-provider-phone --ignore-missing
```

Dev feature branches expire (1–7 day TTL). Trusted PR `pr-N` branches expire after 24 hours and are deleted when the PR closes.

## Safety policy

1. Store SQL in Git; never edit a version that has been applied to Staging or Production.
2. Prefer **expand/contract**: add columns/tables in one change, ship a backward-compatible app, remove old columns in a later change.
3. Keep the running application compatible with both the pre- and post-migration schema during rollout.
4. Keep migrations small. Flyway's `flyway_schema_history` is the lock; still avoid destructive one-shots.
5. Destructive SQL (`DROP`, `TRUNCATE`, non-null without default, type narrowing) needs explicit owner review. Call it out in the PR; add an independent DBA reviewer when the repository moves beyond solo testing.
6. Expect lock and statement timeouts on busy stables; retry CI handles scale-to-zero, not lock contention — schedule destructive work.
7. Prove migration SQL on the isolated Dev `pr-N` branch before touching stable.
8. Forward-fix: ship a new versioned SQL file rather than rewriting history. Do not `flyway undo` on Production except as a rehearsed emergency (Community CLI has no undo).

## Rollback and recovery

Ship a new Flyway version that reverses the change (forward-fix). A `pre-migrate-*` Lakebase branch is **evidence and a restore source**, not an automatic rollback. For catastrophic promote failures:

1. Keep the snapshot (CI does this on failure).
2. Restore via Lakebase point-in-time restore or by treating the snapshot branch as the recovery source (rehearse this; do not invent a merge).
3. Forward-fix with a new SQL file when the data is intact and only schema must change.

Rehearse PITR/restore in Dev before you need it in Production.
