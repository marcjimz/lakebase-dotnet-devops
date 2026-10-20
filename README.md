# Lakebase Sample App

Sample ASP.NET Core API on Databricks Lakebase (Postgres). EF Core is the application ORM. **Flyway is the only schema ledger** (`db/migrations`). `Lakebase.Cli` hosts the pinned Flyway CLI (`lakebase db migrate` / `validate` / `info` / `lint`) and handles copy-on-write branching, roles, snapshots, and promote. There is no second C# migration ledger.

Promotion follows Git `develop` → `staging` → `main`. Each stage selects its Lakebase workspace, project, stable branch, endpoint, and credentials through a paired GitHub environment. The sample currently points those stages at three projects in one workspace; the same workflows can target separate Dev, Staging, and Production workspaces without code changes. PR branches are created only in the configured Dev target, and promotion snapshots then replays committed Flyway SQL on the configured stable branch.

## Layout

| Path | Role |
|------|------|
| `db/migrations` | Flyway versioned SQL (schema source of truth) |
| `lab/migrations` | Staged lab migrations (V2, V3); drag into `db/migrations` during the [lab](docs/LAB.md) |
| `lab/seed` | Lab sample data (`lakebase db seed`), not a migration |
| `conf/flyway.conf` | Non-secret Flyway settings |
| `src/SampleApp` | Sample API + EF Core models/DbContext (runtime ORM only) |
| `src/SampleApp.Tests` | Unit tests + integration tests (`Category=Integration`) |
| `src/Lakebase` | Library (auth, platform ops, credential minting) |
| `src/Lakebase.Cli` | CLI (`lakebase platform` / `lakebase db setup|lint|migrate|validate|info`) |
| `docs/` | Platform vs database, branching, migrations, rulesets |

## Config

One `.env` at the repo root is shared by SampleApp, the `lakebase` CLI, and the [Lakebase SCM VS Code extension](docs/LAB.md):

```bash
cp .env.example .env   # set LAKEBASE_PROJECT_ID and DATABRICKS_HOST (or DATABRICKS_CONFIG_PROFILE)
databricks auth login --host <workspace-url>
```

Locally you authenticate as yourself: no PAT, client secret, or database password goes in `.env`. The extension's post-checkout hook (or `lakebase platform branch create`) writes the branch connection metadata (`LAKEBASE_BRANCH_ID`, `LAKEBASE_HOST`, `LAKEBASE_ENDPOINT`, `DB_USERNAME`), and the app and Flyway mint a short-lived Postgres credential from it.

| `LAKEBASE_AUTH_MODE` | Where | Credentials |
|----------------------|-------|-------------|
| `databricks-cli` (local default) | Laptop | `databricks auth login` profile |
| `pat` | Laptop without the CLI | `DATABRICKS_TOKEN` |
| `client-credentials` | CI | Service principal client id + secret from GitHub environments |
| `github-oidc` | CI | Service principal client id + GitHub OIDC (`LAKEBASE_AUTH_TYPE=github-oidc`) |

When unset, the mode is inferred: `DATABRICKS_TOKEN` → `pat`, client id + secret → `client-credentials`, client id + `LAKEBASE_AUTH_TYPE=github-oidc` → `github-oidc`, otherwise `databricks-cli`. Legacy `.env.platform` / `.env.database` files still load as fallbacks. See [docs/PLATFORM-VS-DATABASE.md](docs/PLATFORM-VS-DATABASE.md).

## Prerequisites

- .NET 10 SDK
- Databricks CLI, signed in with `databricks auth login`
- Flyway CLI 11.20.3 (`dotnet run --project src/Lakebase.Cli -- lakebase db setup` — pinned, checksum-verified)
- A Lakebase project in your Databricks workspace

Run CLI commands from the **repo root**.

## Quick start

