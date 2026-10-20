using dotenv.net;

namespace Lakebase;

/// <summary>
/// Loads <c>.env</c> (the file the Lakebase SCM extension reads and writes), then the legacy
/// <c>.env.platform</c> / <c>.env.database</c> files as fallbacks. Real environment variables
/// always win, so CI (which sets variables directly) never reads these files.
/// </summary>
public static class EnvConfig
{
    public const string SharedEnvFile = ".env";

    private static bool _platformLoaded;
    private static bool _databaseLoaded;

    public static void Load()
    {
        LoadDatabase();
        LoadPlatform();
    }

    public static void LoadPlatform()
    {
        if (_platformLoaded)
            return;

        LoadEnvFile(SharedEnvFile);
        LoadEnvFile(".env.platform");
        _platformLoaded = true;
    }

    public static void LoadDatabase()
    {
        if (_databaseLoaded)
            return;

        LoadEnvFile(SharedEnvFile);
        LoadEnvFile(".env.database");
        _databaseLoaded = true;
    }

    /// <summary>Environment variable, treating empty values (e.g. <c>KEY=</c> written by the hook) as unset.</summary>
    public static string? Get(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value.Trim() : null;

    /// <summary><c>DATABRICKS_HOST</c>, else the host of <c>DATABRICKS_CONFIG_PROFILE</c> in <c>~/.databrickscfg</c>.</summary>
    public static string? WorkspaceHost(string? profile)
    {
        var host = Get("DATABRICKS_HOST") ?? DatabricksConfigFile.HostForProfile(profile);
        if (host is null)
            return null;
        host = host.TrimEnd('/');
        return host.Contains("://", StringComparison.Ordinal) ? host : $"https://{host}";
    }

    private static void LoadEnvFile(string fileName)
    {
        // A bare ".env" is too common to search parent directories for; only the repo root's counts.
        var path = fileName == SharedEnvFile
            ? FindRepoRoot() is { } root && File.Exists(Path.Combine(root, fileName)) ? Path.Combine(root, fileName) : null
            : FindEnvFile(fileName);
        if (path is null)
            return;

        DotEnv.Load(new DotEnvOptions(
            envFilePaths: [path],
            overwriteExistingVars: false));
    }

    private static string? FindEnvFile(string fileName)
    {
        foreach (var startDir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(startDir);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, fileName);
                if (File.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }
        }

        return null;
    }

    public static string? FindRepoRoot()
    {
        foreach (var startDir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(startDir);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "SampleApp.sln"))
                    || File.Exists(Path.Combine(dir.FullName, ".env.example")))
                    return dir.FullName;

                dir = dir.Parent;
            }
        }

        return null;
    }
}
