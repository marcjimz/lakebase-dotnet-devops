namespace Lakebase;

public sealed class CreateBranchOptions
{
    /// <summary>Parent branch id (default: LAKEBASE_MAIN_BRANCH_ID).</summary>
    public string? Parent { get; init; }

    /// <summary>
    /// TTL in hours. When set, branch expires (spec.ttl). When null, uses no_expiry=true.
    /// CI PR branches should use 24.
    /// </summary>
    public int? TtlHours { get; init; }

    /// <summary>If the branch already exists, delete and recreate it.</summary>
    public bool Recreate { get; init; }

    /// <summary>If the branch already exists and Recreate is false, reuse it (idempotent).</summary>
    public bool Idempotent { get; init; } = true;

    /// <summary>Write the branch connection metadata (LAKEBASE_HOST, LAKEBASE_BRANCH_ID, LAKEBASE_ENDPOINT) into .env.</summary>
    public bool WriteEnvDatabase { get; init; } = true;

    public bool ProvisionRoles { get; init; } = true;
}