```bash
dotnet run --project src/Lakebase.Cli -- lakebase db setup

dotnet run --project src/Lakebase.Cli -- \
  lakebase platform branch create feature-my-change --ttl-hours 168

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

**This repo's `db/migrations` starts at V1 for the lab; V2 and V3 live in `lab/migrations`.** Point a fork's CI at fresh Lakebase projects: a database that already applied V2/V3 fails `flyway validate` until they are back in `db/migrations`.

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

Each stage uses a pair of GitHub environments, so control-plane credentials never reach database jobs or untrusted PR code. Setup steps, secrets, and variables: [GitHub setup](#github-setup).

| Environment | Trust boundary | Deployment branches |
|-------------|----------------|---------------------|
| `dev-platform` | Dev control plane | `develop`, `main` |
| `dev-database` | Dev Postgres / migrate | `develop`, `main` |
| `staging-platform` | Staging control plane | `staging` |
| `staging-database` | Staging Postgres | `staging` |
| `production-platform` | Production control plane | `main` |
| `production-database` | Production Postgres | `main` |

The Dev pair allows `main` because `pr-lakebase.yml` runs as `pull_request_target`, which loads workflow code from the default branch.

Solo-safe GitHub rulesets require PRs and automated checks but zero human approvals. All actions are pinned; privileged jobs use branch-restricted environments.

Runners are split by trust. Set these repository variables to change them:

| Variable | Default | Used by |
|----------|---------|---------|
| `LAKEBASE_UNTRUSTED_RUNNER` | `self-hosted` | `pr-branch.yml`, which runs PR code with no secrets |
| `LAKEBASE_TRUSTED_RUNNER` | `self-hosted` | Every job that holds Lakebase credentials |

Both default to `self-hosted` today, so PR code shares the runner with privileged jobs. That is a known residual risk: PR code could tamper with a runner that later holds production credentials. Point `LAKEBASE_UNTRUSTED_RUNNER` at an ephemeral or separate runner pool when one is available. Every job sets up .NET through `.github/actions/setup-dotnet-isolated`, which builds the CLI once (`$LAKEBASE_CLI`) from trusted code.

Security posture, residual risks, and GitHub OIDC (secretless CI) setup: [docs/THREAT-MODEL.md](docs/THREAT-MODEL.md).

## GitHub setup

One-time setup for a new copy of this repo. Follow the steps in order: a push to `main`, `staging`, or `develop` starts a promote run, so the runner, environments, and secrets must exist before those branches do. Never commit env files or secret values.

Prerequisites:

- Dev, Staging, and Production Lakebase projects, each with a stable branch (usually `production`).
- Per stage, two different service principals: a **platform SP** (branches, roles, snapshots) and a **database SP** (the app SP, which runs Flyway and the app).
- `gh` signed in as a repo admin with the `repo` and `workflow` scopes (`gh auth refresh --scopes workflow`).

```bash
REPO=<owner>/lakebase-dotnet-devops
```

### 1. Register a self-hosted runner

Every job runs on the `self-hosted` label unless the runner variables above say otherwise. Add a runner under **Settings → Actions → Runners → New self-hosted runner**, or get a registration token with:

```bash
gh api -X POST "repos/$REPO/actions/runners/registration-token" --jq .token
```

The runner needs `git`, `curl`, and `jq`. Jobs install .NET (from `global.json`) and the pinned Flyway CLI themselves. A runner is registered to one repository, so a second copy of this repo needs its own runner; a separate install directory on the same machine works.

### 2. Create the environments and branch policies

```bash
add_env() {
  local env="$1"; shift
  echo '{"deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true}}' \
    | gh api -X PUT "repos/$REPO/environments/$env" --input - >/dev/null
  for branch in "$@"; do
    gh api -X POST "repos/$REPO/environments/$env/deployment-branch-policies" \
      -f name="$branch" -f type=branch >/dev/null
  done
}

add_env dev-platform develop main
add_env dev-database develop main
add_env staging-platform staging
add_env staging-database staging
add_env production-platform main
add_env production-database main
```

### 3. Set secrets and variables

Every stage uses the same names with its own values.

`*-platform` environments (all secrets):

| Secret | Value |
|--------|-------|
| `DATABRICKS_HOST` | Workspace URL, e.g. `https://<workspace>.cloud.databricks.com` |
| `LAKEBASE_PLATFORM_CLIENT_ID` / `LAKEBASE_PLATFORM_CLIENT_SECRET` | Platform SP OAuth client id / secret |
| `LAKEBASE_PROJECT_ID` | This stage's Lakebase project id |
| `LAKEBASE_PARENT_BRANCH_ID` | Stable branch id (usually `production`) |
| `LAKEBASE_APP_SP_ID` | Database (app) SP application id |
| `LAKEBASE_DEV_GROUP` | Optional, `dev-platform` only: workspace group granted write on feature branches |

`*-database` environments:

