using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SampleApp.Models;

namespace SampleApp.Tests.IntegrationTests;

[Trait("Category", "Integration")]
public class MemberTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    public MemberTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetMembers_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/members");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateMember_ReturnsCreated()
    {
        var member = new { memberId = $"TEST-{Guid.NewGuid():N}", firstName = "Test", lastName = "User", dateOfBirth = "1990-01-01T00:00:00Z", planType = "Commercial" };
        var response = await _client.PostAsJsonAsync("/api/members", member);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<Member>();
        Assert.NotNull(created);
        Assert.Equal(member.firstName, created!.FirstName);
    }

    [Fact]
    public async Task GetMember_NotFound_Returns404()
    {
        var response = await _client.GetAsync("/api/members/99999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
