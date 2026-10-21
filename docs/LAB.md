# Lab: Lakebase branches + Flyway migrations from VS Code

You will use the **Lakebase SCM** VS Code extension with an ASP.NET Core API. Every git branch gets its own copy-on-write Lakebase database branch. The C# models are ahead of the database, so two API endpoints fail until you apply the staged migrations on your branch, while `production` stays untouched.

Time: about 45 minutes. Run every command from the repo root. Each line in a code block is a separate command: run them in order, one line at a time (pasting the whole block into the terminal also works). Don't join two lines into one.

You need two terminals open in VS Code (**Terminal → New Terminal** twice):

- **Terminal A** for commands.
- **Terminal B** for the API, which keeps running until you press `Ctrl+C`.

The repo pieces you will touch:

- `db/migrations/` is the live Flyway ledger. It starts with only `V1__initial_create.sql`.
- `lab/migrations/` holds the staged lab migrations (`V2`, `V3`). Flyway ignores them until you copy them into `db/migrations/`.
- `lab/seed/seed.sql` is about 10,000 rows of sample data. `lab/data/branch-changes.sql` is a small set of edits for step 6.
- `scripts/flyway-migrate.sh` and `scripts/run-tests.sh` are what the extension's **Run Migrations** and **Run Tests** buttons run. Running them yourself does exactly the same thing.

## Facilitator setup (once per workshop)

1. Create an empty Lakebase project. This lab uses `lakebase-dotnet-lab`:

   ```bash
   databricks postgres create-project lakebase-dotnet-lab -p <profile> \
     --json '{"spec":{"display_name":"Lakebase .NET SCM lab"}}'
   ```

2. Steps 2 and 3 change `production`. With several learners, give each learner their own project, or have one person do steps 2 and 3 for everyone.

3. Learners other than the project owner need a Postgres role on the branches they use. Grant the workshop group access to the project in the Lakebase UI.

## Prerequisites

1. **Lakebase SCM extension v0.7.2.** It isn't on the Marketplace, so install the VSIX from the [v0.7.2 release](https://github.com/databricks-solutions/lakebase-scm-extension/releases/tag/v0.7.2):

   Download it:

   ```bash
   curl -fLO https://github.com/databricks-solutions/lakebase-scm-extension/releases/download/v0.7.2/lakebase-scm-extension-0.7.2.vsix
   ```

   Install it in Cursor:

   ```bash
   cursor --install-extension lakebase-scm-extension-0.7.2.vsix
   ```

   Or in VS Code:

   ```bash
   code --install-extension lakebase-scm-extension-0.7.2.vsix
   ```

   If the command isn't found, use **Extensions → … → Install from VSIX…** and pick the downloaded file. Reload the window afterwards. The Extensions view should show **Lakebase SCM** version 0.7.2.

