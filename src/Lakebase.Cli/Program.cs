using Lakebase;
using System.CommandLine;

var jsonOpt = new Option<bool>("--json")
{
    Description = "Machine-readable JSON on stdout (progress on stderr)",
};

var platformBranchCreate = new Command("create", "Create or reuse a Lakebase branch (idempotent)");
var branchNameArg = new Argument<string>("branch-name")
{
    Description = "Branch name (sanitized to [a-z0-9-])",
};
var parentOpt = new Option<string?>("--parent")
{
    Description = "Parent branch id (default: LAKEBASE_MAIN_BRANCH_ID)",
};
var ttlOpt = new Option<int?>("--ttl-hours")
{
    Description = "TTL in hours (omit for no_expiry). CI feature branches: 24–168; PR branches: 24",
};
var recreateOpt = new Option<bool>("--recreate")
{
    Description = "Delete and recreate if the branch already exists",
};
var noWriteEnvOpt = new Option<bool>("--no-write-env")
{
    Description = "Do not update .env",
};
platformBranchCreate.Arguments.Add(branchNameArg);
platformBranchCreate.Options.Add(parentOpt);
platformBranchCreate.Options.Add(ttlOpt);
platformBranchCreate.Options.Add(recreateOpt);
platformBranchCreate.Options.Add(noWriteEnvOpt);
platformBranchCreate.Options.Add(jsonOpt);
platformBranchCreate.SetAction(async parseResult =>
{
    var branchName = parseResult.GetValue(branchNameArg)!;
    var parent = parseResult.GetValue(parentOpt);
    var ttlHours = parseResult.GetValue(ttlOpt);
    var recreate = parseResult.GetValue(recreateOpt);
    var noWriteEnv = parseResult.GetValue(noWriteEnvOpt);
    var json = parseResult.GetValue(jsonOpt);
    await RunJsonOpAsync("platform.branch.create", json, async () =>
    {
        var service = CreatePlatformService();
        return await service.CreateBranchAsync(branchName, new CreateBranchOptions
        {
            Parent = parent,
            TtlHours = ttlHours,
            Recreate = recreate,
            Idempotent = !recreate,
            WriteEnvDatabase = !noWriteEnv,
            ProvisionRoles = true,
        });
    });
});

var platformBranchDelete = new Command("delete", "Delete a Lakebase branch");
var branchRefArg = new Argument<string>("branch-ref")
{
    Description = "Branch name or ID",
};
var ignoreMissingOpt = new Option<bool>("--ignore-missing")
{
    Description = "Do not fail if the branch is already gone",
};
platformBranchDelete.Arguments.Add(branchRefArg);
platformBranchDelete.Options.Add(ignoreMissingOpt);
platformBranchDelete.Options.Add(jsonOpt);
platformBranchDelete.SetAction(async parseResult =>
{
    var branchRef = parseResult.GetValue(branchRefArg)!;
    var ignoreMissing = parseResult.GetValue(ignoreMissingOpt);
    var json = parseResult.GetValue(jsonOpt);
    await RunJsonOpAsync("platform.branch.delete", json, async () =>
    {
        var service = CreatePlatformService();
        await service.DeleteBranchAsync(branchRef, ignoreMissing);
        return new { branch = BranchName.Sanitize(branchRef), deleted = true };
    });
});

var platformBranchList = new Command("list", "List Lakebase branches in the project");
platformBranchList.Options.Add(jsonOpt);
platformBranchList.SetAction(async parseResult =>
{
    var json = parseResult.GetValue(jsonOpt);
    await RunJsonOpAsync("platform.branch.list", json, async () =>
    {
        var service = CreatePlatformService();
        var ids = await service.ListBranchIdsAsync();
        if (!json)
        {
            foreach (var id in ids)
                Console.WriteLine(id);
        }

        return new { branches = ids };
    }, writeResultWhenHuman: false);
});

var platformBranch = new Command("branch", "Branch lifecycle (copy-on-write)");
platformBranch.Subcommands.Add(platformBranchCreate);
platformBranch.Subcommands.Add(platformBranchDelete);
platformBranch.Subcommands.Add(platformBranchList);

