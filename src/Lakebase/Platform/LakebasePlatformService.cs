using System.Text.Json;

namespace Lakebase;

public sealed record CreateBranchResult(
    string BranchName,
    string? BranchId,
    string? Host,
    string? EndpointPath);

public sealed class LakebasePlatformService(
    PlatformSettings settings,
    LakebaseApiClient api,
    LakebaseOperationPoller operationPoller)
{
    public void PrintTarget()
    {
        CliLog.Progress($"==> Workspace: {settings.DatabricksHost}");
        CliLog.Progress($"==> Project:   {settings.ProjectId}");
        if (!string.IsNullOrEmpty(settings.ClientId) && !string.IsNullOrEmpty(settings.ClientSecret))
            CliLog.Progress("==> Auth:      platform service principal");
        else if (!string.IsNullOrEmpty(settings.DatabricksToken))
            CliLog.Progress("==> Auth:      platform token");
    }

    public Task<CreateBranchResult> CreateBranchAsync(string branchName, CancellationToken ct = default)
        => CreateBranchAsync(branchName, new CreateBranchOptions(), ct);

    public Task<CreateBranchResult> CreateBranchAsync(
        string branchName,
        string? parentBranchId,
        bool provisionRoles,
        CancellationToken ct = default)
        => CreateBranchAsync(branchName, new CreateBranchOptions
        {
            Parent = parentBranchId,
            ProvisionRoles = provisionRoles,
            WriteEnvDatabase = provisionRoles,
            Idempotent = true,
        }, ct);

    /// <summary>
    /// Create (or reuse) a copy-on-write branch. Supports parent override, TTL, recreate, and .env write.
    /// </summary>
    public async Task<CreateBranchResult> CreateBranchAsync(
        string branchName,
        CreateBranchOptions options,
        CancellationToken ct = default)
    {
        var sanitized = BranchName.Sanitize(branchName);
        if (!string.Equals(sanitized, branchName, StringComparison.Ordinal))
            CliLog.Progress($"==> Sanitized branch name: '{branchName}' → '{sanitized}'");

        var parentId = string.IsNullOrWhiteSpace(options.Parent) ? settings.MainBranchId : BranchName.Sanitize(options.Parent);
        if (IsProtectedBranch(sanitized) || string.Equals(sanitized, parentId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Refusing to create or reuse protected branch '{sanitized}' (from '{branchName}').");
        PrintTarget();
        CliLog.Progress($"==> Parent:     projects/{settings.ProjectId}/branches/{parentId}");
        CliLog.Progress($"==> New branch: {sanitized}");

        var exists = await BranchExistsAsync(sanitized, ct);
        if (exists)
        {
            if (options.Recreate)
            {
                CliLog.Progress($"==> Branch '{sanitized}' exists — recreating...");
                await DeleteBranchInternalAsync(sanitized, requireAllowFlag: true, ct);
            }
            else if (options.Idempotent)
            {
                CliLog.Progress($"==> Branch '{sanitized}' already exists — reusing (idempotent).");
                var reused = await DescribeBranchAsync(sanitized, ct);
                if (options.ProvisionRoles)
                    await ProvisionBranchRolesAsync(sanitized, ct: ct);
                WriteGitHubOutput(reused.BranchId, sanitized, reused.Host, reused.EndpointPath);
                if (options.WriteEnvDatabase && !string.IsNullOrEmpty(reused.Host) && !string.IsNullOrEmpty(reused.EndpointPath))
                    DatabaseEnvWriter.UpdateBranchConnection(reused.Host, reused.EndpointPath, settings.AppServicePrincipalId);
                return reused with { BranchName = sanitized };
            }
            else
            {
                throw new InvalidOperationException(
                    $"Branch '{sanitized}' already exists. Pass --recreate or rely on default idempotent reuse.");
            }
        }

        await EnsureParentBranchExistsAsync(parentId, ct);

        var sourceBranch = $"projects/{settings.ProjectId}/branches/{parentId}";
        object spec = options.TtlHours is > 0
            ? new { source_branch = sourceBranch, ttl = $"{options.TtlHours.Value * 3600}s" }
            : new { source_branch = sourceBranch, no_expiry = true };

        CliLog.Progress(options.TtlHours is > 0
            ? $"==> Creating branch with TTL {options.TtlHours}h..."
            : "==> Creating branch (no_expiry)...");

        using var createResult = await api.PostAsync(
            $"/api/2.0/postgres/projects/{settings.ProjectId}/branches?branch_id={sanitized}",
            new { spec },
            ct);
        CliLog.Dump(JsonSerializer.Serialize(createResult, new JsonSerializerOptions { WriteIndented = true }));

        var branchId = TryGetString(createResult.RootElement, "branch_id")
            ?? TryGetNestedString(createResult.RootElement, "status", "branch_id")
            ?? sanitized;
        CliLog.Progress($"==> Branch ID: {branchId}");

        var described = await WaitForEndpointAsync(sanitized, ct);

        if (options.ProvisionRoles)
            await ProvisionBranchRolesAsync(sanitized, ct: ct);

        WriteGitHubOutput(described.BranchId ?? branchId, sanitized, described.Host, described.EndpointPath);

        if (options.WriteEnvDatabase && !string.IsNullOrEmpty(described.Host) && !string.IsNullOrEmpty(described.EndpointPath))
            DatabaseEnvWriter.UpdateBranchConnection(described.Host, described.EndpointPath, settings.AppServicePrincipalId);
        else if (!string.IsNullOrEmpty(described.Host) && options.ProvisionRoles)
            WriteDatabaseConfigInstructions(sanitized, described.Host, described.EndpointPath);

        return described with { BranchName = sanitized, BranchId = described.BranchId ?? branchId };
    }

    public async Task DeleteBranchAsync(string branchRef, bool ignoreMissing = false, CancellationToken ct = default)
    {
        var sanitized = BranchName.Sanitize(branchRef);
        if (BranchName.IsProtectedName(sanitized, settings.MainBranchId))
            throw new InvalidOperationException($"Refusing to delete protected branch '{sanitized}'.");

        if (!settings.AllowBranchDelete)
            throw new InvalidOperationException(
                "Branch delete is disabled. Set LAKEBASE_ALLOW_BRANCH_DELETE=true in .env to enable.");

        if (ignoreMissing && !await BranchExistsAsync(sanitized, ct))
        {
            CliLog.Progress($"==> Branch '{sanitized}' not found — nothing to delete.");
            return;
        }

        await DeleteBranchInternalAsync(sanitized, requireAllowFlag: false, ct);
    }

    private async Task DeleteBranchInternalAsync(string sanitized, bool requireAllowFlag, CancellationToken ct)
    {
        if (requireAllowFlag && !settings.AllowBranchDelete)
            throw new InvalidOperationException(
                "Recreate requires LAKEBASE_ALLOW_BRANCH_DELETE=true (CI sets this).");

        if (BranchName.IsProtectedName(sanitized, settings.MainBranchId))
            throw new InvalidOperationException($"Refusing to delete protected branch '{sanitized}'.");

        PrintTarget();
        CliLog.Progress($"==> Deleting branch: {sanitized}");
        await api.DeleteAsync($"/api/2.0/postgres/projects/{settings.ProjectId}/branches/{sanitized}", ct);
        CliLog.Progress("==> Branch deleted.");
    }

    public async Task<IReadOnlyList<string>> ListBranchIdsAsync(CancellationToken ct = default)
    {
        using var branches = await api.GetAsync($"/api/2.0/postgres/projects/{settings.ProjectId}/branches", ct);
        if (!branches.RootElement.TryGetProperty("branches", out var list))
            return [];

        var ids = new List<string>();
        foreach (var branch in list.EnumerateArray())
        {
            var id = TryGetString(branch, "branch_id")
                ?? TryGetString(branch, "name")?.Split('/').LastOrDefault();
            if (!string.IsNullOrEmpty(id))
                ids.Add(id);
        }

        return ids.OrderBy(x => x).ToList();
    }

    public async Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default)
    {
        var sanitized = BranchName.Sanitize(branchName);
        using var doc = await api.TryGetAsync(
            $"/api/2.0/postgres/projects/{settings.ProjectId}/branches/{sanitized}",
            ct);
        if (doc is not null)
            return true;

        var ids = await ListBranchIdsAsync(ct);
        return ids.Any(id => string.Equals(id, sanitized, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<CreateBranchResult> DescribeBranchAsync(string branchName, CancellationToken ct = default)
    {
        var sanitized = BranchName.Sanitize(branchName);
        using var endpoints = await api.GetAsync(
            $"/api/2.0/postgres/projects/{settings.ProjectId}/branches/{sanitized}/endpoints",
            ct);
        CliLog.Dump(JsonSerializer.Serialize(endpoints, new JsonSerializerOptions { WriteIndented = true }));

        var host = TryGetEndpointHost(endpoints.RootElement);
        var endpointId = TryGetEndpointId(endpoints.RootElement);
        var endpointPath = endpointId is not null
            ? $"projects/{settings.ProjectId}/branches/{sanitized}/endpoints/{endpointId}"
            : null;

        return new CreateBranchResult(sanitized, sanitized, host, endpointPath);
    }

    /// <summary>Polls until the branch's primary endpoint reports a host (no fixed sleep).</summary>
    private async Task<CreateBranchResult> WaitForEndpointAsync(string branchName, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("LAKEBASE_ENDPOINT_WAIT_SECONDS"), out var s) && s > 0 ? s : 300);
        var deadline = DateTime.UtcNow + timeout;
        var delay = TimeSpan.FromSeconds(2);

        CliLog.Progress($"==> Waiting for endpoint (up to {timeout.TotalSeconds:0}s)...");
        while (true)
        {
            var described = await DescribeBranchAsync(branchName, ct);
            if (!string.IsNullOrEmpty(described.Host) && !string.IsNullOrEmpty(described.EndpointPath))
                return described;

            if (DateTime.UtcNow + delay > deadline)
                throw new TimeoutException($"Endpoint for branch '{branchName}' did not report a host within {timeout.TotalSeconds:0}s.");

            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
        }
    }

    /// <summary>
    /// Migration proof: COW staging from production, then apply Git Flyway SQL.
    /// Does not modify production. Optional <c>--from</c> validates a feature branch label.
    /// </summary>
    public async Task<StagingApplyResult> StagingApplyAsync(
        string? fromFeature = null,
        string? stagingName = null,
        bool recreate = false,
        int? ttlHours = null,
        CancellationToken ct = default)
    {
        var parent = settings.MainBranchId;
        var name = BranchName.Sanitize(stagingName ?? settings.StagingBranchId);
        string? feature = string.IsNullOrWhiteSpace(fromFeature) ? null : BranchName.Sanitize(fromFeature);

        if (BranchName.IsProtectedName(name, settings.MainBranchId)
            || string.Equals(name, parent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refuse to use protected/production name '{name}' as staging. Pick a staging branch name.");
        }

        if (feature is not null)
        {
            if (string.Equals(feature, parent, StringComparison.OrdinalIgnoreCase)
                || BranchName.IsProtectedName(feature, settings.MainBranchId))
            {
                throw new InvalidOperationException(
                    $"Refuse to use protected branch '{feature}' as --from. Pass a feature branch name, or omit --from.");
            }

            if (string.Equals(feature, name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"--from '{feature}' cannot be the same as staging name '{name}'.");
        }

        PrintTarget();
        CliLog.Progress($"==> Staging apply (migration proof): COW '{parent}' → staging '{name}', then apply Git migrations.");
        CliLog.Progress("==> Production is not modified.");

        await EnsureParentBranchExistsAsync(parent, ct);

        if (feature is not null)
        {
            await EnsureParentBranchExistsAsync(feature, ct);
            await EnsureFeatureSchemaAppliedAsync(feature, ct);
        }

        var branch = await CreateBranchAsync(name, new CreateBranchOptions
        {
            Parent = parent,
            Recreate = recreate,
            Idempotent = !recreate,
            TtlHours = ttlHours,
            ProvisionRoles = true,
            WriteEnvDatabase = true,
        }, ct);

        if (string.IsNullOrEmpty(branch.Host) || string.IsNullOrEmpty(branch.EndpointPath))
            throw new InvalidOperationException($"Staging branch '{name}' has no endpoint host yet.");

        var username = settings.AppServicePrincipalId
            ?? Environment.GetEnvironmentVariable("LAKEBASE_DATABASE_CLIENT_ID")
            ?? throw new InvalidOperationException("Set LAKEBASE_APP_SP_ID or LAKEBASE_DATABASE_CLIENT_ID for staging migrate.");

        var databaseUrl =
            $"Host={branch.Host};Port=5432;Database={settings.DatabaseName};Username={username};SSLMode=Require";

        CliLog.Progress($"==> Applying Git Flyway SQL on staging '{name}' (proves promote replay)...");
        var stagingSettings = BuildBranchDatabaseSettings(databaseUrl, branch.EndpointPath);
        var flywayInfo = await RunFlywayMigrateAsync(stagingSettings, label: $"staging '{name}'", ct);

        CliLog.Blank();
        CliLog.Progress($"==> Staging '{name}' is production + Git migrations (flyway validate OK).");
        CliLog.Progress("==> Next:");
        CliLog.Detail("    dotnet run --project src/Lakebase.Cli -- lakebase db info");
        CliLog.Detail(feature is not null
            ? $"    lakebase platform promote --from {feature} --staging-name {name}"
            : $"    lakebase platform promote --staging-name {name}");

        return new StagingApplyResult(
            ParentBranch: parent,
            FeatureBranch: feature,
            StagingBranch: name,
            Host: branch.Host,
            EndpointPath: branch.EndpointPath,
            FlywayInfo: flywayInfo);
    }

    /// <summary>
    /// Promote: optional production snapshot, apply Git Flyway SQL to production, verify, delete staging.
    /// Optional <c>--from</c> verifies a feature branch still has Flyway history applied.
    /// </summary>
    public async Task<PromoteResult> PromoteAsync(
        bool skipSnapshot = false,
        string? snapshotName = null,
        bool cleanupStaging = true,
        string? stagingName = null,
        string? fromFeature = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(settings.ProductionDatabaseUrl))
            throw new InvalidOperationException(
                "Set PRODUCTION_DATABASE_URL in .env for promote (migration replay on production).");

        PrintTarget();
        var staging = BranchName.Sanitize(stagingName ?? settings.StagingBranchId);
        string? feature = string.IsNullOrWhiteSpace(fromFeature) ? null : BranchName.Sanitize(fromFeature);

        if (feature is not null)
        {
            CliLog.Progress(
                $"==> Promote: apply Git migrations validated on feature '{feature}' → production; then delete staging '{staging}'.");
            await EnsureParentBranchExistsAsync(feature, ct);
            await EnsureFeatureSchemaAppliedAsync(feature, ct);
        }
        else
        {
            CliLog.Progress(
                $"==> Promote: apply Git migrations → production; then delete staging '{staging}'.");
            CliLog.Progress("==> Tip: pass --from <feature-branch> to verify the feature still has migrations before prod.");
        }

        CliLog.Progress("==> Lakebase has no branch merge — promote replays Flyway SQL from the current Git SHA.");

        string? createdSnapshot = null;
        var allowDeleteWasEnabled = settings.AllowBranchDelete;
        var snapshotDeleted = false;
        var stagingDeleted = false;

        try
        {
            if (!skipSnapshot)
            {
                createdSnapshot = snapshotName
                    ?? $"pre-migrate-{DateTime.UtcNow:yyyyMMddHHmmss}";
                CliLog.Progress($"==> Creating pre-migrate snapshot '{createdSnapshot}' from '{settings.MainBranchId}'...");
                await CreateBranchAsync(createdSnapshot, new CreateBranchOptions
                {
                    Parent = settings.MainBranchId,
                    ProvisionRoles = false,
                    WriteEnvDatabase = false,
                    Idempotent = false,
                    Recreate = allowDeleteWasEnabled,
                    TtlHours = 24,
                }, ct);
                CliLog.Progress($"==> Snapshot ready: {createdSnapshot}");
            }
            else
            {
                CliLog.Progress("==> Skipping pre-migrate snapshot (--skip-snapshot).");
            }

            var prodSettings = BuildProductionDatabaseSettings();
            var flywayInfo = await RunWithTemporaryWriteAsync(
                settings.MainBranchId,
                () => RunFlywayMigrateAsync(prodSettings, label: "production", ct),
                ct);

            if (createdSnapshot is not null)
            {
                CliLog.Progress($"==> Promote succeeded — deleting snapshot '{createdSnapshot}'...");
                if (!allowDeleteWasEnabled)
                {
                    CliLog.Progress(
                        $"==> Snapshot '{createdSnapshot}' preserved (set LAKEBASE_ALLOW_BRANCH_DELETE=true to auto-delete).");
                }
                else
                {
                    await DeleteBranchAsync(createdSnapshot, ignoreMissing: false, ct);
                    snapshotDeleted = true;
                }
            }

            if (cleanupStaging)
            {
                CliLog.Progress($"==> Cleaning up staging branch '{staging}'...");
                if (!allowDeleteWasEnabled)
                {
                    CliLog.Progress(
                        $"==> Staging '{staging}' not deleted (set LAKEBASE_ALLOW_BRANCH_DELETE=true).");
                }
                else
                {
                    await DeleteBranchAsync(staging, ignoreMissing: true, ct);
                    stagingDeleted = true;
                }
            }
            else
            {
                CliLog.Progress("==> Leaving staging in place (--no-cleanup-staging).");
            }

            if (feature is not null)
                CliLog.Progress($"==> Production matches feature '{feature}'. Feature branch left in place (delete when done).");
            else
                CliLog.Progress("==> Production migrations applied.");

            return new PromoteResult(
                FeatureBranch: feature,
                StagingBranch: staging,
                ProductionBranch: settings.MainBranchId,
                SnapshotBranch: createdSnapshot,
                SnapshotDeleted: snapshotDeleted,
                StagingDeleted: stagingDeleted,
                FlywayInfo: flywayInfo);
        }
        catch
        {
            if (createdSnapshot is not null)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"==> Promote FAILED — snapshot preserved: '{createdSnapshot}'");
                Console.Error.WriteLine("==> Staging was not deleted.");
            }

            throw;
        }
    }

    /// <summary>
    /// Elevates the app SP to write on a stable branch for <paramref name="action"/>, then always
    /// restores read-only — including when the migration fails or the run is cancelled.
    /// </summary>
    private async Task<T> RunWithTemporaryWriteAsync<T>(string branch, Func<Task<T>> action, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(settings.AppServicePrincipalId))
            return await action();

        CliLog.Progress($"==> Elevating app SP to write on '{branch}' for promote...");
        await ProvisionAppServicePrincipalRoleAsync(branch, readOnly: false, ct);
        try
        {
            return await action();
        }
        finally
        {
            try
            {
                CliLog.Progress($"==> Restoring app SP to read-only on '{branch}'...");
                await ProvisionAppServicePrincipalRoleAsync(branch, readOnly: true, CancellationToken.None);
            }
            catch (Exception restoreEx)
            {
                Console.Error.WriteLine(
                    $"==> SECURITY: failed to restore read-only on '{branch}'. Run 'lakebase platform role provision {branch} --read-only' now. {restoreEx.Message}");
                throw;
            }
        }
    }

    /// <summary>Ensure a feature branch exists and has Git Flyway SQL applied.</summary>
    private async Task EnsureFeatureSchemaAppliedAsync(string featureBranch, CancellationToken ct)
    {
        CliLog.Progress($"==> Checking feature '{featureBranch}' has Git Flyway SQL applied...");
        var described = await DescribeBranchAsync(featureBranch, ct);
        if (string.IsNullOrEmpty(described.Host) || string.IsNullOrEmpty(described.EndpointPath))
            throw new InvalidOperationException($"Feature branch '{featureBranch}' has no endpoint host yet.");

        var username = settings.AppServicePrincipalId
            ?? Environment.GetEnvironmentVariable("LAKEBASE_DATABASE_CLIENT_ID")
            ?? throw new InvalidOperationException("Set LAKEBASE_APP_SP_ID or LAKEBASE_DATABASE_CLIENT_ID.");

        // Feature branches get write roles at create time; ensure we can read flyway_schema_history.
        await ProvisionBranchRolesAsync(featureBranch, ct: ct);

        var databaseUrl =
            $"Host={described.Host};Port=5432;Database={settings.DatabaseName};Username={username};SSLMode=Require";
        var featureSettings = BuildBranchDatabaseSettings(databaseUrl, described.EndpointPath);
        CliLog.Progress($"==> Running: lakebase db validate (feature '{featureBranch}')");
        await FlywayRunner.ValidateAsync(featureSettings, ct);
        await FlywayRunner.InfoAsync(featureSettings, ct);
    }

    /// <param name="readOnly">
    /// When null, protected branches are read-only and feature branches get write membership.
    /// Promote briefly passes <c>false</c> on the stable branch, then restores <c>true</c>.
    /// </param>
    public async Task ProvisionBranchRolesAsync(string branchName, bool? readOnly = null, CancellationToken ct = default)
    {
        var treatReadOnly = readOnly ?? IsProtectedBranch(branchName);

        if (treatReadOnly)
        {
            if (!string.IsNullOrEmpty(settings.AppServicePrincipalId))
            {
                CliLog.Progress($"==> Branch '{branchName}': provisioning app SP with empty membership (read-only).");
                await ProvisionAppServicePrincipalRoleAsync(branchName, readOnly: true, ct);
            }
            else
                CliLog.Progress($"==> Skipping role provisioning on '{branchName}' (no LAKEBASE_APP_SP_ID).");

            return;
        }

        if (!string.IsNullOrEmpty(settings.DevGroup))
            await ProvisionDevGroupRoleAsync(branchName, ct);

        if (!string.IsNullOrEmpty(settings.AppServicePrincipalId))
            await ProvisionAppServicePrincipalRoleAsync(branchName, readOnly: false, ct);
    }

    private async Task<string> RunFlywayMigrateAsync(
        DatabaseConnectionSettings dbSettings,
        string label,
        CancellationToken ct)
    {
        CliLog.Progress($"==> Running: lakebase db migrate ({label})");
        await FlywayRunner.MigrateAsync(dbSettings, retryTransient: true, ct);
        CliLog.Progress($"==> Running: lakebase db validate ({label})");
        await FlywayRunner.ValidateAsync(dbSettings, ct);
        var info = await FlywayRunner.InfoAsync(dbSettings, ct);
        CliLog.Progress($"==> Verify OK: {label} flyway validate succeeded.");
        return info;
    }

    private DatabaseConnectionSettings BuildBranchDatabaseSettings(string databaseUrl, string endpoint)
    {
        EnvConfig.LoadDatabase();
        var clientId = Environment.GetEnvironmentVariable("LAKEBASE_DATABASE_CLIENT_ID")
            ?? Environment.GetEnvironmentVariable("DATABRICKS_CLIENT_ID")
            ?? settings.AppServicePrincipalId;
        var clientSecret = Environment.GetEnvironmentVariable("LAKEBASE_DATABASE_CLIENT_SECRET")
            ?? Environment.GetEnvironmentVariable("DATABRICKS_CLIENT_SECRET");

        return new DatabaseConnectionSettings
        {
            DatabricksHost = settings.DatabricksHost,
            ClientId = clientId,
            ClientSecret = clientSecret,
            ConnectionString = databaseUrl,
            Endpoint = endpoint,
            UseOAuthTokenRefresh = !string.IsNullOrEmpty(clientId) && (!string.IsNullOrEmpty(clientSecret) || GitHubOidc.IsEnabled()),
        };
    }

    private DatabaseConnectionSettings BuildProductionDatabaseSettings()
    {
        // Reuse app SP credentials from the environment / .env for OAuth refresh.
        EnvConfig.LoadDatabase();
        var clientId = Environment.GetEnvironmentVariable("LAKEBASE_DATABASE_CLIENT_ID")
            ?? Environment.GetEnvironmentVariable("DATABRICKS_CLIENT_ID")
            ?? settings.AppServicePrincipalId;
        var clientSecret = Environment.GetEnvironmentVariable("LAKEBASE_DATABASE_CLIENT_SECRET")
            ?? Environment.GetEnvironmentVariable("DATABRICKS_CLIENT_SECRET");

        return new DatabaseConnectionSettings
        {
            DatabricksHost = settings.DatabricksHost,
            ClientId = clientId,
            ClientSecret = clientSecret,
            ConnectionString = settings.ProductionDatabaseUrl!,
            Endpoint = settings.ProductionEndpoint
                ?? throw new InvalidOperationException("Set PRODUCTION_LAKEBASE_ENDPOINT in .env"),
            UseOAuthTokenRefresh = !string.IsNullOrEmpty(clientId) && (!string.IsNullOrEmpty(clientSecret) || GitHubOidc.IsEnabled()),
        };
    }

    /// <summary>
    /// Least-privilege Postgres membership. Empty membership is read-only (OAuth login only;
    /// table access comes from bootstrap GRANTs). Write uses <c>databricks_postgres</c> (schema
    /// owner) so migrations can DDL without <c>DATABRICKS_SUPERUSER</c>.
    /// </summary>
    private static string[] MembershipRolesFor(bool readOnly)
        => readOnly ? [] : ["databricks_postgres"];

    private async Task ProvisionDevGroupRoleAsync(string branchName, CancellationToken ct)
    {
        var devGroup = settings.DevGroup!;
        var roleId = ToRoleId(devGroup);

        CliLog.Progress($"==> Provisioning OAuth Postgres role for dev group '{devGroup}' (least-privilege write)...");
        using var createRoleResult = await api.PostAsync(
            $"/api/2.0/postgres/projects/{settings.ProjectId}/branches/{branchName}/roles?role_id={roleId}&replace_existing=true",
            new
            {
                spec = new
                {
                    identity_type = "GROUP",
                    postgres_role = devGroup,
                    auth_method = "LAKEBASE_OAUTH_V1",
                    membership_roles = MembershipRolesFor(readOnly: false),
                },
            },
            ct);

        await operationPoller.WaitForOperationAsync(createRoleResult.RootElement, ct);
        CliLog.Progress($"==> Dev group role '{roleId}' is ready.");
    }

    private async Task ProvisionAppServicePrincipalRoleAsync(string branchName, bool readOnly, CancellationToken ct)
    {
        var appSpId = settings.AppServicePrincipalId!;
        // Prefer the stable id; fall back to the COW-inherited "<id>-production" role from production.
        var roleIds = new[] { settings.AppRoleId, $"{settings.AppRoleId}-production" };
        var membershipRoles = MembershipRolesFor(readOnly);

        CliLog.Progress($"==> Provisioning OAuth Postgres role for app SP '{appSpId}' (readOnly={readOnly})...");

        Exception? lastError = null;
        foreach (var roleId in roleIds)
        {
            try
            {
                using var createRoleResult = await api.PostAsync(
                    $"/api/2.0/postgres/projects/{settings.ProjectId}/branches/{branchName}/roles?role_id={roleId}&replace_existing=true",
                    new
                    {
                        spec = new
                        {
                            identity_type = "SERVICE_PRINCIPAL",
                            postgres_role = appSpId,
                            auth_method = "LAKEBASE_OAUTH_V1",
                            membership_roles = membershipRoles,
                        },
                    },
                    ct);

                await operationPoller.WaitForOperationAsync(createRoleResult.RootElement, ct);
                CliLog.Progress($"==> App SP role '{roleId}' is ready.");
                return;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("ROLE_ALREADY_EXISTS", StringComparison.Ordinal)
                || ex.Message.Contains("role with that name already exists", StringComparison.OrdinalIgnoreCase))
            {
                lastError = ex;
                CliLog.Progress($"==> Role id '{roleId}' conflict — trying next candidate...");
            }
        }

        // Fail closed: reporting success here would leave the requested membership unapplied
        // (e.g. write access never revoked after a promote).
        throw new InvalidOperationException(
            $"Could not apply app SP role (readOnly={readOnly}) on '{branchName}': every candidate role id conflicted.",
            lastError);
    }

    private void WriteDatabaseConfigInstructions(string branchName, string host, string? endpointPath)
    {
        CliLog.Blank();
        CliLog.Progress("==> Update .env with:");
        CliLog.Progress($"LAKEBASE_HOST={host}");
        CliLog.Progress($"LAKEBASE_BRANCH_ID={branchName}");
        CliLog.Progress($"LAKEBASE_ENDPOINT={endpointPath}");
        CliLog.Progress("    (No password: the app and lakebase db commands mint a short-lived credential.)");
        CliLog.Blank();
        CliLog.Progress("==> Then use the database flow (not platform credentials):");
        CliLog.Detail("    dotnet run --project src/Lakebase.Cli -- lakebase db generate-token");
        CliLog.Detail("    dotnet run --project src/Lakebase.Cli -- lakebase db migrate");
        CliLog.Detail("    dotnet run --project src/Lakebase.Cli -- lakebase db validate");
        CliLog.Progress("==> CI service-principal credentials belong in GitHub environments, not .env.");
    }

    private bool IsProtectedBranch(string branchName)
        => BranchName.IsProtectedName(branchName, settings.MainBranchId);

    private static string ToRoleId(string value)
    {
        var roleId = value.ToLowerInvariant();
        if (roleId.Length < 4 || roleId.Length > 63)
            throw new InvalidOperationException($"Role id must be 4-63 characters; got '{value}'.");

        foreach (var ch in roleId)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch == '-')
                continue;

            throw new InvalidOperationException(
                $"Role id '{value}' is invalid (use lowercase letters, numbers, hyphens).");
        }

        return roleId;
    }

    private async Task EnsureParentBranchExistsAsync(string parentId, CancellationToken ct)
    {
        CliLog.Progress("==> Verifying parent branch exists in project...");
        using var branches = await api.GetAsync($"/api/2.0/postgres/projects/{settings.ProjectId}/branches", ct);

        if (!branches.RootElement.TryGetProperty("branches", out var branchList))
            throw new InvalidOperationException($"No branches found in project '{settings.ProjectId}'.");

        var parentFound = branchList.EnumerateArray().Any(branch =>
        {
            var branchId = TryGetString(branch, "branch_id");
            var name = TryGetString(branch, "name");
            return branchId == parentId
                || (name?.EndsWith($"/branches/{parentId}", StringComparison.Ordinal) ?? false);
        });

        if (parentFound)
            return;

        throw new InvalidOperationException($"Parent branch '{parentId}' not found.");
    }

    private static void WriteGitHubOutput(string? branchId, string branchName, string? host, string? endpointPath)
    {
        var outputFile = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        if (string.IsNullOrEmpty(outputFile))
            return;

        // Do not write the full endpoint resource name (projects/<project>/...).
        // That path embeds LAKEBASE_PROJECT_ID, and GitHub Actions silently drops
        // job outputs that contain a masked secret ("Skip output 'endpoint' since it may contain secret").
        var endpointId = string.IsNullOrEmpty(endpointPath) ? null : endpointPath.Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(endpointId) && !string.IsNullOrEmpty(host))
            endpointId = host.Split('.')[0];

        File.AppendAllText(outputFile, $"branch_id={branchId}\n");
        File.AppendAllText(outputFile, $"branch_name={branchName}\n");
        File.AppendAllText(outputFile, $"host={host}\n");
        File.AppendAllText(outputFile, $"endpoint_id={endpointId}\n");
    }

    public static string? TryGetEndpointHost(JsonElement root)
    {
        if (!root.TryGetProperty("endpoints", out var endpoints) || endpoints.GetArrayLength() == 0)
            return null;

        var status = endpoints[0].GetProperty("status");
        if (status.TryGetProperty("hosts", out var hosts)
            && hosts.TryGetProperty("host", out var host))
            return host.GetString();

        if (status.TryGetProperty("host", out var directHost))
            return directHost.GetString();

        return null;
    }

    public static string? TryGetEndpointId(JsonElement root)
    {
        if (!root.TryGetProperty("endpoints", out var endpoints) || endpoints.GetArrayLength() == 0)
            return null;

        var endpoint = endpoints[0];
        var endpointId = TryGetString(endpoint, "endpoint_id")
            ?? TryGetString(endpoint, "id");
        if (!string.IsNullOrEmpty(endpointId))
            return endpointId;

        var name = TryGetString(endpoint, "name");
        var fromName = name?.Split('/').LastOrDefault();
        if (!string.IsNullOrEmpty(fromName))
            return fromName;

        var host = TryGetEndpointHost(root);
        var fromHost = host?.Split('.').FirstOrDefault();
        return string.IsNullOrEmpty(fromHost) ? null : fromHost;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) ? value.GetString() : null;

    private static string? TryGetNestedString(JsonElement element, string parentName, string propertyName)
        => element.TryGetProperty(parentName, out var parent) ? TryGetString(parent, propertyName) : null;
}
