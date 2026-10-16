# Threat model

Scope: the GitHub Actions pipeline, the `Lakebase` library/CLI, and the Lakebase projects it
targets. Goal: PR authors can propose schema changes, but only reviewed, merged SQL reaches a
stable branch, and no PR-controlled code ever holds Lakebase credentials.

## Assets

| Asset | Where it lives |
|-------|----------------|
| Platform SP credential (branch/role control plane) | `<env>-platform` GitHub environments |
| Database SP credential (Postgres migrate) | `<env>-database` GitHub environments |
| Stable branch data (Dev / Staging / Production) | Lakebase projects |
| Short-lived Postgres passwords (≈1 h) | Job memory / masked `GITHUB_ENV` only |

## Trust boundaries

| Lane | Runs | Credentials | Runner |
|------|------|-------------|--------|
| `pr-branch.yml` (untrusted) | PR code: build, unit tests, offline lint | None | `vars.LAKEBASE_UNTRUSTED_RUNNER` (default `self-hosted`) |
| `pr-lakebase.yml` (trusted) | Default-branch CLI; applies **only** PR Flyway SQL to an isolated `pr-N` branch | Dev platform + Dev database | `vars.LAKEBASE_TRUSTED_RUNNER` (default `self-hosted`) |
| `*-promote.yml` (trusted) | Merged SHA; snapshot → (Production: COW rehearsal migrate) → temporary write → migrate stable → restore read-only | Per-stage environments, branch-restricted | Trusted runner |

## Threats and controls

| Threat | Control | Residual risk / owner action |
|--------|---------|------------------------------|
| PR code exfiltrates secrets | Untrusted lane has read-only `GITHUB_TOKEN`, no environment, no secrets; trusted lane never executes PR code | — |
| PR poisons a shared runner that later runs privileged jobs | Untrusted and trusted lanes use separate runner variables; .NET installs per job under `RUNNER_TEMP`; PRs limited to same-repo branches | **Open:** both default to the same self-hosted runner. Move `LAKEBASE_UNTRUSTED_RUNNER` to an ephemeral or separate pool |
| PR SQL reads data on the `pr-N` branch | `pr-N` is a copy-on-write clone of **Dev** stable, never Production | **Dev must not contain production or regulated data.** Seed with synthetic or masked data |
| PR SQL abuses table-owner privilege | Runs only on the isolated `pr-N` branch with a 24 h TTL; symlinks rejected; `flyway.conf` validated by the trusted linter | Malicious SQL can still damage `pr-N`; reviewers must read migrations before merge |
| Fork PRs | Same-repo check on every job | — |
| App SP left writable on a stable branch | Workflow: `restore-stable-readonly` runs with `always()`. CLI: `promote` restores read-only in a `finally` block and prints a `SECURITY:` line if restore fails | Monitor for the `SECURITY:` line / failed restore job |
| Bad migration on a stable branch | Pre-migrate snapshot (48 h TTL, not migrated); Production migrates a COW rehearsal branch before elevating stable; Lakebase point-in-time restore | Snapshot is evidence, not automatic rollback; a failed rehearsal leaves the `rehearse-*` branch and does not write stable |
| Long-lived client secrets leak | Secrets scoped per environment; optional GitHub OIDC federation (`LAKEBASE_AUTH_TYPE=github-oidc`) removes stored secrets | Migrate to OIDC; rotate secrets until then |
| Token or password in logs | Tokens masked (`::add-mask::`), Flyway output redacted, error bodies truncated | — |
| Supply chain (actions, packages, Flyway) | Actions pinned to SHAs; NuGet locked restore; Flyway pinned with SHA-256 checksum; Dependabot | Review Dependabot PRs |
| Connection exhaustion / credential-API throttling | One process-wide `NpgsqlDataSource` (`AddLakebaseDbContext`); credentials cached until near expiry; 429/5xx retry with backoff | — |
| TLS interception | Connections require TLS | Use `SSLMode=VerifyFull` for deployed apps |

## Enabling GitHub OIDC (recommended)

1. Create a federation policy on each service principal that trusts this repository and the
   specific GitHub environment (`repo:<owner>/<repo>:environment:<env>`).
2. In each privileged job, add `id-token: write` to `permissions`.
3. Set repository/environment variables `LAKEBASE_AUTH_TYPE=github-oidc` and, if your policy
   uses a custom audience, `DATABRICKS_TOKEN_AUDIENCE`. Pass them to the CLI and
   `mint-lakebase-credential.sh` steps, then delete the `*_CLIENT_SECRET` secrets.

The OIDC path is implemented but has not been exercised end to end in this repository yet.
Validate it in Dev before you remove the secrets.
