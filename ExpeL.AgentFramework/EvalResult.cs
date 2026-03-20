using System.Text.Json.Serialization;

internal class EvalResult
{
    [JsonPropertyName("passed")] public bool Passed { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}