using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SampleApp.Models;

namespace SampleApp.Tests.IntegrationTests;

/// <summary>Care events only use V1 columns, so these pass on every branch.</summary>
[Trait("Category", "Integration")]
public class CareEventTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    public CareEventTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetForUnknownMember_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/careevents/member/999999999");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await response.Content.ReadFromJsonAsync<List<CareEvent>>();
        Assert.NotNull(events);
        Assert.Empty(events!);
    }

    [Fact]
    public void Branch_HasAtLeastV1()
        => Assert.True(SchemaVersion.Current >= 1, $"Flyway schema version is {SchemaVersion.Current?.ToString() ?? "unreadable"}; run ./scripts/flyway-migrate.sh.");
}
