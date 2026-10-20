using System.Diagnostics;
using System.Text.Json;

namespace Lakebase;

/// <summary>Runs the Databricks CLI. Abstracted so tests can fake CLI output.</summary>
public interface IDatabricksCli
{
    Task<string> RunJsonAsync(IReadOnlyList<string> args, CancellationToken ct = default);
}

/// <summary>
/// Developer identity via the Databricks CLI login (<c>databricks auth login</c>): the same
/// profile the Lakebase SCM extension uses, so no PAT or client secret is stored locally.
/// </summary>
public sealed class DatabricksCli : IDatabricksCli
{
    public static IDatabricksCli Default { get; set; } = new DatabricksCli();

    public async Task<string> RunJsonAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DATABRICKS_CLI_PATH") ?? "databricks")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("json");

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start databricks CLI");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Databricks CLI not found. Install it and run 'databricks auth login', or set LAKEBASE_AUTH_MODE=pat with DATABRICKS_TOKEN.",
                ex);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"databricks {args[0]} {args[1]} failed: {FirstLine(await stderr)}");
            return await stdout;
        }
    }

    public static IReadOnlyList<string> TargetArgs(string? profile, string? host)
        => !string.IsNullOrEmpty(profile) ? ["--profile", profile]
            : !string.IsNullOrEmpty(host) ? ["--host", host]
            : [];

    /// <summary>OAuth access token for the profile/host, refreshed by the CLI when near expiry.</summary>
    public static async Task<(string Token, DateTime ExpiresUtc)> GetAccessTokenAsync(
        IDatabricksCli cli,
        string? profile,
        string? host,
        CancellationToken ct = default)
    {
        var json = await cli.RunJsonAsync(["auth", "token", .. TargetArgs(profile, host)], ct);
        using var doc = JsonDocument.Parse(json);
        var token = doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("databricks auth token returned no access_token");
        var expiry = doc.RootElement.TryGetProperty("expiry", out var exp)
            && DateTimeOffset.TryParse(exp.GetString(), out var parsed)
            ? parsed.UtcDateTime
            : DateTime.UtcNow.AddMinutes(30);
        return (token, expiry);
    }

    /// <summary>The signed-in user's name (email), which is also their Lakebase Postgres role.</summary>
    public static async Task<string> GetCurrentUserNameAsync(
        IDatabricksCli cli,
        string? profile,
        string? host,
        CancellationToken ct = default)
    {
        var json = await cli.RunJsonAsync(["current-user", "me", .. TargetArgs(profile, host)], ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("userName").GetString()
            ?? throw new InvalidOperationException("databricks current-user me returned no userName");
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(line) ? "(no output)" : line;
    }
}
