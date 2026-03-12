using System.Text.Json.Serialization;

namespace Routing.AgentFramework;

public sealed class RouteDecision
{
    [JsonPropertyName("route")]
    public Route Route { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
    
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
}