| Name | Kind | Value |
|------|------|-------|
| `DATABRICKS_HOST` | secret | Workspace URL |
| `LAKEBASE_DATABASE_CLIENT_ID` / `LAKEBASE_DATABASE_CLIENT_SECRET` | secret | Database (app) SP OAuth client id / secret |
| `LAKEBASE_DATABASE_USERNAME` | secret | Postgres role that currently owns the tables, normally the database SP application id ([details](docs/GITHUB-RULESETS.md#github-environments-admin)) |
| `LAKEBASE_PROD_ENDPOINT` | secret | This stage's stable endpoint **host**, e.g. `ep-<name>.database.<region>.cloud.databricks.com` |
| `LAKEBASE_PROJECT_ID` | variable | This stage's Lakebase project id |
| `LAKEBASE_PARENT_BRANCH_ID` | variable | Stable branch id (defaults to `production`) |
| `LAKEBASE_APP_SP_ID` | variable | Optional, `dev-database` only; defaults to `LAKEBASE_DATABASE_CLIENT_ID` |

`LAKEBASE_PROD_ENDPOINT` is the stable endpoint of *that stage's* project, not a pointer at the Production project.

`gh secret set` without `--body` prompts for each value, which keeps secrets out of shell history and process arguments:

```bash
for stage in dev staging production; do
  for name in DATABRICKS_HOST LAKEBASE_PLATFORM_CLIENT_ID LAKEBASE_PLATFORM_CLIENT_SECRET \
              LAKEBASE_PROJECT_ID LAKEBASE_PARENT_BRANCH_ID LAKEBASE_APP_SP_ID; do
    echo "$stage-platform: $name"
    gh secret set "$name" --env "$stage-platform" --repo "$REPO"
  done
  for name in DATABRICKS_HOST LAKEBASE_DATABASE_CLIENT_ID LAKEBASE_DATABASE_CLIENT_SECRET \
              LAKEBASE_DATABASE_USERNAME LAKEBASE_PROD_ENDPOINT; do
    echo "$stage-database: $name"
    gh secret set "$name" --env "$stage-database" --repo "$REPO"
  done
  read -r -p "$stage Lakebase project id: " project_id
  gh variable set LAKEBASE_PROJECT_ID --env "$stage-database" --repo "$REPO" --body "$project_id"
  gh variable set LAKEBASE_PARENT_BRANCH_ID --env "$stage-database" --repo "$REPO" --body production
done
```

Optional variables:

| Variable | Scope | Purpose |
|----------|-------|---------|
| `LAKEBASE_TRUSTED_RUNNER` / `LAKEBASE_UNTRUSTED_RUNNER` | repository | Runner labels (default `self-hosted`) |
| `LAKEBASE_FEATURE_TTL_HOURS` | `dev-platform` | Feature-branch TTL, clamped to 24 to 168 (default 168) |

To replace the client secrets with GitHub OIDC, see [docs/THREAT-MODEL.md](docs/THREAT-MODEL.md).

### 4. Push `main`, then create `staging` and `develop`

Keep `main` as the default branch and create the other two from it:

```bash
git push <remote> main
main_sha="$(gh api "repos/$REPO/git/ref/heads/main" --jq .object.sha)"
for branch in staging develop; do
  gh api -X POST "repos/$REPO/git/refs" -f ref="refs/heads/$branch" -f sha="$main_sha"
done
```

Each push runs that stage's promote. On empty stables the first run applies every Flyway version, which initializes the stage. Check the runs under **Actions** before continuing.

### 5. Apply the rulesets

Apply them only after the branches exist, because they block direct pushes. Commands: [docs/GITHUB-RULESETS.md](docs/GITHUB-RULESETS.md#apply-with-gh).

### Verify

```bash
gh api "repos/$REPO/environments" --jq '.environments[].name'
for stage in dev staging production; do
  gh secret list --env "$stage-platform" --repo "$REPO"
  gh secret list --env "$stage-database" --repo "$REPO"
  gh variable list --env "$stage-database" --repo "$REPO"
done
gh api "repos/$REPO/actions/runners" --jq '.runners[] | .name + " " + .status'
```

## Using the library in an app

Register one shared data source; it rotates the Lakebase credential automatically:

```csharp
builder.Services.AddLakebaseDbContext<AppDbContext>(DatabaseConnectionSettings.Load());
```

Do not build an `NpgsqlDataSource` inside an `AddDbContext` options callback. That creates a new connection pool and mints a new credential on every request.

Add `--json` on CLI commands for machine-readable reports (progress on stderr, result on stdout). Failures return a non-zero exit code in both modes.
