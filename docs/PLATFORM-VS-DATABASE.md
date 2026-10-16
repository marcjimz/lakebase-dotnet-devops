# Platform vs Database Flows

Two independent flows, configs, and credentials.

## Platform (control plane)

**Config:** `.env.platform`  
**Credentials:** `LAKEBASE_PLATFORM_CLIENT_ID` / `LAKEBASE_PLATFORM_CLIENT_SECRET`  
**Not for:** Postgres connections, SampleApp runtime, Flyway

| Command | Purpose |
|---------|---------|
| `lakebase platform branch create <name>` | Create/reuse COW branch (`--parent`, `--ttl-hours`, `--recreate`, `--no-write-env`) |
| `lakebase platform branch list` | List branch ids |
| `lakebase platform branch delete <name>` | Delete (`--ignore-missing`); needs `LAKEBASE_ALLOW_BRANCH_DELETE=true` |
| `lakebase platform role provision <name> [--write\|--read-only]` | Least-privilege membership (`databricks_postgres` vs empty) |
| `lakebase platform staging apply [--from <feature>] --name <ephemeral>` | Optional local COW rehearsal from stable; CI does not maintain a validation branch |
| `lakebase platform promote [--from <feature>]` | Optional local helper: replay Git migrations on stable and delete the named rehearsal branch |

## Database (data plane)

**Config:** `.env.database`  
**Credentials:** `LAKEBASE_DATABASE_CLIENT_ID` / `LAKEBASE_DATABASE_CLIENT_SECRET`  
**Not for:** Branch create/delete, role APIs

| Command / tool | Purpose |
|----------------|---------|
| `lakebase db generate-token` | Short-lived Postgres password |
| `lakebase db setup` | Install pinned Flyway CLI (checksum-verified) |
| `lakebase db lint` | Offline SQL filename/order/checksum checks |
| `lakebase db migrate` | Apply Flyway SQL (retries compute wake-ups) |
| `lakebase db validate` | Live Flyway checksum/history validation |
| `lakebase db info` | Flyway history vs Git SQL |
| `lakebase db grant-ddl --to <app-sp>` | Make the app SP owner of application tables (feature-branch DDL) |
| `lakebase db flyway -- …` | Pass-through (baseline, repair, …) |
| `SampleApp` | API against `DATABASE_URL` |

Branch create updates `.env.database` (`LAKEBASE_ENDPOINT` + host) unless `--no-write-env`.

## Setup

```bash
cp .env.platform.example .env.platform
cp .env.database.example .env.database
```

## Library layout

```
src/Lakebase/
├── Configuration/   # env loaders
├── Auth/            # workspace + DB tokens
├── Api/             # REST client
├── Platform/        # branch / role / promote (promote calls FlywayRunner)
└── Database/        # generate-token, Npgsql, Flyway host (lint/migrate/validate)
```

## CI secrets

Six GitHub environments: `dev-platform`, `dev-database`, `staging-platform`, `staging-database`, `production-platform`, `production-database`. Publish them with `~/git_projects/lakebase-dotnet-devops-env/scripts/publish-github-secrets.sh` (outside this repo). See [BRANCHING-STRATEGY.md](BRANCHING-STRATEGY.md) and [GITHUB-RULESETS.md](GITHUB-RULESETS.md).

| Secret | Flow |
|--------|------|
| `DATABRICKS_HOST` | Both |
| `LAKEBASE_PLATFORM_CLIENT_ID` / `LAKEBASE_PLATFORM_CLIENT_SECRET` | Platform environments |
| `LAKEBASE_PROJECT_ID` / `LAKEBASE_PARENT_BRANCH_ID` / `LAKEBASE_APP_SP_ID` | Platform |
| `LAKEBASE_DATABASE_CLIENT_ID` / `SECRET` / `USERNAME` | Database environments |
| `LAKEBASE_PROD_ENDPOINT` / `LAKEBASE_PROD_ENDPOINT_PATH` | That environment's stable host + endpoint path |
