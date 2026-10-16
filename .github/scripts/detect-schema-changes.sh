#!/usr/bin/env bash
# Compare HEAD to a base via the GitHub compare API. Sets schema_changed=true
# when db/migrations/* or conf/flyway.conf changed. Fail-open (true) on errors.
set -euo pipefail

repo="${REPO:-${GITHUB_REPOSITORY:-}}"
head="${HEAD_SHA:-}"
[ -n "${GITHUB_OUTPUT:-}" ] || { echo "::error::GITHUB_OUTPUT is not set"; exit 1; }
[ -n "$repo" ] && [ -n "$head" ] || { echo "::error::REPO and HEAD_SHA are required"; exit 1; }

emit() {
  echo "schema_changed=$1" >> "$GITHUB_OUTPUT"
  echo "Schema changed: $1"
}

if [ "${FORCE:-}" = "true" ]; then
  echo "Forced schema detection"
  emit true
  exit 0
fi

is_zero() { [ -z "${1:-}" ] || [[ "$1" =~ ^0+$ ]]; }

base="${BASE_SHA:-}"
if is_zero "$base" && ! is_zero "${BEFORE_SHA:-}"; then
  base="$BEFORE_SHA"
fi

if is_zero "$base"; then
  for ref in "heads/develop" "heads/${DEFAULT_BRANCH:-main}"; do
    sha=$(gh api "repos/$repo/git/ref/$ref" --jq .object.sha 2>/dev/null || true)
    if [ -n "$sha" ]; then
      base="$sha"
      echo "Compare base from $ref ($base)"
      break
    fi
  done
fi

if is_zero "$base"; then
  echo "::warning::No compare base; treating as a schema change"
  emit true
  exit 0
fi

if [ "$base" = "$head" ]; then
  echo "Base and HEAD are the same commit ($head)"
  emit false
  exit 0
fi

echo "Comparing $base...$head for Flyway paths"

tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT
if ! gh api "repos/$repo/compare/${base}...${head}" > "$tmp"; then
  echo "::warning::Compare API failed; treating as a schema change"
  emit true
  exit 0
fi

file_count=$(jq -r '.files | length' "$tmp")
if [ "$file_count" = "300" ]; then
  echo "::warning::Compare file list is truncated; treating as a schema change"
  emit true
  exit 0
fi

changed=$(jq -r '
  [.files[]?.filename // empty]
  | map(select(startswith("db/migrations/") or . == "conf/flyway.conf"))
  | unique[]
' "$tmp")

if [ -n "$changed" ]; then
  echo "Schema paths changed:"
  printf '%s\n' "$changed"
  emit true
else
  emit false
fi
