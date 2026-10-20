using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SampleApp.Models;

namespace SampleApp.Tests.IntegrationTests;

/// <summary>Passes once lab/migrations/V3 (Providers.Email, CommentsText → Notes) is applied.</summary>
[Trait("Category", "Integration")]
public class ProviderTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    public ProviderTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetProviders_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/providers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateProvider_RoundTripsEmailAndNotes()
    {
        var provider = new
        {
            npi = $"{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}",
            name = "Dr. Ada Lovelace",
            specialty = "Cardiology",
            isActive = true,
            email = "ada@example.com",
            notes = "Accepting new patients",
        };

        var response = await _client.PostAsJsonAsync("/api/providers", provider);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<Provider>();
        Assert.NotNull(created);
        Assert.Equal(provider.email, created!.Email);
        Assert.Equal(provider.notes, created.Notes);

        var fetched = await _client.GetFromJsonAsync<Provider>($"/api/providers/{created.Id}");
        Assert.Equal(provider.notes, fetched!.Notes);
    }
}