var platformRoleProvision = new Command("provision", "Provision dev/app Postgres roles on a branch");
var roleBranchArg = new Argument<string>("branch-name")
{
    Description = "Branch name",
};
var roleWriteOpt = new Option<bool>("--write")
{
    Description = "Least-privilege write membership (databricks_postgres), including on protected/stable branches",
};
var roleReadOnlyOpt = new Option<bool>("--read-only")
{
    Description = "Empty membership (read-only). Default on protected branches",
};
platformRoleProvision.Arguments.Add(roleBranchArg);
platformRoleProvision.Options.Add(roleWriteOpt);
platformRoleProvision.Options.Add(roleReadOnlyOpt);
platformRoleProvision.Options.Add(jsonOpt);
platformRoleProvision.SetAction(async parseResult =>
{
    var branchName = parseResult.GetValue(roleBranchArg)!;
    var write = parseResult.GetValue(roleWriteOpt);
    var readOnly = parseResult.GetValue(roleReadOnlyOpt);
    var json = parseResult.GetValue(jsonOpt);
    if (write && readOnly)
        throw new InvalidOperationException("Pass only one of --write or --read-only.");
    await RunJsonOpAsync("platform.role.provision", json, async () =>
    {
        var service = CreatePlatformService();
        var sanitized = BranchName.Sanitize(branchName);
        bool? mode = write ? false : readOnly ? true : null;
        await service.ProvisionBranchRolesAsync(sanitized, mode);
        return new { branch = sanitized, provisioned = true, readOnly = mode };
    });
});

var platformRole = new Command("role", "Postgres role assignment via platform API");
platformRole.Subcommands.Add(platformRoleProvision);

var platformStaging = new Command("staging", "Migration proof: COW production → staging, apply Git Flyway SQL (does not touch production)");
var stagingApply = new Command("apply", "COW staging from production, then apply Git Flyway SQL");
var stagingFromOpt = new Option<string?>("--from")
{
    Description = "Optional feature branch to validate before staging (label for promote)",
};
var stagingNameOpt = new Option<string?>("--name")
{
    Description = "Validation branch name (default: LAKEBASE_STAGING_BRANCH_ID or 'development')",
};
var stagingRecreateOpt = new Option<bool>("--recreate")
{
    Description = "Delete and recreate staging before applying migrations",
};
var stagingTtlOpt = new Option<int?>("--ttl-hours")
{
    Description = "Optional TTL for ephemeral staging branches",
};
stagingApply.Options.Add(stagingFromOpt);
stagingApply.Options.Add(stagingNameOpt);
stagingApply.Options.Add(stagingRecreateOpt);
stagingApply.Options.Add(stagingTtlOpt);
stagingApply.Options.Add(jsonOpt);
stagingApply.SetAction(async parseResult =>
{
    var from = parseResult.GetValue(stagingFromOpt);
    var name = parseResult.GetValue(stagingNameOpt);
    var recreate = parseResult.GetValue(stagingRecreateOpt);
    var ttlHours = parseResult.GetValue(stagingTtlOpt);
    var json = parseResult.GetValue(jsonOpt);
    await RunJsonOpAsync("platform.staging.apply", json, async () =>
    {
        var service = CreatePlatformService();
        return await service.StagingApplyAsync(from, name, recreate, ttlHours);
    });
});
platformStaging.Subcommands.Add(stagingApply);

var platformPromote = new Command("promote", "Apply Git Flyway SQL to production (validated on feature), then delete staging");
var skipSnapshotOpt = new Option<bool>("--skip-snapshot")
{
    Description = "Skip pre-migrate production snapshot",
};
var snapshotNameOpt = new Option<string?>("--snapshot-name")
{
    Description = "Pre-migrate snapshot name (default: pre-migrate-<timestamp>)",
};
var noCleanupStagingOpt = new Option<bool>("--no-cleanup-staging")
{
    Description = "Keep staging branch after successful promote",
};
var promoteStagingNameOpt = new Option<string?>("--staging-name")
{
    Description = "Staging branch to delete after promote (default: staging)",
};
var promoteFromOpt = new Option<string?>("--from")
{
    Description = "Feature branch to verify before prod (recommended)",
};
platformPromote.Options.Add(skipSnapshotOpt);
platformPromote.Options.Add(snapshotNameOpt);
platformPromote.Options.Add(noCleanupStagingOpt);
platformPromote.Options.Add(promoteStagingNameOpt);
platformPromote.Options.Add(promoteFromOpt);
platformPromote.Options.Add(jsonOpt);
platformPromote.SetAction(async parseResult =>
{
    var skipSnapshot = parseResult.GetValue(skipSnapshotOpt);
    var snapshotName = parseResult.GetValue(snapshotNameOpt);
    var noCleanupStaging = parseResult.GetValue(noCleanupStagingOpt);
    var stagingName = parseResult.GetValue(promoteStagingNameOpt);
    var from = parseResult.GetValue(promoteFromOpt);
    var json = parseResult.GetValue(jsonOpt);
    await RunJsonOpAsync("platform.promote", json, async () =>
    {
        var service = CreatePlatformService();
        return await service.PromoteAsync(
            skipSnapshot,
            snapshotName,
            cleanupStaging: !noCleanupStaging,
            stagingName,
            from);
    });
});

