# Lab: Lakebase branches + Flyway migrations from VS Code

You will use the **Lakebase SCM** VS Code extension with an ASP.NET Core API. Each git branch gets its own copy-on-write Lakebase database branch. The app's models are ahead of the database: two Swagger endpoints are broken until you drag the staged migrations into place and run them on your branch, while `production` stays untouched.

Time: about 45 minutes.

## How the pieces fit

| Piece | Role |
|-------|------|
| `db/migrations/` | Live Flyway ledger. Starts with `V1__initial_create.sql` only. |
| `lab/migrations/` | Staged lab steps (`V2`, `V3`). Flyway ignores this folder until you copy a file into `db/migrations/`. |
| `lab/seed/seed.sql` | ~10k rows of sample members, providers, and care events for the V1 schema. Idempotent; not a migration. |
| `src/SampleApp` | API. Its EF models already expect `Members.Email`, `Providers.Email`, and `Providers.Notes`. |
| Lakebase SCM extension | Creates the Lakebase branch when you switch git branches and writes its connection to `.env`. |
| `scripts/flyway-migrate.sh` | What the extension's **Run Migrations** button runs; it calls `lakebase db migrate` (pinned Flyway 11.20.3). |
| `.env` | Two lines you set; the extension fills in the branch connection. No passwords, PATs, or client secrets. |

| Database at | `GET /api/members` | `GET /api/providers` | `GET /api/careevents/member/{id}` |
|-------------|--------------------|----------------------|-----------------------------------|
| V1 (seeded) | 500 `column m.Email does not exist` | 500 `column p.Email does not exist` | 200, seeded events |
| V1 + V2 | 200, 4,005 members (`email` null) | 500 | 200 |
| V1 + V2 + V3 | 200 | 200, 1,003 providers; `notes` holds the old `CommentsText` | 200 |

## Facilitator setup (once per workshop)

1. Create the Lakebase project (any name; this lab uses `lakebase-dotnet-lab`):
   ```bash
   databricks postgres create-project lakebase-dotnet-lab -p <profile> \
     --json '{"spec":{"display_name":"Lakebase .NET SCM lab"}}'
   ```
2. Apply V1 to the project's `production` branch so every learner's branch starts from it. From the repo root with a `.env` on `main` (see step 2 below):
   ```bash
   dotnet run --project src/Lakebase.Cli -- lakebase db migrate
   ```
3. Learners other than the project owner need a Postgres role on the branches they use; grant the workshop group access to the project in the Lakebase UI.

## Prerequisites

- VS Code with the **Lakebase SCM** extension 0.7.1 or later (0.6.x has no .NET support). Install the VSIX with `code --install-extension lakebase-scm-extension-<version>.vsix`. VS Code will also suggest C# Dev Kit from `.vscode/extensions.json`.
- .NET SDK 10.0.401+ on `PATH` (`dotnet --version`). VS Code's terminal must see it, since the Run Migrations button runs `dotnet`.
- Databricks CLI, signed in: `databricks auth login --host https://fevm-real-time-mode-demo.cloud.databricks.com`
- Pinned Flyway: `dotnet run --project src/Lakebase.Cli -- lakebase db setup`

## 1. Connect the repo

```bash
git clone <your fork> && cd lakebase-dotnet-devops
cp .env.example .env
code .
```

`.env` needs only `LAKEBASE_PROJECT_ID` and `DATABRICKS_HOST` (or `DATABRICKS_CONFIG_PROFILE` if you have several CLI profiles for the workspace). The extension recognizes the project from `LAKEBASE_PROJECT_ID`; you do **not** need **Set Up Existing Project**. If you run it anyway, it rewrites `.env.example` and `.env` from its own template; restore the repo's copy with `git checkout -- .env.example`.

**Expect:** the Lakebase SCM view lists the project and its `production` branch.

## 2. Load sample data and look at the broken baseline

On `main`, the extension points `.env` at `production`. Load the sample data there (**Terminal → Run Task → Lab: seed sample data**):