2. **.NET SDK 10.0.401.** Install it from the [.NET 10 page](https://dotnet.microsoft.com/download/dotnet/10.0), or without admin rights:

   ```bash
   curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
   bash dotnet-install.sh --version 10.0.401 --install-dir ~/.dotnet
   export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
   ```

   Add that `export` line to your shell profile (`~/.bash_profile` or `~/.zshrc`) so VS Code's terminals get it too. Then quit and reopen VS Code.

3. **Databricks CLI, signed in:**

   ```bash
   databricks auth login --host https://fevm-real-time-mode-demo.cloud.databricks.com
   databricks auth profiles
   ```

   Note the name of a profile for this host whose `Valid` column is `YES`. You'll put it in `.env` in step 1.

## 1. Connect the repo

1. Clone the repo and create your `.env`:

   ```bash
   git clone https://github.com/marcjimz/lakebase-dotnet-devops.git
   cd lakebase-dotnet-devops
   cp .env.example .env
   ```

2. Open `.env` and set these three lines. They are the only ones you edit:

   ```dotenv
   LAKEBASE_PROJECT_ID=lakebase-dotnet-lab
   DATABRICKS_HOST=https://fevm-real-time-mode-demo.cloud.databricks.com
   DATABRICKS_CONFIG_PROFILE=<your profile from databricks auth profiles>
   ```

   Leave the rest alone. The extension fills in the branch connection (`LAKEBASE_BRANCH_ID`, `LAKEBASE_HOST`, `DB_USERNAME`) when you switch branches. Until it does, the app and the migrate script pair your git branch with its Lakebase branch themselves (`main` → `production`).

3. Install the pinned Flyway:

   ```bash
   dotnet run --project src/Lakebase.Cli -- lakebase db setup
   ```

   Expect `==> Installed Flyway 11.20.3`, or `==> Flyway 11.20.3 at ...` if it's already installed.

4. Open the repo folder in your editor. Skip this if it's already open. In Cursor:

   ```bash
   cursor .
   ```

   In VS Code:

   ```bash
   code .
   ```

   If you get `command not found`, use **File → Open Folder…** instead, or run **Shell Command: Install 'cursor' command in PATH** (or `'code'` in VS Code) from the Command Palette.

5. Confirm the setup in Terminal A:

   ```bash
   dotnet --version
   git branch --show-current
   ```

   Expect `10.0.401` and `main`. The Lakebase SCM view lists the `lakebase-dotnet-lab` project, with `main` under **Current Tier**.

Do **not** run **Set Up Existing Project**: the three `.env` lines are enough. If you run it anyway, it rewrites `.env.example` and `.env`. Restore them with `git checkout -- .env.example` and redo step 1.2.

## 2. Deploy V1 to production

`production` is empty. On `main`, click **Run Migrations** in the Lakebase SCM view, or run the same command in Terminal A:

```bash
./scripts/flyway-migrate.sh
```

Expect:

```
Migrating schema "public" to version "1 - initial create"
Successfully applied 1 migration to schema "public", now at version v1
...
Schema version: 1
| Versioned | 1       | initial create        | SQL      | ... | Success  | No       |
```

Running it again prints `Schema "public" is up to date. No migration necessary.`

## 3. Load the sample data

Load about 10,000 rows into `production`:

```bash
dotnet run --project src/Lakebase.Cli -- lakebase db seed
```

Expect:

```
==> Seeding projects/lakebase-dotnet-lab/branches/production/endpoints/primary from lab/seed/seed.sql
==> 10012 row(s) inserted or updated (re-runs skip existing rows)
```

That is 4,005 members, 1,003 providers, and 5,004 care events. Running it again inserts `0` rows.

## 4. See the broken baseline

1. Start the API in **Terminal B**:

   ```bash
   dotnet run --project src/SampleApp
   ```

   Wait for `Now listening on: http://localhost:5000`. Swagger is at http://localhost:5000/swagger if you prefer a browser.

2. Call the three endpoints in **Terminal A**:

   ```bash
   curl -i http://localhost:5000/api/careevents/member/1 | head -1
   curl -i http://localhost:5000/api/members | head -1
   curl -i http://localhost:5000/api/providers | head -1
   ```

   Expect `HTTP/1.1 200 OK` for care events and `HTTP/1.1 500 Internal Server Error` for members and providers.

3. See why members and providers fail:

   ```bash
   curl -s http://localhost:5000/api/members | head -1
   curl -s http://localhost:5000/api/providers | head -1
   ```

   Expect:

   ```
   Npgsql.PostgresException (0x80004005): 42703: column m.Email does not exist
   Npgsql.PostgresException (0x80004005): 42703: column p.Email does not exist
   ```

   The data is there, but the C# models expect columns the V1 schema doesn't have yet.

4. Stop the API: press `Ctrl+C` in Terminal B.

5. Run the tests. Click **Run Tests** in the Lakebase SCM view, or run the same command:

   ```bash
   ./scripts/run-tests.sh
   ```

   Expect `Total tests: 79`, `Passed: 68`, `Skipped: 11`. Unit tests and the V1-level integration tests (care events, paging validation) pass. Every member and provider test is skipped with a message such as `Needs schema V2; this branch is at V1. Copy lab/migrations/V2__*.sql into db/migrations and run ./scripts/flyway-migrate.sh.` Tests read the branch's Flyway version, so they never fail just because a migration hasn't been applied yet.

## 5. Branch the code and the database

1. Create a feature branch in Terminal A:

   ```bash
   git checkout -b feature/member-email
   ```

   The extension watches `.git/HEAD`, so a terminal checkout is enough while VS Code is open on the repo. Within a few seconds it shows `Lakebase branch "feature-member-email" created and connected.`

2. Confirm that `.env` now points at the new branch:

   ```bash
   grep -E '^(LAKEBASE_BRANCH_ID|LAKEBASE_HOST)=' .env
   ```

   Expect `LAKEBASE_BRANCH_ID=feature-member-email` and a new `LAKEBASE_HOST`. This branch is a copy-on-write copy of `production`: the same V1 schema and the same 10,012 rows, created in seconds without copying data.

## 6. Change data on the branch, compare, and reset

You'll query both branches with SQL, change data on yours, check the API on each branch, then reset your branch from `production` and apply the changes again.

The SQL commands use `databricks psql`, which signs in with your CLI profile. Tell it which profile to use, once, in Terminal A:

```bash
export DATABRICKS_CONFIG_PROFILE=<your profile from step 1>
```

Table and column names are mixed-case, so keep the double quotes: `public."CareEvents"`, not `public.CareEvents`.

1. Count care events on both branches before changing anything:

   ```bash
   databricks psql --project lakebase-dotnet-lab --branch production -- -c 'select count(*) from public."CareEvents"'
   ```

   ```bash
   databricks psql --project lakebase-dotnet-lab --branch feature-member-email -- -c 'select count(*) from public."CareEvents"'
   ```

   Expect `5004` from both. Your branch is still an exact copy.

2. Apply the branch-only changes to your branch: 3 new care events for Jane Doe and one edited description. Read `lab/data/branch-changes.sql` first if you like.

   ```bash
   dotnet run --project src/Lakebase.Cli -- lakebase db seed lab/data/branch-changes.sql
   ```

   Expect `==> Seeding projects/lakebase-dotnet-lab/branches/feature-member-email/endpoints/primary ...` and `==> 4 row(s) inserted or updated`. A re-run reports `0`.

3. Count again on both branches:

   ```bash
   databricks psql --project lakebase-dotnet-lab --branch production -- -c 'select count(*) from public."CareEvents"'
   ```

   ```bash
   databricks psql --project lakebase-dotnet-lab --branch feature-member-email -- -c 'select count(*) from public."CareEvents"'
   ```

   Expect `5004` on `production` and `5007` on your branch.

4. See Jane's events on your branch:

   ```bash
   databricks psql --project lakebase-dotnet-lab --branch feature-member-email -- -c 'select "EventType", "Description" from public."CareEvents" where "MemberId" = 1'
   ```

   Expect 5 rows: `Routine blood panel`, `Annual wellness exam (edited on branch)`, `Branch-only: follow-up call`, `Branch-only: chest X-ray`, and `Branch-only: new prescription`.

5. Now check the same thing through the API. Start it in Terminal B with `dotnet run --project src/SampleApp`, then call it in Terminal A:

   ```bash
   curl -s http://localhost:5000/api/careevents/member/1
   ```

   Expect the same 5 events. Press `Ctrl+C` in Terminal B.

6. Switch to `main` and confirm the extension points `.env` back at `production`:

   ```bash
   git checkout main
   grep '^LAKEBASE_BRANCH_ID=' .env
   ```

   Expect `LAKEBASE_BRANCH_ID=production`. Wait a few seconds and repeat the `grep` if it still shows the feature branch.

7. Start the API again in Terminal B (it reads `.env` at startup), then repeat the call:

   ```bash
   curl -s http://localhost:5000/api/careevents/member/1
   ```

   Expect only Jane's original 2 events, `Routine blood panel` and the unedited `Annual wellness exam`. Your branch's writes never reached `production`. Press `Ctrl+C` in Terminal B.

8. Go back to your branch and confirm `.env` follows:

   ```bash
   git checkout feature/member-email
   grep '^LAKEBASE_BRANCH_ID=' .env
   ```

   Expect `LAKEBASE_BRANCH_ID=feature-member-email`.

9. Reset your branch from `production`. Reset is only available in the Lakebase UI:

   1. In the Lakebase SCM view, click the project name to open it in Databricks (or **Command Palette → Lakebase SCM: Open in Databricks Console**).
   2. Go to **Branches**.
   3. Click the **⋮** menu next to `feature-member-email` and select **Reset from parent**.
   4. Confirm.

   A reset replaces the branch's data **and schema** with the latest from its parent, so your step 6.2 changes are discarded. Connections drop for a moment, but the connection details don't change, so `.env` stays valid. You can't reset `production` (it has no parent), or a branch that has child branches.

10. Confirm the reset with SQL:

    ```bash
    databricks psql --project lakebase-dotnet-lab --branch feature-member-email -- -c 'select count(*) from public."CareEvents"'
    ```

    Expect `5004` again. Jane is back to her 2 original events.

11. Apply the branch-only changes again:

    ```bash
    dotnet run --project src/Lakebase.Cli -- lakebase db seed lab/data/branch-changes.sql
    ```

    Expect `==> 4 row(s) inserted or updated` (not `0`: the reset removed the earlier rows). Count once more:

    ```bash
    databricks psql --project lakebase-dotnet-lab --branch feature-member-email -- -c 'select count(*) from public."CareEvents"'
    ```

    Expect `5007`.

## 7. Stage V2 and migrate the branch

1. Copy V2 into the live ledger. Dragging the file in VS Code's Explorer works too.

   ```bash
   cp lab/migrations/V2__add_member_email.sql db/migrations/
   ```

2. Click **Run Migrations**, or run:

   ```bash
   ./scripts/flyway-migrate.sh
   ```

   Expect `Successfully applied 1 migration to schema "public", now at version v2`.

3. Start the API in Terminal B with `dotnet run --project src/SampleApp`, then check in Terminal A:

   ```bash
   curl -i http://localhost:5000/api/members | head -1
   curl -i http://localhost:5000/api/providers | head -1
   ```

   Expect `HTTP/1.1 200 OK` for members and still `HTTP/1.1 500 Internal Server Error` for providers.

4. Look at the members response. It's paginated: 10 members per page by default, ordered by id.

   ```bash
   curl -s http://localhost:5000/api/members
   ```

   Expect a page envelope: `"items"` holds 10 members (the first is Jane Doe, `M-1001`, with `"email":null`), followed by `"page":1,"pageSize":10,"totalCount":4005,"totalPages":401`. Ask for a different page:

   ```bash
   curl -s "http://localhost:5000/api/members?page=2&pageSize=3"
   ```

   Expect 3 members (`M-1004`, `M-1005`, `M-000001`) and `"totalPages":1335`. `pageSize` is capped at 100; try `?pageSize=500` to see the `400` validation error, `pageSize must be between 1 and 100.` Press `Ctrl+C` in Terminal B.

5. Run the tests again:

   ```bash
   ./scripts/run-tests.sh
   ```

   Expect `Passed: 76`, `Skipped: 3`. The member tests now run and pass; only the provider tests are still skipped, waiting for V3.

6. Optional: add a test for your change. Members now have an email, so a test that creates a member with an `email` and reads it back belongs with the other member tests in `src/SampleApp.Tests/IntegrationTests/MemberTests.cs`. Mark it `[RequiresSchemaFact(2)]` instead of `[Fact]` so it's skipped, not failed, on branches that don't have V2 yet. Run `./scripts/run-tests.sh` again to see it pass.

`production` doesn't have V2. If you `git checkout main`, members fails there again.

## 8. Stage V3 and run the tests

1. Copy V3 in and migrate. V3 adds `Providers.Email` and renames `CommentsText` to `Notes`.

   ```bash
   cp lab/migrations/V3__provider_email_and_notes.sql db/migrations/
   ./scripts/flyway-migrate.sh
   ```

   Expect `Successfully applied 1 migration to schema "public", now at version v3`.

2. Start the API in Terminal B, then check providers in Terminal A:

   ```bash
   curl -i http://localhost:5000/api/providers | head -1
   curl -s http://localhost:5000/api/providers/1
   ```

   Expect `HTTP/1.1 200 OK`. Provider 1 is `Dr. Ada Lovelace`, with `"notes":"Accepting new patients"`: the old `CommentsText` survived the rename. `GET /api/providers` is paginated like members: `curl -s http://localhost:5000/api/providers` returns 10 providers with `"totalCount":1003,"totalPages":101`. Press `Ctrl+C` in Terminal B.

3. Run the full test suite against your branch. Click **Run Tests**, or:

   ```bash
   ./scripts/run-tests.sh
   ```

   Expect `Total tests: 79`, `Passed: 79`, nothing skipped. The tests create a few members and providers of their own, so `totalCount` goes up afterwards.

4. Optional: add a test for the V3 change, for example a provider created with `notes` returns them, in `ProviderTests.cs`, marked `[RequiresSchemaFact(3)]`.

## 9. Commit the migrations

```bash
git add db/migrations
git commit -m "Add member and provider email columns"
```

Never edit a migration after it has been applied anywhere. Fix forward with a new `V{n}` file instead.

## 10. Deploy to production

One merge, then migrate and test `production`. Lakebase has no database merge: schema reaches `production` by running the committed Flyway files there. Data doesn't move, so your step 6 branch-only rows stay on the feature branch.

1. Merge your branch into `main`:

   ```bash
   git checkout main
   git merge feature/member-email
   ```

   Accept the default message if git opens an editor. `ls db/migrations` now lists V1, V2, and V3, and `grep '^LAKEBASE_BRANCH_ID=' .env` shows `LAKEBASE_BRANCH_ID=production` (wait a few seconds if it still shows the feature branch).

2. Run the migrations against `production`. Click **Run Migrations**, or run:

   ```bash
   ./scripts/flyway-migrate.sh
   ```

   Expect `Successfully applied 2 migrations to schema "public", now at version v3`.

3. Run the tests against `production`. Click **Run Tests**, or run:

   ```bash
   ./scripts/run-tests.sh
   ```

   Expect `Total tests: 79`, `Passed: 79`, nothing skipped. The endpoints that were broken in step 4 now work on `production`. The tests add a few test members and providers there.

## 11. Clean up

Delete the feature branch from the extension's Lakebase SCM view, which removes the Lakebase branch too. Or run:

```bash
git branch -d feature/member-email
LAKEBASE_ALLOW_BRANCH_DELETE=true dotnet run --project src/Lakebase.Cli -- lakebase platform branch delete feature-member-email
```

Expect `==> Branch deleted.` `production` keeps V1–V3 and the sample data.

## Troubleshooting

- **`A compatible .NET SDK was not found. Requested SDK version: 10.0.401`:** your terminal's `dotnet` is an older install (for example Homebrew `dotnet@8`). Put .NET 10 first on `PATH` (prerequisite 2), check `dotnet --version`, and restart VS Code.

- **`A new access token could not be retrieved because the refresh token is invalid`:** run `databricks auth login --host https://fevm-real-time-mode-demo.cloud.databricks.com` again.

- **`session does not read back as authenticated`, `cannot configure default credentials`, or `<a> and <b> match https://<workspace>`:** several CLI profiles point at the workspace. Set `DATABRICKS_CONFIG_PROFILE` in `.env` to a profile that `databricks current-user me -p <profile>` accepts, then reload the window.

- **`No branch connection: set LAKEBASE_PROJECT_ID in .env ...`:** check the three `.env` lines from step 1. The Lakebase branch for your git branch must exist: `main` maps to `production`, and the extension creates feature branches when you switch to them. Clicking **Refresh Credentials** also writes the connection.

- **No "Lakebase branch … created" notification in step 5:** VS Code must be open on this folder with extension v0.7.2. Check the Lakebase SCM view and **View → Output → Lakebase SCM**.

- **`Branches with an expiration date cannot have child branches`:** branch from `main` (`production`), not from another feature branch.

- **`relation "public.careevents" does not exist` in SQL:** Postgres lowercases unquoted names, and these tables have mixed-case names. Quote them: `select count(*) from public."CareEvents"`. If it still fails, run `\dt` to check you're on a branch that has V1.

- **Flyway lists a version as `Future`:** the database has a migration that isn't in your `db/migrations/`, for example one applied on another branch. Copy it in, or switch to the matching branch.

## Notes for the repo's CI

The repo's GitHub workflows (PR `pr-N` branches and develop → staging → main promotion) authenticate as service principals stored in GitHub environments; see the README. The extension's own CI templates (`pr.yml`, `merge.yml`, `cleanup-orphans.yml`) are intentionally not used here: they would create a second database branch per PR and migrate `develop`/`staging`/`main` twice.