var platformCommand = new Command("platform", "Lakebase control plane (branches, roles, snapshots, promote)");
platformCommand.Subcommands.Add(platformBranch);
platformCommand.Subcommands.Add(platformRole);
platformCommand.Subcommands.Add(platformStaging);
platformCommand.Subcommands.Add(platformPromote);

var dbGenerateToken = new Command("generate-token", "Generate a short-lived database credential");
dbGenerateToken.Options.Add(jsonOpt);
dbGenerateToken.SetAction(async parseResult =>
{
    var json = parseResult.GetValue(jsonOpt);
    await RunJsonOpAsync("db.generate-token", json, async () =>
    {
        var service = CreateDatabaseService();
        return await service.GenerateCredentialAsync();
    });
});

var dbSetup = new Command("setup", "Install the pinned Flyway CLI (checksum-verified)");
dbSetup.Options.Add(jsonOpt);
dbSetup.SetAction(async parseResult =>
{
    var json = parseResult.GetValue(jsonOpt);
    await RunJsonOpAsync("db.setup", json, async () =>
    {
        var binary = await FlywayInstaller.EnsureInstalledAsync();
        CliLog.Progress($"Flyway {FlywayInstaller.Version} at {binary}");
        return new { version = FlywayInstaller.Version, binary, home = FlywayInstaller.InstallDir };
    });
});

var dbLint = new Command("lint", "Offline Flyway SQL ledger checks (no database)");
dbLint.Options.Add(jsonOpt);
dbLint.SetAction(async parseResult =>
{
    var json = parseResult.GetValue(jsonOpt);
    await RunJsonOpAsync("db.lint", json, () =>
    {
        var result = FlywaySqlLinter.Lint();
        if (!json)
        {
            foreach (var line in result.ManifestLines)
                Console.WriteLine(line);
        }

        return Task.FromResult<object>(new { fileCount = result.FileCount, manifest = result.ManifestLines });
    }, writeResultWhenHuman: false);
});

var dbMigrate = new Command("migrate", "Apply Flyway SQL (retries Lakebase compute wake-ups)");
var noRetryOpt = new Option<bool>("--no-retry")
{
    Description = "Do not retry connection / compute wake-up failures",
};
dbMigrate.Options.Add(noRetryOpt);
dbMigrate.Options.Add(jsonOpt);
dbMigrate.SetAction(async parseResult =>
{
    var json = parseResult.GetValue(jsonOpt);
    var noRetry = parseResult.GetValue(noRetryOpt);
    await RunJsonOpAsync("db.migrate", json, async () =>
    {
        var settings = DatabaseConnectionSettings.Load();
        await FlywayRunner.MigrateAsync(settings, retryTransient: !noRetry);
        return new { migrated = true, retried = !noRetry };
    });
});

var dbValidate = new Command("validate", "Live Flyway checksum/history validation");
dbValidate.Options.Add(jsonOpt);
dbValidate.SetAction(async parseResult =>
{
    var json = parseResult.GetValue(jsonOpt);
    await RunJsonOpAsync("db.validate", json, async () =>
    {
        var settings = DatabaseConnectionSettings.Load();
        await FlywayRunner.ValidateAsync(settings);
        return new { validated = true };
    });
});

var dbInfo = new Command("info", "Flyway history vs Git SQL");
dbInfo.Options.Add(jsonOpt);
dbInfo.SetAction(async parseResult =>
{
    var json = parseResult.GetValue(jsonOpt);
    await RunJsonOpAsync("db.info", json, async () =>
    {
        var settings = DatabaseConnectionSettings.Load();
        var info = await FlywayRunner.InfoAsync(settings);
        if (!json)
            Console.Write(info);
        return new { info };
    }, writeResultWhenHuman: false);
});

var dbGrantDdl = new Command("grant-ddl", "Make the app SP owner of application tables on this branch (COW-safe DDL)");
var grantToOpt = new Option<string?>("--to")
{
    Description = "App SP Postgres role (default: LAKEBASE_APP_SP_ID)",
};
dbGrantDdl.Options.Add(grantToOpt);
dbGrantDdl.Options.Add(jsonOpt);
dbGrantDdl.SetAction(async parseResult =>
{
    var json = parseResult.GetValue(jsonOpt);
    var to = parseResult.GetValue(grantToOpt);
    await RunJsonOpAsync("db.grant-ddl", json, async () =>
    {
        EnvConfig.LoadPlatform();
        var role = to
            ?? Environment.GetEnvironmentVariable("LAKEBASE_APP_SP_ID")
            ?? throw new InvalidOperationException("Pass --to or set LAKEBASE_APP_SP_ID.");
        var settings = DatabaseConnectionSettings.Load();
        return await FeatureBranchDdl.GrantAppOwnershipAsync(settings, role);
    });
});

