using dotenv.net;

namespace Lakebase;

public static class EnvConfig
{
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

        LoadEnvFile(".env.platform");
        _platformLoaded = true;
    }

    public static void LoadDatabase()
    {
        if (_databaseLoaded)
            return;

        LoadEnvFile(".env.database");
        _databaseLoaded = true;
    }

    private static void LoadEnvFile(string fileName)
    {
        var path = FindEnvFile(fileName);
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
                    || File.Exists(Path.Combine(dir.FullName, ".env.database.example")))
                    return dir.FullName;

                dir = dir.Parent;
            }
        }

        return null;
    }
}
