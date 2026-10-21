#!/usr/bin/env bash
# Entry point for the Lakebase SCM extension's "Run Tests" button. Runs unit and integration
# tests against the branch in .env. Integration tests that need a migration the branch doesn't
# have yet are skipped, and the output says which migration to apply.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet test "$@" --logger "console;verbosity=normal"
