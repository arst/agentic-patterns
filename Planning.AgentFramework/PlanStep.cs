using System.Text.Json.Serialization;

namespace Planning.AgentFramework;

public sealed class PlanStep
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    [JsonPropertyName("args")] public Dictionary<string, string> Args { get; set; } = new();
    [JsonPropertyName("description")] public string Description { get; set; } = "";
}
