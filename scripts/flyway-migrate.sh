#!/usr/bin/env bash
# Entry point for the Lakebase SCM extension's "Run Migrations" button. Delegates to the
# repo's pinned Flyway (lakebase db migrate) so the button, the CLI, and CI share one
# migration ledger. Credentials are minted from your Databricks CLI login.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet run --project src/Lakebase.Cli -- lakebase db migrate "$@"
dotnet run --project src/Lakebase.Cli -- lakebase db info
