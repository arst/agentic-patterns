using System.Text.Json.Serialization;

namespace Planning.AgentFramework;

public sealed class Plan
{
    [JsonPropertyName("steps")] public List<PlanStep> Steps { get; set; } = new();
}
