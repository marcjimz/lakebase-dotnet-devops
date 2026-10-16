using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Lakebase;

/// <summary>
/// Installs the repo-pinned Flyway CLI with checksum verification (macOS arm64/x64, Linux x64).
/// </summary>
public static class FlywayInstaller
{
    public const string DefaultVersion = "11.20.3";

    private static readonly Dictionary<string, (string Artifact, string Sha256)> Artifacts = new()
    {
        ["osx-arm64"] = (
            $"flyway-commandline-{DefaultVersion}-macosx-arm64.tar.gz",
            "119f579b083ba04acaffb929a69f5a2ace6ecc6522c1bde821567adc9d7bcdfd"),
        ["osx-x64"] = (
            $"flyway-commandline-{DefaultVersion}-macosx-x64.tar.gz",
            "52228fe30ef629472f1e178393cf94db1791990374f5b9fedd46b077397ea2e1"),
        ["linux-x64"] = (
            $"flyway-commandline-{DefaultVersion}-linux-x64.tar.gz",
            "bc0c194fc784578a9f491495f3e4196aafde3182fac11d4e01f6398d7967cd98"),
    };

    public static string Version =>
        Environment.GetEnvironmentVariable("FLYWAY_VERSION") is { Length: > 0 } v ? v : DefaultVersion;

    // On GitHub Actions default to RUNNER_TEMP (wiped per job): self-hosted runners may also run
    // untrusted PR code, which could replace a binary in a persistent home-directory cache.
    public static string CacheRoot =>
        Environment.GetEnvironmentVariable("FLYWAY_INSTALL_DIR") is { Length: > 0 } dir
            ? dir
            : IsGitHubActions && Environment.GetEnvironmentVariable("RUNNER_TEMP") is { Length: > 0 } runnerTemp
                ? Path.Combine(runnerTemp, "lakebase-flyway")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "lakebase-flyway");

    private static bool IsGitHubActions =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    public static string InstallDir => Path.Combine(CacheRoot, $"flyway-{Version}");

    public static string BinaryPath => Path.Combine(InstallDir, "flyway");

    public static async Task<string> EnsureInstalledAsync(CancellationToken ct = default)
    {
        if (Version != DefaultVersion)
        {
            throw new InvalidOperationException(
                $"This CLI pins Flyway {DefaultVersion} (checksums). Unset FLYWAY_VERSION or set it to {DefaultVersion}.");
        }

        Directory.CreateDirectory(CacheRoot);
        if (!File.Exists(BinaryPath))
            await DownloadAndExtractAsync(ct).ConfigureAwait(false);

        EnsureExecutable(BinaryPath);
        WriteGitHubPath();
        return BinaryPath;
    }

    public static string? FindExistingBinary()
    {
        var home = Environment.GetEnvironmentVariable("FLYWAY_HOME");
        if (!string.IsNullOrEmpty(home))
        {
            var candidate = Path.Combine(home, "flyway");
            if (File.Exists(candidate))
                return candidate;
        }

        return File.Exists(BinaryPath) ? BinaryPath : null;
    }

    public static async Task<string> ResolveBinaryAsync(CancellationToken ct = default)
    {
        var existing = FindExistingBinary();
        if (existing is not null)
        {
            WriteGitHubPath(Path.GetDirectoryName(existing)!);
            return existing;
        }

        return await EnsureInstalledAsync(ct).ConfigureAwait(false);
    }

    private static async Task DownloadAndExtractAsync(CancellationToken ct)
    {
        var rid = RuntimeIdentifier();
        if (!Artifacts.TryGetValue(rid, out var spec))
        {
            throw new InvalidOperationException(
                $"Unsupported platform for pinned Flyway CLI: {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}. " +
                "Supported: macOS arm64, macOS x86_64 (Rosetta), Linux x86_64.");
        }

        var tmp = Directory.CreateTempSubdirectory("lakebase-flyway-");
        try
        {
            var archive = Path.Combine(tmp.FullName, spec.Artifact);
            var baseUrl = Environment.GetEnvironmentVariable("FLYWAY_BASE_URL")
                ?? $"https://github.com/flyway/flyway/releases/download/flyway-{DefaultVersion}";
            var url = $"{baseUrl.TrimEnd('/')}/{spec.Artifact}";
            CliLog.Progress($"Downloading Flyway {DefaultVersion} ({spec.Artifact})");

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Lakebase.Cli");
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var fs = File.Create(archive))
            {
                await response.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            VerifySha256(archive, spec.Sha256, spec.Artifact);

            var extractRoot = Path.Combine(tmp.FullName, "extract");
            Directory.CreateDirectory(extractRoot);
            await using (var input = File.OpenRead(archive))
            await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                TarFile.ExtractToDirectory(gzip, extractRoot, overwriteFiles: true);
            }

            var extracted = Directory.GetDirectories(extractRoot, "flyway-*").FirstOrDefault()
                ?? throw new InvalidOperationException("Flyway archive did not contain a flyway-* directory");

            if (Directory.Exists(InstallDir))
                Directory.Delete(InstallDir, recursive: true);
            Directory.CreateDirectory(CacheRoot);
            Directory.Move(extracted, InstallDir);
            CliLog.Progress($"Installed Flyway {DefaultVersion} to {InstallDir}");
        }
        finally
        {
            try { tmp.Delete(recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private static void VerifySha256(string file, string expected, string artifact)
    {
        using var stream = File.OpenRead(file);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Flyway checksum mismatch for {artifact}{Environment.NewLine}expected: {expected}{Environment.NewLine}actual:   {hash}");
        }
    }

    private static string RuntimeIdentifier()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            && RuntimeInformation.OSArchitecture == Architecture.Arm64)
            return "osx-arm64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            && RuntimeInformation.OSArchitecture == Architecture.X64)
            return "osx-x64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && RuntimeInformation.OSArchitecture == Architecture.X64)
            return "linux-x64";
        return $"{(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : "unknown")}-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
    }

    private static void EnsureExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static void WriteGitHubPath(string? directory = null)
    {
        directory ??= InstallDir;
        var githubPath = Environment.GetEnvironmentVariable("GITHUB_PATH");
        if (!string.IsNullOrEmpty(githubPath))
            File.AppendAllText(githubPath, directory + Environment.NewLine);

        var githubEnv = Environment.GetEnvironmentVariable("GITHUB_ENV");
        if (!string.IsNullOrEmpty(githubEnv))
        {
            File.AppendAllText(
                githubEnv,
                $"FLYWAY_HOME={directory}{Environment.NewLine}FLYWAY_VERSION={Version}{Environment.NewLine}");
        }
    }
}
