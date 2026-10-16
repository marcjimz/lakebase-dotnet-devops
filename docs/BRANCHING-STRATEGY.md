# Lakebase Branching Strategy

Lakebase branches are copy-on-write snapshots. Creating a branch is O(1). There is **no merge**, and branches **cannot cross projects**. Schema reaches each environment's stable branch by replaying Flyway SQL from an **immutable Git SHA**.

GitHub environment configuration maps each stage to a Lakebase workspace, project, stable branch, and endpoint. The current sample uses three projects in one workspace, but the workflow topology does not require a shared workspace:

| Environment | Current sample project | Stable branch | Ephemeral branches | GitHub refs |
|-------------|------------------------|---------------|--------------------|-------------|
| Dev | `lakebase-dotnet-dev` | auto-created `production` | `pr-N`, optional local feature/rehearsal | feature branches; merge to Git `develop` |
| Staging | `lakebase-dotnet-staging` | `production` | pre-migrate recovery snapshots only | Git `staging` |
| Production | `lakebase-dotnet-prod` | `production` | `rehearse-*` (migrated, then deleted); `pre-migrate-*` snapshots (not migrated) | Git `main` |

```
GitHub feature  → secret-free build + unit tests + Flyway lint
PR to develop   → trusted controller → Dev `pr-N` (24h TTL) → migrate + validate
merge → develop → snapshot → Dev stable
PR develop → staging → merge → snapshot → Staging stable
PR staging → main → merge → snapshot → COW rehearsal migrate → Production stable

Clone / migrate jobs run only when `db/migrations/` or `conf/flyway.conf` changed vs the PR base. Build, unit tests, and `lakebase db lint` always run.
```

See [PLATFORM-VS-DATABASE.md](PLATFORM-VS-DATABASE.md) and [GITHUB-RULESETS.md](GITHUB-RULESETS.md).

## GitHub environments

