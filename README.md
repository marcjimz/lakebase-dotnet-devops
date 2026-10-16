# Lakebase Sample App

Sample ASP.NET Core API on Databricks Lakebase (Postgres). EF Core is the application ORM. **Flyway is the only schema ledger** (`db/migrations`). `Lakebase.Cli` hosts the pinned Flyway CLI (`lakebase db migrate` / `validate` / `info` / `lint`) and handles copy-on-write branching, roles, snapshots, and promote. There is no second C# migration ledger.

Promotion follows Git `develop` → `staging` → `main`. Each stage selects its Lakebase workspace, project, stable branch, endpoint, and credentials through a paired GitHub environment. The sample currently points those stages at three projects in one workspace; the same workflows can target separate Dev, Staging, and Production workspaces without code changes. PR branches are created only in the configured Dev target, and promotion snapshots then replays committed Flyway SQL on the configured stable branch.

## Layout

| Path | Role |
|------|------|
| `db/migrations` | Flyway versioned SQL (schema source of truth) |
| `conf/flyway.conf` | Non-secret Flyway settings |
| `src/SampleApp` | Sample API + EF Core models/DbContext (runtime ORM only) |
| `src/SampleApp.Tests` | Unit tests + integration tests (`Category=Integration`) |
| `src/Lakebase` | Library (auth, platform ops, credential minting) |
| `src/Lakebase.Cli` | CLI (`lakebase platform` / `lakebase db setup|lint|migrate|validate|info`) |
| `docs/` | Platform vs database, branching, migrations, rulesets |

## Config

| File | Used by |
|------|---------|
| `.env.platform` | `lakebase platform *` |
| `.env.database` | SampleApp, `lakebase db *`, `lakebase db generate-token` |

```bash
cp .env.platform.example .env.platform
cp .env.database.example .env.database
```

See [docs/PLATFORM-VS-DATABASE.md](docs/PLATFORM-VS-DATABASE.md).

## Prerequisites

- .NET 10 SDK
- Flyway CLI 11.20.3 (`dotnet run --project src/Lakebase.Cli -- lakebase db setup` — pinned, checksum-verified)
- A Lakebase project in your Databricks workspace

Run CLI commands from the **repo root**.

## Quick start

```bash
dotnet run --project src/Lakebase.Cli -- lakebase db setup

dotnet run --project src/Lakebase.Cli -- \
  lakebase platform branch create feature-my-change --ttl-hours 168

dotnet run --project src/Lakebase.Cli -- lakebase db generate-token

dotnet run --project src/Lakebase.Cli -- lakebase db migrate
dotnet run --project src/Lakebase.Cli -- lakebase db validate

dotnet run --project src/SampleApp
```

Swagger: `http://localhost:5000/swagger`

## Schema change workflow

Lakebase has no merge. Schema reaches each environment by replaying Git Flyway SQL at an immutable SHA.

1. Branch from Git `develop`; add a new `db/migrations/V{n}__*.sql` file
2. Open a PR to `develop` — secret-free CI builds/tests; a trusted controller migrates an isolated Dev `pr-N` branch when Flyway paths changed
3. Merge to `develop` — snapshot, then replay Flyway on the configured Dev stable branch
4. Open a PR `develop` → `staging` — same required checks
5. Merge to `staging` — snapshot, then replay Flyway on Staging stable
6. Open a PR `staging` → `main`
7. Merge to `main` — snapshot Production stable, migrate a COW rehearsal branch, then replay Flyway on Production stable

Details: [docs/BRANCHING-STRATEGY.md](docs/BRANCHING-STRATEGY.md), [docs/MIGRATION-GUIDE.md](docs/MIGRATION-GUIDE.md), [docs/GITHUB-RULESETS.md](docs/GITHUB-RULESETS.md).

**Adopting Flyway on a database that already has EF Core schema/`__EFMigrationsHistory` requires a one-time Flyway `baseline`. Do not `flyway migrate` V1 onto an already-populated EF database.** Current Dev/Staging/Prod stables were empty of app schema, so V1 can initialize fresh stables.

