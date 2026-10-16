using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Lakebase;

/// <summary>
/// Offline Flyway SQL ledger checks (no database): naming, uniqueness, order, checksums, config hygiene.
/// </summary>
public static class FlywaySqlLinter
{
    private static readonly Regex FileNamePattern = new(
        @"^V[0-9]+([._][0-9]+)*__[A-Za-z0-9_]+\.sql$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // CI may validate a PR's flyway.conf with a trusted copy of this linter.
    // Keep the accepted surface deliberately small: callbacks, custom locations,
    // placeholders, and connection settings could turn configuration into code execution.
    private static readonly IReadOnlyDictionary<string, string> TrustedConfig = new Dictionary<string, string>(
        StringComparer.Ordinal)
    {
        ["flyway.locations"] = "filesystem:db/migrations",
        ["flyway.table"] = "flyway_schema_history",
        ["flyway.defaultSchema"] = "public",
        ["flyway.schemas"] = "public",
        ["flyway.validateMigrationNaming"] = "true",
        ["flyway.sqlMigrationPrefix"] = "V",
        ["flyway.sqlMigrationSeparator"] = "__",
        ["flyway.sqlMigrationSuffixes"] = ".sql",
        ["flyway.baselineOnMigrate"] = "true",
        ["flyway.baselineVersion"] = "0",
    };

    public static FlywayLintResult Lint(string? repoRoot = null)
    {
        var root = repoRoot ?? EnvConfig.FindRepoRoot()
            ?? throw new InvalidOperationException("Could not find repo root (SampleApp.sln).");
        var dir = Path.Combine(root, "db", "migrations");
        var config = Path.Combine(root, "conf", "flyway.conf");
        var errors = new List<string>();
        var manifest = new List<string>();

        if (!Directory.Exists(dir))
            throw Fail(errors, $"Missing {dir}");

        // Flyway scans the location recursively and also runs script migrations/callbacks (.sh, .ps1, ...),
        // so only top-level .sql files may exist here.
        foreach (var path in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (name == ".DS_Store")
                continue;
            if (Directory.Exists(path))
                errors.Add($"Subdirectories are not allowed in db/migrations: {Path.GetRelativePath(dir, path)}");
            else if (Path.GetDirectoryName(path) == dir && !name.EndsWith(".sql", StringComparison.Ordinal))
                errors.Add($"Only .sql migrations are allowed in db/migrations: {name}");
        }

        var files = Directory.GetFiles(dir, "*.sql").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
            throw Fail(errors, $"No Flyway SQL files in {dir}");

        manifest.Add("==> Flyway SQL manifest");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? prevKey = null;

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            if (!FileNamePattern.IsMatch(name))
            {
                errors.Add($"Invalid Flyway filename: {name} (expected V{{version}}__{{description}}.sql)");
                continue;
            }

            var info = new FileInfo(path);
            if (info.Length == 0)
                errors.Add($"Empty migration: {name}");

            var version = name[1..name.IndexOf("__", StringComparison.Ordinal)];
            var versionKey = version.Replace('_', '.');
            if (!seen.Add(versionKey))
                errors.Add($"Duplicate Flyway version {versionKey} ({name})");

            if (prevKey is not null && CompareVersions(versionKey, prevKey) <= 0)
                errors.Add($"Flyway versions must increase: {prevKey} then {versionKey} ({name})");

            prevKey = versionKey;
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            manifest.Add($"{hash}  {path}");
        }

        if (!File.Exists(config))
            errors.Add($"Missing {config}");
        else
        {
            var actual = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in File.ReadAllLines(config))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    errors.Add($"{config} contains an invalid setting: {line}");
                    continue;
                }

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (!TrustedConfig.ContainsKey(key))
                    errors.Add($"{config} contains unsupported setting {key}");
                else if (!actual.TryAdd(key, value))
                    errors.Add($"{config} contains duplicate setting {key}");
            }

            foreach (var (key, expected) in TrustedConfig)
            {
                if (!actual.TryGetValue(key, out var value))
                    errors.Add($"{config} missing {key}");
                else if (!string.Equals(value, expected, StringComparison.Ordinal))
                    errors.Add($"{config} must set {key}={expected}");
            }
        }

        if (errors.Count > 0)
            throw Fail(errors, errors[0]);

        manifest.Add($"==> Offline Flyway SQL validation passed ({files.Length} file(s))");
        return new FlywayLintResult(files.Length, manifest);
    }

    public static int CompareVersions(string current, string previous)
    {
        var cur = Parts(current);
        var prev = Parts(previous);
        var len = Math.Max(cur.Length, prev.Length);
        for (var i = 0; i < len; i++)
        {
            var c = i < cur.Length ? cur[i] : 0;
            var p = i < prev.Length ? prev[i] : 0;
            if (c != p)
                return c.CompareTo(p);
        }

        return 0;
    }

    private static int[] Parts(string version)
        => version.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();

    private static InvalidOperationException Fail(List<string> errors, string primary)
    {
        foreach (var error in errors)
            Console.Error.WriteLine($"::error::{error}");
        return new InvalidOperationException(primary);
    }
}

public sealed record FlywayLintResult(int FileCount, IReadOnlyList<string> ManifestLines);
