using System.Net;
using System.Text.Json;
using Lakebase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace SampleApp.Tests.Hardening;

public class TokenLifetimeTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ExpiresIn_UsesServerLifetime()
    {
        using var doc = JsonDocument.Parse("""{"access_token":"x","expires_in":3600}""");
        Assert.Equal(Now.AddHours(1), TokenLifetime.ExpiryFromExpiresIn(doc.RootElement, Now));
    }

    [Fact]
    public void ExpiresIn_MissingFallsBackConservatively()
    {
        using var doc = JsonDocument.Parse("""{"access_token":"x"}""");
        Assert.Equal(Now + TokenLifetime.Fallback, TokenLifetime.ExpiryFromExpiresIn(doc.RootElement, Now));
    }

    [Fact]
    public void Credential_PrefersExpireTime()
    {
        using var doc = JsonDocument.Parse("""{"token":"x","expire_time":"2026-10-10T12:30:00Z"}""");
        Assert.Equal(Now.AddMinutes(30), TokenLifetime.ExpiryFromCredential(doc.RootElement, Now));
    }

    [Fact]
    public void Credential_PastExpireTimeFallsBack()
    {
        using var doc = JsonDocument.Parse("""{"token":"x","expire_time":"2026-10-10T11:00:00Z"}""");
        Assert.Equal(Now + TokenLifetime.Fallback, TokenLifetime.ExpiryFromCredential(doc.RootElement, Now));
    }
}

public class LakebaseHttpTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, false, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, false)]
    [InlineData(HttpStatusCode.NotImplemented, true, false)]
    [InlineData(HttpStatusCode.BadRequest, true, false)]
    [InlineData(HttpStatusCode.Conflict, true, false)]
    public void ShouldRetry_OnlyReplaysSafeFailures(HttpStatusCode status, bool idempotent, bool expected)
        => Assert.Equal(expected, LakebaseHttp.ShouldRetry(status, idempotent));

    [Fact]
    public void SafeErrorSummary_Truncates()
    {
        var summary = LakebaseHttp.SafeErrorSummary(new string('a', 2000));
        Assert.True(summary.Length <= 501);
    }
}

public class DataSourceRegistrationTests
{
    private sealed class TestContext(DbContextOptions<TestContext> options) : DbContext(options);

    [Fact]
    public void AddLakebaseDbContext_SharesOneDataSourceAcrossScopes()
    {
        var settings = new DatabaseConnectionSettings
        {
            DatabricksHost = "https://example.invalid",
            ConnectionString = "Host=localhost;Database=test;Username=test",
        };

        using var provider = new ServiceCollection()
            .AddLakebaseDbContext<TestContext>(settings)
            .BuildServiceProvider();

        NpgsqlDataSource first;
        NpgsqlDataSource second;
        using (var scope = provider.CreateScope())
        {
            _ = scope.ServiceProvider.GetRequiredService<TestContext>();
            first = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        }

        using (var scope = provider.CreateScope())
        {
            _ = scope.ServiceProvider.GetRequiredService<TestContext>();
            second = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        }

        Assert.Same(first, second);
    }
}

public class AppSchemaTests
{
    [Theory]
    [InlineData("public")]
    [InlineData("app_v2")]
    public void NormalizeIdentifier_AcceptsSimpleNames(string name)
        => Assert.Equal(name, FeatureBranchDdl.NormalizeIdentifier(name, "schema"));

    [Theory]
    [InlineData("public; DROP SCHEMA x")]
    [InlineData("a\"b")]
    [InlineData("")]
    public void NormalizeIdentifier_RejectsInjection(string name)
        => Assert.Throws<InvalidOperationException>(() => FeatureBranchDdl.NormalizeIdentifier(name, "schema"));
}
