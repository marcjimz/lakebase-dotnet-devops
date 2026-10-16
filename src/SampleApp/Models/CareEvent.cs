using System.Text.Json.Serialization;

namespace SampleApp.Models;

public class CareEvent
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime EventDate { get; set; }
    public DateTime CreatedAt { get; set; }
    [JsonIgnore]
    public Member? Member { get; set; }
}
