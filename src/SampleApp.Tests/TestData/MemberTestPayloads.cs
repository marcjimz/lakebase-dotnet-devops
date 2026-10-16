namespace SampleApp.Tests.TestData;

public static class MemberTestPayloads
{
    public static object CreateMember() => new
    {
        memberId = $"TEST-{Guid.NewGuid():N}",
        firstName = "Jane",
        lastName = "Doe",
        dateOfBirth = "1990-05-15T00:00:00Z",
        planType = "Commercial",
        email = "jane.doe@example.com"
    };

    public static object CreateMemberWithCareEvents() => new
    {
        memberId = $"TEST-{Guid.NewGuid():N}",
        firstName = "John",
        lastName = "Smith",
        dateOfBirth = "1985-03-20T00:00:00Z",
        planType = "Medicare",
        careEvents = new[]
        {
            new
            {
                eventType = "Office Visit",
                description = "Annual wellness exam",
                eventDate = "2026-09-01T14:30:00Z"
            }
        }
    };

    public static object CreateCareEvent(int memberId) => new
    {
        memberId,
        eventType = "Lab Work",
        description = "Routine blood panel",
        eventDate = "2026-09-10T09:00:00Z"
    };
}
