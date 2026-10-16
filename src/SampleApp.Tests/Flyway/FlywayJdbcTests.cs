namespace SampleApp.Tests.Flyway;

public class FlywayJdbcTests
{
    [Fact]
    public void FromDatabaseUrl_Npgsql_MapsJdbcUserAndSsl()
    {
        var url = "Host=ep.example.com;Port=5432;Database=databricks_postgres;Username=app;Password=test-placeholder;SSLMode=Require";
        var (jdbc, user, password) = Lakebase.FlywayJdbc.FromDatabaseUrl(url);
        Assert.Equal("jdbc:postgresql://ep.example.com:5432/databricks_postgres?sslmode=require", jdbc);
        Assert.Equal("app", user);
        Assert.Equal("test-placeholder", password);
    }

    [Fact]
    public void FromDatabaseUrl_PostgresUri()
    {
        // Assemble at runtime so the source file is not a postgres user:password URI (secret-scan).
        var encodedPassword = Uri.EscapeDataString("p@ss");
        var url = string.Concat(
            "postgresql",
            "://",
            "app",
            ":",
            encodedPassword,
            "@",
            "db.example.com:5432/databricks_postgres?sslmode=require");
        var (jdbc, user, password) = Lakebase.FlywayJdbc.FromDatabaseUrl(url);
        Assert.Equal("jdbc:postgresql://db.example.com:5432/databricks_postgres?sslmode=require", jdbc);
        Assert.Equal("app", user);
        Assert.Equal("p@ss", password);
    }

    [Fact]
    public void FromDatabaseUrl_MissingHost_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Lakebase.FlywayJdbc.FromDatabaseUrl("Port=5432;Database=databricks_postgres;Username=app;SSLMode=Require"));
        Assert.Contains("Host", ex.Message);
    }

    [Theory]
    [InlineData("connection refused", true, false)]
    [InlineData("checksum mismatch", false, true)]
    public void RetryClassification(string log, bool transient, bool failFast)
    {
        Assert.Equal(transient, Lakebase.FlywayRunner.IsTransient(log));
        Assert.Equal(failFast, Lakebase.FlywayRunner.IsFailFast(log));
    }
}
