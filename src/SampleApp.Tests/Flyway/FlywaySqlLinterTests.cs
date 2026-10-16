namespace SampleApp.Tests.Flyway;

public class FlywaySqlLinterTests
{
    [Fact]
    public void Lint_RepoMigrations_Pass()
    {
        var result = Lakebase.FlywaySqlLinter.Lint();
        Assert.True(result.FileCount >= 1);
        Assert.Contains(result.ManifestLines, line => line.Contains("Offline Flyway SQL validation passed"));
    }

    [Fact]
    public void CompareVersions_NumericOrder()
    {
        Assert.True(Lakebase.FlywaySqlLinter.CompareVersions("2", "1") > 0);
        Assert.True(Lakebase.FlywaySqlLinter.CompareVersions("1.1", "1") > 0);
        Assert.True(Lakebase.FlywaySqlLinter.CompareVersions("1", "1.0") == 0);
        Assert.True(Lakebase.FlywaySqlLinter.CompareVersions("2", "10") < 0);
    }

    [Fact]
    public void Lint_BadFilename_Fails()
    {
        var root = Path.Combine(Path.GetTempPath(), "lakebase-lint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "db", "migrations"));
        Directory.CreateDirectory(Path.Combine(root, "conf"));
        File.WriteAllText(Path.Combine(root, "db", "migrations", "bad.sql"), "SELECT 1;");
        File.WriteAllText(
            Path.Combine(root, "conf", "flyway.conf"),
            "flyway.locations=filesystem:db/migrations\nflyway.table=flyway_schema_history\nflyway.defaultSchema=public\n");

        var ex = Assert.Throws<InvalidOperationException>(() => Lakebase.FlywaySqlLinter.Lint(root));
        Assert.Contains("Invalid Flyway filename", ex.Message);
    }

    [Fact]
    public void Lint_ExecutableFlywayConfig_Fails()
    {
        var root = Path.Combine(Path.GetTempPath(), "lakebase-lint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "db", "migrations"));
        Directory.CreateDirectory(Path.Combine(root, "conf"));
        File.WriteAllText(Path.Combine(root, "db", "migrations", "V1__initial.sql"), "SELECT 1;");
        File.Copy(
            Path.Combine(Lakebase.EnvConfig.FindRepoRoot()!, "conf", "flyway.conf"),
            Path.Combine(root, "conf", "flyway.conf"));
        File.AppendAllText(
            Path.Combine(root, "conf", "flyway.conf"),
            "\nflyway.callbacks=com.example.UntrustedCallback\n");

        var ex = Assert.Throws<InvalidOperationException>(() => Lakebase.FlywaySqlLinter.Lint(root));
        Assert.Contains("unsupported setting flyway.callbacks", ex.Message);
    }

    [Theory]
    [InlineData("V2__run.sh", "Only .sql migrations are allowed")]
    [InlineData("sub/V2_1__nested.sql", "Subdirectories are not allowed")]
    public void Lint_NonSqlOrNestedMigration_Fails(string extra, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "lakebase-lint-" + Guid.NewGuid().ToString("N"));
        var migrations = Path.Combine(root, "db", "migrations");
        Directory.CreateDirectory(migrations);
        Directory.CreateDirectory(Path.Combine(root, "conf"));
        File.WriteAllText(Path.Combine(migrations, "V1__initial.sql"), "SELECT 1;");
        var extraPath = Path.Combine(migrations, extra);
        Directory.CreateDirectory(Path.GetDirectoryName(extraPath)!);
        File.WriteAllText(extraPath, "echo pwned");
        File.Copy(
            Path.Combine(Lakebase.EnvConfig.FindRepoRoot()!, "conf", "flyway.conf"),
            Path.Combine(root, "conf", "flyway.conf"));

        var ex = Assert.Throws<InvalidOperationException>(() => Lakebase.FlywaySqlLinter.Lint(root));
        Assert.Contains(expected, ex.Message);
    }
}