## CI

| Workflow | Trigger | Action |
|----------|---------|--------|
| `github-branch.yml` (`Lakebase Dev Feature Branch`) | Manual dispatch from Git `develop` | Trusted create/delete lifecycle for optional Dev feature branches |
| `pr-branch.yml` (`Lakebase PR Branch`) | PR open/sync | Untrusted lane: locked restore, build, unit tests, and offline `lakebase db lint`; no Lakebase secrets |
| `pr-lakebase.yml` (`Lakebase PR Schema`) | `pull_request_target` into `develop` | Trusted lane: schema diff, isolated `pr-N`, Flyway migrate/validate, cleanup; never executes PR application code |
| `dev-promote.yml` (`Promote to Dev`) | Push to `develop` | Immutable merge SHA → snapshot → Dev stable (`dev-platform` / `dev-database`) |
| `staging-promote.yml` (`Promote to Staging`) | Push to `staging` (from `develop`) | Snapshot → Staging stable (`staging-platform` / `staging-database`) |
| `production-promote.yml` (`Promote to Production`) | Push to `main` (from `staging`) | Snapshot → COW rehearsal migrate → Production stable (`production-platform` / `production-database`) |
| `promote-environment.yml` | `workflow_call` | Shared promote implementation |

GitHub environments. Publish secrets from outside this repo with `~/git_projects/lakebase-dotnet-devops-env/scripts/publish-github-secrets.sh`. Do not copy env files or secret values into git.

| Environment | Trust boundary |
|-------------|----------------|
| `dev-platform` | Dev control plane |
| `dev-database` | Dev Postgres / migrate |
| `staging-platform` | Staging control plane (restrict to `staging`) |
| `staging-database` | Staging Postgres |
| `production-platform` | Production control plane (restrict to `main`) |
| `production-database` | Production Postgres |

Secret **names** match the existing pattern: `DATABRICKS_HOST`, `LAKEBASE_PLATFORM_*`, `LAKEBASE_DATABASE_*`, `LAKEBASE_PROJECT_ID`, `LAKEBASE_PARENT_BRANCH_ID`, `LAKEBASE_APP_SP_ID`, `LAKEBASE_PROD_ENDPOINT`, `LAKEBASE_PROD_ENDPOINT_PATH`, `LAKEBASE_ALLOW_BRANCH_DELETE`.

Solo-safe GitHub rulesets require PRs and automated checks but zero human approvals. All actions are pinned; privileged jobs use branch-restricted environments.

Runners are split by trust. Set these repository variables to change them:

| Variable | Default | Used by |
|----------|---------|---------|
| `LAKEBASE_UNTRUSTED_RUNNER` | `self-hosted` | `pr-branch.yml`, which runs PR code with no secrets |
| `LAKEBASE_TRUSTED_RUNNER` | `self-hosted` | Every job that holds Lakebase credentials |

Both default to `self-hosted` today, so PR code shares the runner with privileged jobs. That is a known residual risk: PR code could tamper with a runner that later holds production credentials. Point `LAKEBASE_UNTRUSTED_RUNNER` at an ephemeral or separate runner pool when one is available. Every job sets up .NET through `.github/actions/setup-dotnet-isolated`, which builds the CLI once (`$LAKEBASE_CLI`) from trusted code.

Security posture, residual risks, and GitHub OIDC (secretless CI) setup: [docs/THREAT-MODEL.md](docs/THREAT-MODEL.md).

## Using the library in an app

Register one shared data source; it rotates the Lakebase credential automatically:

```csharp
builder.Services.AddLakebaseDbContext<AppDbContext>(DatabaseConnectionSettings.Load());
```

Do not build an `NpgsqlDataSource` inside an `AddDbContext` options callback. That creates a new connection pool and mints a new credential on every request.

Add `--json` on CLI commands for machine-readable reports (progress on stderr, result on stdout). Failures return a non-zero exit code in both modes.