```bash
dotnet run --project src/Lakebase.Cli -- lakebase db seed
```

**Expect:** `Inserted 10012 row(s)` (4,005 members, 1,003 providers, 5,004 care events) in a few seconds the first time, `Inserted 0 row(s)` on any re-run (it skips rows that exist).

Run the API (**Lab: run SampleApp (Swagger)**) and open http://localhost:5000/swagger.

- `GET /api/careevents/member/1` → **200** with Jane Doe's seeded events
- `GET /api/members` → **500**, `column m.Email does not exist`
- `GET /api/providers` → **500**, `column p.Email does not exist`

The data is there, but the C# models are ahead of the schema. Stop the app.

## 3. Branch the code and the database

```bash
git checkout -b feature/member-email
```

A terminal checkout is enough: the extension watches `.git/HEAD` (and polls every 5 seconds), so it reacts to the terminal, the Source Control UI, or its own **Create Branch** command alike. VS Code must be open on the repo.

**Expect:** a notification `Lakebase branch "feature-member-email" created and connected.` `.env` now has `LAKEBASE_BRANCH_ID=feature-member-email`, a new `LAKEBASE_HOST`, and `DB_USERNAME` (your email). This branch is a copy-on-write copy of `production`: same schema and the same ~10k seeded rows, created in seconds without copying the data.

## 4. Stage V2 and migrate the branch

Drag `lab/migrations/V2__add_member_email.sql` into `db/migrations/` (or `cp lab/migrations/V2__add_member_email.sql db/migrations/`).

Click **Run Migrations** in the Lakebase SCM view, or run the **Lab: migrate** task. Flyway prints `Successfully applied 1 migration ... now at version v2`.

**Expect:**
- `GET /api/members` → 200 with the 4,005 seeded members (`email` null); `POST /api/members` with an `email` round-trips.
- `GET /api/providers` → still 500.
- The extension's schema diff for the branch shows `Members.Email` added versus `production`.
- `production` is unchanged: `git checkout main`, run the app, and members is still 500.

## 5. Stage V3

Back on `feature/member-email` (or a new branch), copy `lab/migrations/V3__provider_email_and_notes.sql` into `db/migrations/` and run migrations again.

**Expect:** `GET /api/providers` → 200 with the 1,003 seeded providers; each `notes` value is the old `CommentsText`, carried through the rename (e.g. `GET /api/providers/1` → Dr. Ada Lovelace, "Accepting new patients"). **Lab: integration tests** pass (8 tests).

## 6. Commit it

```bash
git add db/migrations
git commit -m "Add member and provider email columns"
```

Never edit a migration after it has been applied anywhere; fix forward with a new `V{n}` file.

## 7. Clean up

Delete the feature branch from the extension (it deletes the Lakebase branch too), or:

```bash
git checkout main
git branch -D feature/member-email
LAKEBASE_ALLOW_BRANCH_DELETE=true dotnet run --project src/Lakebase.Cli -- lakebase platform branch delete feature-member-email
```

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `A new access token could not be retrieved because the refresh token is invalid` | `databricks auth login --host <workspace-url>` |
| `No branch connection in .env` | Switch git branches once so the extension writes the connection, or click **Refresh Credentials**. |
| `global.json` / SDK not found from the button | VS Code's terminal is using an older `dotnet`; put .NET 10 first on `PATH` and reload the window. |
| Extension shows no .NET support | Upgrade to extension 0.7.1+. |
| Flyway says a version is `Future` | The database has a migration that isn't in your `db/migrations/` (e.g. applied on another branch); copy it in or switch branches. |

## Notes for the repo's CI

The repo's GitHub workflows (PR `pr-N` branches and develop → staging → main promotion) authenticate as service principals stored in GitHub environments; see the README. The extension's own CI templates (`pr.yml`, `merge.yml`, `cleanup-orphans.yml`) are intentionally not used here: they would create a second database branch per PR and migrate `develop`/`staging`/`main` twice.
