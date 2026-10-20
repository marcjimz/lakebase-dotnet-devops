using System.Text;
using System.Text.RegularExpressions;

namespace Lakebase;

/// <summary>
/// Updates .env after branch create so developers do not hand-edit hosts. Writes the same branch
/// metadata keys as the Lakebase SCM extension's post-checkout hook, never a password: the app and
/// Flyway mint a short-lived credential at runtime.
/// </summary>
public static partial class DatabaseEnvWriter
{
    // Connection keys a stale value of would shadow the new branch metadata.
    private static readonly string[] StaleKeys = ["DATABASE_URL", "DB_PASSWORD"];

    public static string? FindDatabaseEnvPath()
    {
        var root = EnvConfig.FindRepoRoot();
        if (root is null)
            return null;

        return Path.Combine(root, EnvConfig.SharedEnvFile);
    }

    public static void UpdateBranchConnection(
        string host,
        string endpointPath,
        string? username = null)
    {
        var path = FindDatabaseEnvPath()
            ?? throw new InvalidOperationException("Could not locate repo root for .env");
        UpdateBranchConnection(path, host, endpointPath, username);
    }

    public static void UpdateBranchConnection(
        string path,
        string host,
        string endpointPath,
        string? username)
    {
        var match = EndpointPathPattern().Match(endpointPath);
        if (!match.Success)
            throw new InvalidOperationException($"Unexpected endpoint path '{endpointPath}'");

        username ??= EnvConfig.Get("LAKEBASE_DATABASE_CLIENT_ID") ?? EnvConfig.Get("LAKEBASE_APP_SP_ID");

        Dictionary<string, string> updates = new(StringComparer.Ordinal)
        {
            ["LAKEBASE_HOST"] = host,
            ["LAKEBASE_BRANCH_ID"] = match.Groups["branch"].Value,
            ["LAKEBASE_ENDPOINT"] = match.Groups["endpoint"].Value,
        };
        if (!string.IsNullOrEmpty(username))
            updates["DB_USERNAME"] = username;

        UpsertKeys(path, updates, StaleKeys);
        CliLog.Progress($"==> Updated {path}");
        foreach (var (key, value) in updates)
            CliLog.Detail($"    {key}={value}");
    }

    private static void UpsertKeys(string path, IReadOnlyDictionary<string, string> updates, IReadOnlyCollection<string> remove)
    {
        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : new List<string>
            {
                "# Auto-updated by lakebase platform branch create",
            };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq];
            if (remove.Contains(key))
            {
                lines.RemoveAt(i--);
                continue;
            }

            if (!updates.TryGetValue(key, out var value))
                continue;

            lines[i] = $"{key}={value}";
            seen.Add(key);
        }

        foreach (var (key, value) in updates)
        {
            if (seen.Contains(key))
                continue;
            lines.Add($"{key}={value}");
        }

        var sb = new StringBuilder();
        foreach (var line in lines)
            sb.AppendLine(line);
        File.WriteAllText(path, sb.ToString());
    }

    [GeneratedRegex(@"^projects/[^/]+/branches/(?<branch>[^/]+)/endpoints/(?<endpoint>[^/]+)$")]
    private static partial Regex EndpointPathPattern();
}