var dbSeed = new Command("seed", "Run an idempotent SQL data script on the current branch (not a migration)");
var seedFileArg = new Argument<string>("file")
{
    Description = "SQL file, relative to the repo root",
    DefaultValueFactory = _ => "lab/seed/seed.sql",
};
dbSeed.Arguments.Add(seedFileArg);
dbSeed.Options.Add(jsonOpt);
dbSeed.SetAction(async parseResult =>
{
    var json = parseResult.GetValue(jsonOpt);
    var file = parseResult.GetValue(seedFileArg)!;
    await RunJsonOpAsync("db.seed", json, async () =>
    {
        var settings = DatabaseConnectionSettings.Load();
        var path = Path.IsPathRooted(file) ? file : Path.Combine(EnvConfig.FindRepoRoot() ?? ".", file);
        CliLog.Progress($"==> Seeding {settings.Endpoint ?? "DATABASE_URL"} from {file}");
        var rows = await SqlScriptRunner.RunFileAsync(settings, path);
        CliLog.Progress($"==> {rows} row(s) inserted or updated (re-runs skip existing rows)");
        return new { file, rowsAffected = rows };
    });
});

var dbFlyway = new Command("flyway", "Pass remaining arguments to the pinned Flyway CLI");
var flywayArgsArg = new Argument<string[]>("args")
{
    Description = "Flyway CLI arguments (baseline, repair, ...)",
    Arity = System.CommandLine.ArgumentArity.ZeroOrMore,
};
dbFlyway.Arguments.Add(flywayArgsArg);
dbFlyway.Options.Add(jsonOpt);
dbFlyway.SetAction(async parseResult =>
{
    var json = parseResult.GetValue(jsonOpt);
    var args = parseResult.GetValue(flywayArgsArg) ?? [];
    if (args.Length == 0)
        throw new InvalidOperationException("Pass Flyway arguments, e.g. lakebase db flyway -- baseline -baselineVersion=1");
    await RunJsonOpAsync("db.flyway", json, async () =>
    {
        var settings = DatabaseConnectionSettings.Load();
        var output = await FlywayRunner.RunAsync(settings, args, captureStdout: true, retryTransient: false);
        if (!json)
            Console.Write(output);
        return new { arguments = args, output };
    }, writeResultWhenHuman: false);
});

var dbCommand = new Command("db", "Branch database connections and Flyway migrations");
dbCommand.Subcommands.Add(dbGenerateToken);
dbCommand.Subcommands.Add(dbSetup);
dbCommand.Subcommands.Add(dbLint);
dbCommand.Subcommands.Add(dbMigrate);
dbCommand.Subcommands.Add(dbValidate);
dbCommand.Subcommands.Add(dbInfo);
dbCommand.Subcommands.Add(dbGrantDdl);
dbCommand.Subcommands.Add(dbSeed);
dbCommand.Subcommands.Add(dbFlyway);

var lakebaseCommand = new Command("lakebase", "Lakebase CLI");
lakebaseCommand.Subcommands.Add(platformCommand);
lakebaseCommand.Subcommands.Add(dbCommand);

var rootCommand = new RootCommand("Lakebase CLI");
rootCommand.Subcommands.Add(lakebaseCommand);

var exitCode = await rootCommand.Parse(args).InvokeAsync();
// --json failures are reported on stdout and flagged via Environment.ExitCode.
return exitCode != 0 ? exitCode : Environment.ExitCode;

static async Task RunJsonOpAsync<T>(
    string operation,
    bool json,
    Func<Task<T>> action,
    bool writeResultWhenHuman = true)
{
    CliLog.JsonMode = json;
    try
    {
        var result = await action();
        if (json)
            CliLog.WriteReport(OperationReport.Ok(operation, result));
        else if (writeResultWhenHuman)
        {
            // Human mode: service already printed progress; nothing else required.
        }
    }
    catch (Exception ex)
    {
        if (!json)
            throw;

        CliLog.WriteErrorReport(operation, "operation-failed", ex.Message);
        Environment.ExitCode = 1;
    }
}

static LakebasePlatformService CreatePlatformService()
{
    var settings = PlatformSettings.Load();
    var auth = new DatabricksWorkspaceAuth(settings);
    var api = new LakebaseApiClient(settings, auth);
    var operationPoller = new LakebaseOperationPoller(api);
    return new LakebasePlatformService(settings, api, operationPoller);
}

static LakebaseDatabaseService CreateDatabaseService()
{
    var settings = DatabaseConnectionSettings.Load();
    var auth = new DatabricksWorkspaceAuth(settings);
    var api = new LakebaseApiClient(settings, auth);
    return new LakebaseDatabaseService(settings, api);
}
