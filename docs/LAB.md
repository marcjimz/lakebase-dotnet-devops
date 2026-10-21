# Lab: Lakebase branches + Flyway migrations from VS Code

You will use the **Lakebase SCM** VS Code extension with an ASP.NET Core API. Each git branch gets its own copy-on-write Lakebase database branch. The app's models are ahead of the database: two Swagger endpoints are broken until you drag the staged migrations into place and run them on your branch, while `production` stays untouched.

Time: about 45 minutes.

## How the pieces fit

| Piece | Role |
|-------|------|
| `db/migrations/` | Live Flyway ledger. Starts with `V1__initial_create.sql` only. |
| `lab/migrations/` | Staged lab steps (`V2`, `V3`). Flyway ignores this folder until you copy a file into `db/migrations/`. |
| `lab/seed/seed.sql` | ~10k rows of sample members, providers, and care events for the V1 schema. Idempotent; not a migration. |
| `lab/data/branch-changes.sql` | Branch-only data changes (step 6) to compare a feature branch with `production`. |
| `src/SampleApp` | API. Its EF models already expect `Members.Email`, `Providers.Email`, and `Providers.Notes`. |
| Lakebase SCM extension | Creates the Lakebase branch when you switch git branches and writes its connection to `.env`. |
| `scripts/flyway-migrate.sh` | What the extension's **Run Migrations** button runs; it calls `lakebase db migrate` (pinned Flyway 11.20.3). |
| `.env` | Two lines you set; the extension fills in the branch connection. No passwords, PATs, or client secrets. |

| Database at | `GET /api/members` | `GET /api/providers` | `GET /api/careevents/member/{id}` |
|-------------|--------------------|----------------------|-----------------------------------|
| V1 (seeded) | 500 `column m.Email does not exist` | 500 `column p.Email does not exist` | 200, seeded events |
| V1 + V2 | 200, 4,005 members (`email` null) | 500 | 200 |
| V1 + V2 + V3 | 200 | 200, 1,003 providers; `notes` holds the old `CommentsText` | 200 |

## Steps at a glance

| # | Where | You do | You see |
|---|-------|--------|---------|
| 1 | `main` | Connect the repo (`cp .env.example .env`) | Project + empty `production` branch in the Lakebase SCM view |
| 2 | `main` | Deploy V1 with **Run Migrations** | Tables created on `production`, Flyway at v1 |
| 3 | `main` | Load ~10k sample rows (`lakebase db seed`) | `10012 row(s) inserted or updated` |
| 4 | `main` | Run the API | Members and providers broken (500), care events fine |
| 5 | `feature/member-email` | `git checkout -b` | Lakebase branch created with the same schema and data |
| 6 | feature, then `main` | Run `lab/data/branch-changes.sql` on the branch; compare with `main` | Branch has Jane's 3 new events + an edit; `production` doesn't |
| 7 | feature | Drag in V2, **Run Migrations** | Members fixed on the branch only |
| 8 | feature | Drag in V3, **Run Migrations** | Providers fixed, notes survive the rename, tests green |
| 9 | feature | Commit | Migrations in Git |
| 10 | `main` | Clean up | Branch deleted |

## Facilitator setup (once per workshop)

1. Create an empty Lakebase project (any name; this lab uses `lakebase-dotnet-lab`). Learners deploy the schema themselves in step 2.
   ```bash
   databricks postgres create-project lakebase-dotnet-lab -p <profile> \
     --json '{"spec":{"display_name":"Lakebase .NET SCM lab"}}'
   ```
2. With several learners, give each their own project (steps 2–3 change `production`), or have one person do steps 2–3 for everyone. Learners other than the project owner need a Postgres role on the branches they use; grant the workshop group access in the Lakebase UI.

## Prerequisites

- VS Code with the **Lakebase SCM** extension **v0.7.2** (0.6.x has no .NET support). It isn't on the Marketplace; install the VSIX from the [v0.7.2 release](https://github.com/databricks-solutions/lakebase-scm-extension/releases/tag/v0.7.2):
  ```bash
  curl -fLO https://github.com/databricks-solutions/lakebase-scm-extension/releases/download/v0.7.2/lakebase-scm-extension-0.7.2.vsix
  code --install-extension lakebase-scm-extension-0.7.2.vsix
  code --list-extensions --show-versions | grep lakebase   # kevin-hartman.lakebase-scm-extension@0.7.2
  ```
  Or download `lakebase-scm-extension-0.7.2.vsix` from the release page and use **Extensions → … → Install from VSIX…**. Reload the window afterwards. If `code` isn't on your `PATH`, run **Shell Command: Install 'code' command in PATH** from the Command Palette. VS Code will also suggest C# Dev Kit from `.vscode/extensions.json`.
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

**Expect:** the Lakebase SCM view lists the project and its `production` branch. On `main` the extension points `.env` at `production` (check `LAKEBASE_BRANCH_ID=production`); if those lines are missing, click **Refresh Credentials**.

## 2. Deploy V1 to production

`production` is empty. The **Schema Migrations** view (shown on `main`) lists the files in `db/migrations`: only `V1__initial_create.sql`. Click **Run Migrations** (or **Command Palette → Lakebase SCM: Run Migrations**, or the **Lab: migrate** task).

