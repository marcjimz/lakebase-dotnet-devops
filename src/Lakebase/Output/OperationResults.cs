namespace Lakebase;

public sealed record StagingApplyResult(
    string ParentBranch,
    string? FeatureBranch,
    string StagingBranch,
    string? Host,
    string? EndpointPath,
    string FlywayInfo);

public sealed record PromoteResult(
    string? FeatureBranch,
    string StagingBranch,
    string ProductionBranch,
    string? SnapshotBranch,
    bool SnapshotDeleted,
    bool StagingDeleted,
    string FlywayInfo);
