namespace Lakebase;

/// <summary>
/// Reads <c>host</c> for a profile from <c>~/.databrickscfg</c> (or <c>DATABRICKS_CONFIG_FILE</c>), so
/// <c>.env</c> only needs <c>DATABRICKS_CONFIG_PROFILE</c>. Never reads or returns secrets.
/// </summary>
public static class DatabricksConfigFile
{
    public static string? HostForProfile(string? profile, string? path = null)
    {
        if (string.IsNullOrEmpty(profile))
            return null;

        path ??= Environment.GetEnvironmentVariable("DATABRICKS_CONFIG_FILE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".databrickscfg");
        if (!File.Exists(path))
            return null;

        var inProfile = false;
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inProfile = string.Equals(line[1..^1].Trim(), profile, StringComparison.Ordinal);
                continue;
            }

            if (!inProfile)
                continue;

            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals("host", StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim().TrimEnd('/');
        }

        return null;
    }
}
