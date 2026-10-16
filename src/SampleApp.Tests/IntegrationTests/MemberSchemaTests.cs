using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SampleApp.Models;
using SampleApp.Tests.TestData;

namespace SampleApp.Tests.IntegrationTests;

[Trait("Category", "Integration")]
public class MemberSchemaTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public MemberSchemaTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateMember_ValidPayload_ReturnsMemberSchema()
    {
        var payload = MemberTestPayloads.CreateMember();

        var response = await _client.PostAsJsonAsync("/api/members", payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<Member>();
        Assert.NotNull(created);
        Assert.True(created!.Id > 0);
        Assert.Equal("Jane", created.FirstName);
        Assert.Equal("Doe", created.LastName);
        Assert.Equal("Commercial", created.PlanType);
        Assert.Equal("jane.doe@example.com", created.Email);
        Assert.NotEqual(default, created.DateOfBirth);
        Assert.NotEqual(default, created.CreatedAt);
        Assert.NotNull(created.CareEvents);
        Assert.Empty(created.CareEvents);
    }

    [Fact]
    public async Task GetMemberById_ReturnsFullSchemaWithCareEvents()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/members", MemberTestPayloads.CreateMember());
        var created = await createResponse.Content.ReadFromJsonAsync<Member>();
        Assert.NotNull(created);

        var careEventResponse = await _client.PostAsJsonAsync(
            "/api/careevents",
            MemberTestPayloads.CreateCareEvent(created!.Id));
        Assert.Equal(HttpStatusCode.Created, careEventResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/api/members/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        using var json = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.True(root.GetProperty("id").GetInt32() > 0);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("memberId").GetString()));
        Assert.Equal("Jane", root.GetProperty("firstName").GetString());
        Assert.Equal("Doe", root.GetProperty("lastName").GetString());
        Assert.Equal("Commercial", root.GetProperty("planType").GetString());
        Assert.Equal("jane.doe@example.com", root.GetProperty("email").GetString());
        Assert.True(root.TryGetProperty("dateOfBirth", out _));
        Assert.True(root.TryGetProperty("createdAt", out _));
        Assert.True(root.TryGetProperty("updatedAt", out _));

        var careEvents = root.GetProperty("careEvents");
        Assert.Equal(JsonValueKind.Array, careEvents.ValueKind);
        Assert.Equal(1, careEvents.GetArrayLength());

        var careEvent = careEvents[0];
        Assert.True(careEvent.GetProperty("id").GetInt32() > 0);
        Assert.Equal(created.Id, careEvent.GetProperty("memberId").GetInt32());
        Assert.Equal("Lab Work", careEvent.GetProperty("eventType").GetString());
        Assert.Equal("Routine blood panel", careEvent.GetProperty("description").GetString());
        Assert.True(careEvent.TryGetProperty("eventDate", out _));
        Assert.True(careEvent.TryGetProperty("createdAt", out _));
    }

    [Fact]
    public async Task CreateMember_WithNestedCareEvents_PersistsCareEvents()
    {
        var response = await _client.PostAsJsonAsync("/api/members", MemberTestPayloads.CreateMemberWithCareEvents());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<Member>();
        Assert.NotNull(created);
        Assert.True(created!.Id > 0);
        Assert.Single(created.CareEvents);
        Assert.Equal("Office Visit", created.CareEvents.First().EventType);
        Assert.Equal(created.Id, created.CareEvents.First().MemberId);
    }
}
