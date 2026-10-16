# GitHub rulesets (solo-safe)

Active rulesets on `sghanta-sf/lakebase-dotnet-devops` require PRs and automated validation on `develop`, `staging`, and `main`. They require **zero human approvals**, so the repository owner can exercise the complete flow alone.

CODEOWNERS remains advisory. Repository administrators have PR-only break-glass bypass; direct pushes, force pushes, and branch deletion remain blocked.

Required PR check:

- `validate`

After `pr-lakebase.yml` is present on `main` and `lakebase-schema` has reported once, add `lakebase-schema` as a required check on `develop`.

## Active policy

| Branch | PR-only | Human approvals | Required checks | Force-push / delete | Bypass |
|--------|---------|-----------------|-----------------|---------------------|--------|
| `develop` | yes | 0 | `validate` | blocked | repository admins, PR merge only |
| `staging` | yes | 0 | `validate` | blocked | repository admins, PR merge only |
| `main` | yes | 0 | `validate` | blocked | repository admins, PR merge only |

## Apply with `gh`

These commands target the personal application repository, not the organization mirror.

```bash
REPO="${REPO:-sghanta-sf/lakebase-dotnet-devops}"

# Inspect existing rulesets
gh api "repos/$REPO/rulesets" --jq '.[].name'

create_ruleset() {
  local name="$1" pattern="$2"
  gh api -X POST "repos/$REPO/rulesets" --input - <<EOF
{
  "name": "$name",
  "target": "branch",
  "enforcement": "active",
  "conditions": {
    "ref_name": {
      "include": ["refs/heads/$pattern"],
      "exclude": []
    }
  },
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    {
      "type": "pull_request",
      "parameters": {
        "required_approving_review_count": 0,
        "dismiss_stale_reviews_on_push": false,
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_review_thread_resolution": false,
        "require_extra_approval_for_unattributed_changes": false
      }
    },
    {
      "type": "required_status_checks",
      "parameters": {
        "strict_required_status_checks_policy": false,
        "do_not_enforce_on_create": true,
        "required_status_checks": [
          { "context": "validate" }
        ]
      }
    }
  ],
  "bypass_actors": [
    {
      "actor_id": 5,
      "actor_type": "RepositoryRole",
      "bypass_mode": "pull_request"
    }
  ]
}
EOF
}

create_ruleset "Protect develop (solo)" "develop"
create_ruleset "Protect staging (solo)" "staging"
create_ruleset "Protect main (solo)" "main"
```

Do not require `lakebase-schema` until `pr-lakebase.yml` exists on `main` and that check has run once:

```bash
RULESET_ID=<id>
gh api -X PUT "repos/$REPO/rulesets/$RULESET_ID" --input payload.json
```

## GitHub environments (admin)

Create the six environments (`dev-platform`, `dev-database`, `staging-platform`, `staging-database`, `production-platform`, `production-database`), their branch policies, secrets, and variables with the manual steps in [README → GitHub setup](../README.md#github-setup). Do not copy env files or secret values into this repository.

Active protections:

- `dev-platform` / `dev-database`: deployment branches = `develop` and `main`
- `staging-platform` / `staging-database`: deployment branches = `staging`
- `production-platform` / `production-database`: deployment branches = `main`
- no required reviewers, wait timers, or prevent-self-review gate

`main` is required on the Dev pair because trusted `pull_request_target` jobs (`pr-lakebase.yml`) load workflow code from the default branch. `develop` remains allowed for Dev promote and manual feature-branch dispatch.

Secret names (stdin to `gh secret set --env`, never argv):

| Secret | Platform env | Database env |
|--------|--------------|--------------|
| `DATABRICKS_HOST` | yes | yes |
| `LAKEBASE_PLATFORM_CLIENT_ID` | yes | no |
| `LAKEBASE_PLATFORM_CLIENT_SECRET` | yes | no |
| `LAKEBASE_PROJECT_ID` | yes | no |
| `LAKEBASE_PARENT_BRANCH_ID` | yes | no |
| `LAKEBASE_APP_SP_ID` | yes | optional (or set repo `vars.LAKEBASE_APP_SP_ID`) |
| `LAKEBASE_DEV_GROUP` | optional | no |
| `LAKEBASE_ALLOW_BRANCH_DELETE` | optional (workflows force `true` where needed) | no |
| `LAKEBASE_DATABASE_CLIENT_ID` | no | yes |
| `LAKEBASE_DATABASE_CLIENT_SECRET` | no | yes |
| `LAKEBASE_DATABASE_USERNAME` | no | yes |
| `LAKEBASE_PROD_ENDPOINT` | no | yes (stable **host**) |

Each `*-database` environment also needs the **variables** `LAKEBASE_PROJECT_ID` and `LAKEBASE_PARENT_BRANCH_ID` (used to mint the database credential).

Repo / `dev-database` variable `LAKEBASE_APP_SP_ID`: App SP application (client) id. Feature-branch create transfers table ownership to this role so developers can `lakebase db migrate` locally with the same identity in `.env.database`.

`LAKEBASE_DATABASE_CLIENT_ID` / `SECRET` for feature migrate should be that App SP. `LAKEBASE_DATABASE_USERNAME` in `grant-app-ddl` is the **current table owner** (often the identity that first ran Flyway). After ownership is transferred, local and CI migrate use the App SP as `Username`.

Optional environment variable on `dev-platform`: `LAKEBASE_FEATURE_TTL_HOURS` (clamped to 24–168).