Platform jobs and database jobs use **separate** GitHub environments so control-plane credentials never reach untrusted PR code. Setup steps: [README → GitHub setup](../README.md#github-setup).

| GitHub environment | Used by | Typical secrets |
|--------------------|---------|-----------------|
| `dev-platform` | trusted `pr-lakebase.yml`, manual feature lifecycle from Git `develop`, `dev-promote.yml` | `DATABRICKS_HOST`, `LAKEBASE_PLATFORM_*`, `LAKEBASE_PROJECT_ID`, `LAKEBASE_PARENT_BRANCH_ID`, `LAKEBASE_APP_SP_ID`, `LAKEBASE_ALLOW_BRANCH_DELETE` |
| `dev-database` | trusted PR schema migrate, `dev-promote.yml` | `DATABRICKS_HOST`, `LAKEBASE_DATABASE_*`, `LAKEBASE_PROD_ENDPOINT`; variables `LAKEBASE_PROJECT_ID`, `LAKEBASE_PARENT_BRANCH_ID` |
| `staging-platform` | Staging promote (branch/role/snapshot) | same platform pattern, Staging project |
| `staging-database` | Staging migrate/test | same database pattern, Staging project |
| `production-platform` | Production promote (branch/role/snapshot) | same platform pattern, Production project |
| `production-database` | Production migrate/test | same database pattern, Production project |

`LAKEBASE_PROD_*` in each `*-database` environment is **that project's stable endpoint**, not a pointer at the Production project.

To move a stage to another workspace or project, update its paired `*-platform` and `*-database` GitHub environment configuration together. No workflow-code change is required.

The solo-safe policy restricts `dev-*` to GitHub `develop` and `main`, `staging-*` to `staging`, and `production-*` to `main`. `main` is on the Dev pair so trusted `pull_request_target` jobs can use `dev-platform` / `dev-database`. It has no environment reviewers or CODEOWNER approval gate. Add independent approval later without changing workflow code.

## Naming

| Context | Pattern | Example |
|---------|---------|---------|
| Optional Dev feature | Sanitized GitHub ref (`github-branch.yml`) | `feature-add-provider-phone` |
| Dev CI PR | `pr-<number>` (24h TTL) | `pr-42` |
| Pre-promote snapshot | `pre-migrate-<sha12>` or `pre-migrate-pr-<n>-<sha12>` | `pre-migrate-pr-42-abc123def456` |
| Production rehearsal | `rehearse-<sha12>` or `rehearse-pr-<n>-<sha12>` (24h TTL; CI Production only) | `rehearse-pr-42-abc123def456` |
| Environment stable | `production` (Lakebase default) | `production` |

Names are sanitized to lowercase `[a-z0-9-]`, length 3–63. GitHub refs `main`, `develop`, `staging`, `production`, and `development` are not mapped to Dev feature branches. Git `develop` is the integration branch; there is no persistent Lakebase `development` dependency.

## Commands (local / one project)

Run from the repo root. Local CLI still uses `.env.platform` / `.env.database` for **one** project at a time.

### Feature branch (Dev)

```bash
dotnet run --project src/Lakebase.Cli -- \
  lakebase platform branch create feature-add-provider-phone --ttl-hours 168

# add db/migrations/V2__add_provider_phone_number.sql
dotnet run --project src/Lakebase.Cli -- lakebase db lint
dotnet run --project src/Lakebase.Cli -- lakebase db migrate
dotnet run --project src/Lakebase.Cli -- lakebase db validate
dotnet run --project src/Lakebase.Cli -- lakebase db info
```

### Optional rehearsal (COW of stable)

Creates a temporary COW of **stable** (`LAKEBASE_MAIN_BRANCH_ID`), then runs `flyway migrate`. It does not modify stable and is not used by CI.

```bash
dotnet run --project src/Lakebase.Cli -- \
  lakebase platform staging apply \
    --from feature-add-provider-phone \
    --name rehearsal-local \
    --recreate \
    --ttl-hours 24
```

### Promote (replay on stable)

```bash
dotnet run --project src/Lakebase.Cli -- \
  lakebase platform promote \
    --from feature-add-provider-phone \
    --staging-name rehearsal-local
```

Requires `LAKEBASE_ALLOW_BRANCH_DELETE=true` to remove the rehearsal branch after success. On failure, the `pre-migrate-*` snapshot is preserved. A snapshot is **recovery evidence**, not automatic rollback — use it (or PITR) deliberately.

CI promote does **not** copy data or branches across projects. It checks out the merge SHA and replays that SHA's Flyway SQL in that environment's project.

Merge to Git `develop` replays onto **Lakebase .NET Dev** stable. Merge to Git `staging` requires the PR head to be `develop`. Merge to Git `main` requires the PR head to be `staging`. Existing Dev feature branches stay on their original copy-on-write snapshot; only **new** feature branches inherit the updated Dev parent.

### Helpers

```bash
dotnet run --project src/Lakebase.Cli -- \
  lakebase platform branch create pr-42 --ttl-hours 24 --recreate --no-write-env

dotnet run --project src/Lakebase.Cli -- \
  lakebase platform branch delete feature-add-provider-phone --ignore-missing

dotnet run --project src/Lakebase.Cli -- \
  lakebase platform role provision production --read-only
```

## Machine-readable output

With `--json`: progress on stderr, report on stdout.

```bash
dotnet run --project src/Lakebase.Cli -- \
  lakebase platform staging apply --name rehearsal-local --json
```

## Guards

- Protected names (`production`, `main`, `master`, `LAKEBASE_MAIN_BRANCH_ID`) cannot be deleted via CLI.
- OAuth roles never receive `DATABRICKS_SUPERUSER`. Protected/stable uses empty membership (read-only); feature/write uses `databricks_postgres`.
- Each project has a limited number of unarchived branches (typically 10). Optional feature branches are managed by manual dispatch and expire in 1–7 days; trusted `pr-N` branches expire after 24 hours and are deleted on PR close.
- Promote only the merge commit SHA, never a moving branch head.
