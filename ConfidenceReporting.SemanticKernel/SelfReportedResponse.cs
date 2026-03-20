using System.Text.Json.Serialization;

internal class SelfReportedResponse
{
    [JsonPropertyName("answer")] public string Answer { get; set; } = "";

    [JsonPropertyName("confidence")] public double Confidence { get; set; } = 0.5;

    [JsonPropertyName("reasoning")] public string Reasoning { get; set; } = "";
}