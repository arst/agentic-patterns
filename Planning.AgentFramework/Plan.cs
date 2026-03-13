using System.Text.Json.Serialization;

public sealed class Plan
{
    [JsonPropertyName("steps")] public List<PlanStep> Steps { get; set; } = new();
}