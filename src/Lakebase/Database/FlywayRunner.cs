using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Lakebase;

/// <summary>
/// Hosts the pinned Flyway CLI. Does not parse SQL or keep a second ledger.
/// Passwords stay in the child environment and are never written to logs.
/// </summary>
public static class FlywayRunner
{
    private static readonly Regex FailFast = new(
        @"Found non-empty schema|no schema history table|baselineOnMigrate|Validate failed|checksum mismatch|Detected failed migration|already exists|syntax error|ERROR: Migration V|lock timeout|statement timeout",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Transient = new(
        @"Unable to obtain connection|Unable to connect to|connection refused|Connection to .+ refused|the connection attempt failed|Connection reset|connect timed out|connection timed out|the database system is starting up|remaining connection slots|could not connect|Communications link failure|Network is unreachable|Temporary failure in name resolution|Name or service not known|No route to host|SSL connection has been closed|unexpectedly closed|I/O error|IO Error|Connection is not available|failed to connect",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static Task MigrateAsync(DatabaseConnectionSettings settings, bool retryTransient = true, CancellationToken ct = default)
        => RunAsync(settings, ["migrate"], captureStdout: false, retryTransient, ct);

    public static Task ValidateAsync(DatabaseConnectionSettings settings, CancellationToken ct = default)
        => RunAsync(settings, ["validate"], captureStdout: false, retryTransient: false, ct);

    public static Task<string> InfoAsync(DatabaseConnectionSettings settings, CancellationToken ct = default)
        => RunAsync(settings, ["info"], captureStdout: true, retryTransient: false, ct);

    public static async Task<string> RunAsync(
        DatabaseConnectionSettings settings,
        IReadOnlyList<string> flywayArgs,
        bool captureStdout,
        bool retryTransient,
        CancellationToken ct = default)
    {
        var max = 1;
        if (retryTransient)
        {
            max = 6;
            if (int.TryParse(Environment.GetEnvironmentVariable("FLYWAY_MIGRATE_MAX_ATTEMPTS"), out var parsed) && parsed > 0)
                max = parsed;
        }

        InvalidOperationException? last = null;
        for (var attempt = 1; attempt <= max; attempt++)
        {
            try
            {
                return await InvokeOnceAsync(settings, flywayArgs, captureStdout, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (retryTransient && attempt < max && IsTransient(ex.Message) && !IsFailFast(ex.Message))
            {
                last = ex;
                CliLog.Progress($"flyway {string.Join(' ', flywayArgs)} failed (attempt {attempt}/{max}); waiting for compute...");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 15), ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (retryTransient)
            {
                throw new InvalidOperationException(
                    $"flyway {string.Join(' ', flywayArgs)} failed with a non-transient error; not retrying",
                    ex);
            }
        }

        throw new InvalidOperationException(
            $"flyway {string.Join(' ', flywayArgs)} failed after {max} connection/compute attempts",
            last);
    }

    public static bool IsFailFast(string log) => FailFast.IsMatch(log);

    public static bool IsTransient(string log) => Transient.IsMatch(log);

    private static async Task<string> InvokeOnceAsync(
        DatabaseConnectionSettings settings,
        IReadOnlyList<string> flywayArgs,
        bool captureStdout,
        CancellationToken ct)
    {
        var repoRoot = EnvConfig.FindRepoRoot()
            ?? throw new InvalidOperationException("Could not find repo root (SampleApp.sln).");
        var config = Path.Combine(repoRoot, "conf", "flyway.conf");
        if (!File.Exists(config))
            throw new InvalidOperationException($"Missing Flyway config: {config}");

        var flyway = await FlywayInstaller.ResolveBinaryAsync(ct).ConfigureAwait(false);
        var resolved = await LakebaseConnectionFactory.ResolveConnectionStringAsync(settings, ct).ConfigureAwait(false);
        var (jdbc, user, password) = FlywayJdbc.FromDatabaseUrl(resolved);
        if (string.IsNullOrEmpty(user))
            throw new InvalidOperationException("DATABASE_URL is missing Username");

        var psi = new ProcessStartInfo
        {
            FileName = flyway,
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add($"-configFiles={config}");
        foreach (var arg in flywayArgs)
            psi.ArgumentList.Add(arg);

        psi.Environment["FLYWAY_URL"] = jdbc;
        psi.Environment["FLYWAY_USER"] = user;
        psi.Environment["FLYWAY_PASSWORD"] = password;
        psi.Environment["FLYWAY_REPO_ROOT"] = repoRoot;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start Flyway CLI '{flyway}'.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var safeOut = Redact(stdout, password);
        var safeErr = Redact(stderr, password);
        var combined = string.Join(Environment.NewLine, new[] { safeOut, safeErr }.Where(s => !string.IsNullOrWhiteSpace(s)));

        if (!string.IsNullOrWhiteSpace(safeOut) && !captureStdout)
            CliLog.Dump(safeOut);
        if (!string.IsNullOrWhiteSpace(safeErr))
            Console.Error.WriteLine(safeErr);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"flyway {string.Join(' ', flywayArgs)} failed with exit code {process.ExitCode}.{Environment.NewLine}{combined}");
        }

        return safeOut;
    }

    private static string Redact(string text, string? password)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(password))
            return text;
        return new StringBuilder(text).Replace(password, "***").ToString();
    }
}
