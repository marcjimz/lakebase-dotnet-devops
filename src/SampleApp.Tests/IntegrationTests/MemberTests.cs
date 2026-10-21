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

    [RequiresSchemaFact(2)]
    public async Task GetMembers_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/members");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [RequiresSchemaFact(2)]
    public async Task GetMembers_DefaultsToFirstPageOfTen()
    {
        await _client.PostAsJsonAsync("/api/members", NewMember());

        var page = await _client.GetFromJsonAsync<PagedResult<Member>>("/api/members");

        Assert.NotNull(page);
        Assert.Equal(1, page!.Page);
        Assert.Equal(10, page.PageSize);
        Assert.InRange(page.Items.Count, 1, 10);
        Assert.True(page.TotalCount >= page.Items.Count);
        Assert.Equal((int)Math.Ceiling(page.TotalCount / 10.0), page.TotalPages);
        Assert.Equal(page.Items.OrderBy(m => m.Id).Select(m => m.Id), page.Items.Select(m => m.Id));
    }

    [RequiresSchemaFact(2)]
    public async Task GetMembers_PagesDoNotOverlap()
    {
        await _client.PostAsJsonAsync("/api/members", NewMember());
        await _client.PostAsJsonAsync("/api/members", NewMember());

        var first = await _client.GetFromJsonAsync<PagedResult<Member>>("/api/members?page=1&pageSize=1");
        var second = await _client.GetFromJsonAsync<PagedResult<Member>>("/api/members?page=2&pageSize=1");

        Assert.Single(first!.Items);
        Assert.Single(second!.Items);
        Assert.True(first.Items[0].Id < second.Items[0].Id);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    public async Task GetMembers_OutOfRangePaging_ReturnsBadRequest(string query)
    {
        var response = await _client.GetAsync($"/api/members?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static object NewMember() => new
    {
        memberId = $"TEST-{Guid.NewGuid():N}",
        firstName = "Page",
        lastName = "Test",
        dateOfBirth = "1990-01-01T00:00:00Z",
        planType = "Commercial",
    };

    [RequiresSchemaFact(2)]
    public async Task CreateMember_ReturnsCreated()
    {
        var member = new { memberId = $"TEST-{Guid.NewGuid():N}", firstName = "Test", lastName = "User", dateOfBirth = "1990-01-01T00:00:00Z", planType = "Commercial" };
        var response = await _client.PostAsJsonAsync("/api/members", member);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<Member>();
        Assert.NotNull(created);
        Assert.Equal(member.firstName, created!.FirstName);
    }

    [RequiresSchemaFact(2)]
    public async Task GetMember_NotFound_Returns404()
    {
        var response = await _client.GetAsync("/api/members/99999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
