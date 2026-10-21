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
    public async Task GetProviders_DefaultsToFirstPageOfTen()
    {
        var page = await _client.GetFromJsonAsync<PagedResult<Provider>>("/api/providers");

        Assert.NotNull(page);
        Assert.Equal(1, page!.Page);
        Assert.Equal(10, page.PageSize);
        Assert.True(page.Items.Count <= 10);
        Assert.Equal((int)Math.Ceiling(page.TotalCount / 10.0), page.TotalPages);
        Assert.Equal(page.Items.OrderBy(p => p.Id).Select(p => p.Id), page.Items.Select(p => p.Id));
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=101")]
    public async Task GetProviders_OutOfRangePaging_ReturnsBadRequest(string query)
    {
        var response = await _client.GetAsync($"/api/providers?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
