namespace Lakebase;

/// <summary>Maps the checked-out git branch to its Lakebase branch, the way the SCM extension pairs them.</summary>
public static class GitBranch
{
    /// <summary>Current branch from <c>.git/HEAD</c>, or null when detached (CI checks out a SHA) or not a repo.</summary>
    public static string? Current(string? repoRoot = null)
    {
        repoRoot ??= EnvConfig.FindRepoRoot();
        if (repoRoot is null)
            return null;

        var head = Path.Combine(repoRoot, ".git", "HEAD");
        if (!File.Exists(head))
            return null;

        const string prefix = "ref: refs/heads/";
        var line = File.ReadLines(head).FirstOrDefault()?.Trim();
        return line is not null && line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : null;
    }

    /// <summary>
    /// The trunk (main/master or <c>LAKEBASE_TRUNK_BRANCH</c>) pairs with the project's stable branch
    /// (<c>LAKEBASE_MAIN_BRANCH_ID</c>, default <c>production</c>); any other branch with its sanitized name.
    /// </summary>
    public static string ToLakebaseBranch(string gitBranch)
    {
        var trunk = EnvConfig.Get("LAKEBASE_TRUNK_BRANCH");
        return gitBranch is "main" or "master" || gitBranch == trunk
            ? EnvConfig.Get("LAKEBASE_MAIN_BRANCH_ID") ?? "production"
            : BranchName.Sanitize(gitBranch);
    }
}