**Expect:** Flyway prints `Migrating schema "public" to version "1 - initial create"` and `Successfully applied 1 migration ... now at version v1`, followed by `lakebase db info` showing V1 as `Success`. The `Members`, `Providers`, and `CareEvents` tables now exist, all empty.

## 3. Load sample data

Still on `main`, load the sample data into `production` (**Terminal → Run Task → Lab: seed sample data**):

```bash
dotnet run --project src/Lakebase.Cli -- lakebase db seed
```

**Expect:** `10012 row(s) inserted or updated` (4,005 members, 1,003 providers, 5,004 care events) in a few seconds the first time, `0 row(s)` on any re-run (it skips rows that exist).

## 4. Look at the broken baseline

Run the API (**Lab: run SampleApp (Swagger)**) and open http://localhost:5000/swagger.

- `GET /api/careevents/member/1` → **200** with Jane Doe's seeded events
- `GET /api/members` → **500**, `column m.Email does not exist`
- `GET /api/providers` → **500**, `column p.Email does not exist`

The data is there, but the C# models are ahead of the schema. Stop the app.

## 5. Branch the code and the database

```bash
git checkout -b feature/member-email
```

A terminal checkout is enough: the extension watches `.git/HEAD` (and polls every 5 seconds), so it reacts to the terminal, the Source Control UI, or its own **Create Branch** command alike. VS Code must be open on the repo.

**Expect:** a notification `Lakebase branch "feature-member-email" created and connected.` `.env` now has `LAKEBASE_BRANCH_ID=feature-member-email`, a new `LAKEBASE_HOST`, and `DB_USERNAME` (your email). This branch is a copy-on-write copy of `production`: same schema and the same ~10k seeded rows, created in seconds without copying the data.

## 6. Change data on the branch and compare

Your branch's data is yours: writes stay on the branch and never reach `production`.

1. Open `lab/data/branch-changes.sql` and read it: 3 new care events for Jane Doe (`M-1001`) and one edited description. Run it on the branch (**Lab: apply branch data changes**):
   ```bash
   dotnet run --project src/Lakebase.Cli -- lakebase db seed lab/data/branch-changes.sql
   ```
   **Expect:** `4 row(s) inserted or updated` (3 inserts + 1 update); a re-run reports `0`.
2. Run the API and call `GET /api/careevents/member/1`. **Expect:** 5 events, including the three `Branch-only: …` rows and `Annual wellness exam (edited on branch)`.
3. Stop the app and `git checkout main`. Within a few seconds the extension points `.env` back at `production` (`LAKEBASE_BRANCH_ID=production`). Start the API again (it reads `.env` at startup) and call the same endpoint. **Expect:** Jane's original 2 events and the unedited `Annual wellness exam`.
4. `git checkout feature/member-email` to continue. The extension reconnects `.env` to your branch; your changes are still there.

Writing your own `INSERT`/`UPDATE` works the same way: save it as a `.sql` file and run `lakebase db seed <file>` on whichever branch is checked out.

## 7. Stage V2 and migrate the branch

Drag `lab/migrations/V2__add_member_email.sql` into `db/migrations/` (or `cp lab/migrations/V2__add_member_email.sql db/migrations/`).

Click **Run Migrations** in the Lakebase SCM view, or run the **Lab: migrate** task. Flyway prints `Successfully applied 1 migration ... now at version v2`.

**Expect:**
- `GET /api/members` → 200 with the 4,005 seeded members (`email` null); `POST /api/members` with an `email` round-trips.
- `GET /api/providers` → still 500.
- The extension's schema diff for the branch shows `Members.Email` added versus `production`.
- `production` is unchanged: `git checkout main`, run the app, and members is still 500.

## 8. Stage V3

Back on `feature/member-email` (or a new branch), copy `lab/migrations/V3__provider_email_and_notes.sql` into `db/migrations/` and run migrations again.

**Expect:** `GET /api/providers` → 200 with the 1,003 seeded providers; each `notes` value is the old `CommentsText`, carried through the rename (e.g. `GET /api/providers/1` → Dr. Ada Lovelace, "Accepting new patients"). **Lab: integration tests** pass (8 tests).

## 9. Commit it

```bash
git add db/migrations
git commit -m "Add member and provider email columns"
```

Never edit a migration after it has been applied anywhere; fix forward with a new `V{n}` file.

## 10. Clean up

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
| Extension shows no .NET support | You have an older build (0.6.x); install the [v0.7.2 VSIX](https://github.com/databricks-solutions/lakebase-scm-extension/releases/tag/v0.7.2) and reload the window. |
| `Branches with an expiration date cannot have child branches` | Lakebase branches with a TTL can't be branched from. Branch from `main` (`production`, no TTL) rather than from another feature branch. |
| Flyway says a version is `Future` | The database has a migration that isn't in your `db/migrations/` (e.g. applied on another branch); copy it in or switch branches. |

## Notes for the repo's CI

The repo's GitHub workflows (PR `pr-N` branches and develop → staging → main promotion) authenticate as service principals stored in GitHub environments; see the README. The extension's own CI templates (`pr.yml`, `merge.yml`, `cleanup-orphans.yml`) are intentionally not used here: they would create a second database branch per PR and migrate `develop`/`staging`/`main` twice.
