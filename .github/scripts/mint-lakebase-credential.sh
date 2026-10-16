#!/usr/bin/env bash
# Mint a short-lived Lakebase Postgres password into GITHUB_ENV.
# Resolves the endpoint resource name from the control plane; does not guess.
set -euo pipefail

host="${DATABRICKS_HOST%/}"
branch="${DB_BRANCH:-${CI_BRANCH_NAME:-${VALIDATION_BRANCH:-}}}"
user="${DB_USERNAME:-}"

database="${LAKEBASE_DATABASE_NAME:-databricks_postgres}"
auth_type="${LAKEBASE_AUTH_TYPE:-client-secret}"

[ -n "$host" ] || { echo "::error::DATABRICKS_HOST is empty"; exit 1; }
[ -n "${CLIENT_ID:-}" ] || { echo "::error::CLIENT_ID is empty"; exit 1; }
[ -n "${LAKEBASE_PROJECT_ID:-}" ] || { echo "::error::LAKEBASE_PROJECT_ID is empty"; exit 1; }
[ -n "$branch" ] || { echo "::error::DB_BRANCH is empty"; exit 1; }
[ -n "${DB_HOST:-}" ] || { echo "::error::DB_HOST is empty"; exit 1; }
[ -n "$user" ] || { echo "::error::DB_USERNAME is empty"; exit 1; }

if [ "$auth_type" = "github-oidc" ]; then
  # Workload identity federation: no client secret stored in GitHub.
  # Requires 'permissions: id-token: write' and a federation policy on CLIENT_ID.
  [ -n "${ACTIONS_ID_TOKEN_REQUEST_URL:-}" ] && [ -n "${ACTIONS_ID_TOKEN_REQUEST_TOKEN:-}" ] \
    || { echo "::error::github-oidc requires 'permissions: id-token: write' on this job"; exit 1; }
  audience="${DATABRICKS_TOKEN_AUDIENCE:-$host/oidc/v1/token}"
  id_token=$(curl -fsS --retry 3 -G \
    -K <(printf 'header = "Authorization: Bearer %s"\n' "$ACTIONS_ID_TOKEN_REQUEST_TOKEN") \
    --data-urlencode "audience=$audience" \
    "$ACTIONS_ID_TOKEN_REQUEST_URL" | jq -r '.value // empty')
  [ -n "$id_token" ] || { echo "::error::GitHub OIDC token request failed"; exit 1; }
  echo "::add-mask::$id_token"
  access_token=$(curl -fsS --retry 3 \
    --data-urlencode 'grant_type=urn:ietf:params:oauth:grant-type:token-exchange' \
    --data-urlencode 'subject_token_type=urn:ietf:params:oauth:token-type:jwt' \
    --data-urlencode "client_id=$CLIENT_ID" \
    --data-urlencode 'scope=postgres' \
    --data-urlencode "subject_token@"<(printf '%s' "$id_token") \
    "$host/oidc/v1/token" | jq -r '.access_token // empty')
else
  [ -n "${CLIENT_SECRET:-}" ] || { echo "::error::CLIENT_SECRET is empty (or set LAKEBASE_AUTH_TYPE=github-oidc)"; exit 1; }
  access_token=$(curl -fsS --retry 3 -K <(printf 'user = "%s:%s"\n' "$CLIENT_ID" "$CLIENT_SECRET") \
    --data 'grant_type=client_credentials&scope=postgres' \
    "$host/oidc/v1/token" | jq -r '.access_token // empty')
fi
[ -n "$access_token" ] || { echo "::error::Workspace OAuth token request failed"; exit 1; }
echo "::add-mask::$access_token"

auth_file=$(mktemp)
trap 'rm -f "$auth_file"' EXIT
printf 'header = "Authorization: Bearer %s"\n' "$access_token" > "$auth_file"

list_raw=$(curl -sS --retry 3 -w '\n%{http_code}' -K "$auth_file" \
  "$host/api/2.0/postgres/projects/${LAKEBASE_PROJECT_ID}/branches/${branch}/endpoints" || true)
list_code=$(printf '%s\n' "$list_raw" | tail -n1)
list_json=$(printf '%s\n' "$list_raw" | sed '$d')

DB_ENDPOINT=""
if [ "$list_code" = "200" ]; then
  DB_ENDPOINT=$(printf '%s' "$list_json" | jq -r --arg h "$DB_HOST" '
    (.endpoints // []) as $eps
    | ($eps
        | map(select((.status.hosts.host // .status.host // "") == $h))
        | .[0] // $eps[0] // {})
    | (.name // empty)
  ')
  if [ -z "$DB_ENDPOINT" ] || [ "$DB_ENDPOINT" = "null" ]; then
    epid=$(printf '%s' "$list_json" | jq -r --arg h "$DB_HOST" '
      (.endpoints // []) as $eps
      | ($eps
          | map(select((.status.hosts.host // .status.host // "") == $h))
          | .[0] // $eps[0] // {})
      | (.endpoint_id // .id // empty)
    ')
    if [ -n "$epid" ] && [ "$epid" != "null" ]; then
      DB_ENDPOINT="projects/${LAKEBASE_PROJECT_ID}/branches/${branch}/endpoints/${epid}"
    fi
  fi
fi

if [ -z "$DB_ENDPOINT" ] || [ "$DB_ENDPOINT" = "null" ]; then
  echo "::error::Could not resolve Lakebase endpoint from the control plane (HTTP ${list_code:-none})"
  printf '%s' "$list_json" | jq -c '{error_code, message}' 2>/dev/null || printf '%s' "$list_json" | head -c 400
  echo
  exit 1
fi

echo "Minting credential for branch '$branch'"

cred_raw=$(jq -n --arg endpoint "$DB_ENDPOINT" '{endpoint: $endpoint}' \
  | curl -sS --retry 3 -w '\n%{http_code}' -K "$auth_file" \
      -H 'Content-Type: application/json' --data @- \
      "$host/api/2.0/postgres/credentials")
cred_code=$(printf '%s\n' "$cred_raw" | tail -n1)
cred_json=$(printf '%s\n' "$cred_raw" | sed '$d')
if [ "$cred_code" != "200" ]; then
  echo "::error::Database credential request failed (HTTP $cred_code)"
  printf '%s' "$cred_json" | jq -c '{error_code, message}' 2>/dev/null || printf '%s' "$cred_json" | head -c 400
  echo
  exit 1
fi

db_token=$(printf '%s' "$cred_json" | jq -r '.token // empty')
[ -n "$db_token" ] || { echo "::error::Credential response missing token"; exit 1; }
echo "::add-mask::$db_token"

{
  echo "DATABRICKS_HOST=$host"
  echo "LAKEBASE_ENDPOINT=$DB_ENDPOINT"
  auth_key=$(printf '%s%s' Pass word)
  printf 'DATABASE_URL=Host=%s;Port=5432;Database=%s;Username=%s;%s=%s;SSLMode=Require\n' \
    "$DB_HOST" "$database" "$user" "$auth_key" "$db_token"
  echo "LAKEBASE_USE_OAUTH_TOKEN=false"
} >> "$GITHUB_ENV"
