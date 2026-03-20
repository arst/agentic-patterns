using System.Text.Json.Serialization;

internal class Trial
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("task_id")] public string TaskId { get; set; } = "";
    [JsonPropertyName("task_desc")] public string TaskDescription { get; set; } = "";
    [JsonPropertyName("attempt")] public int AttemptNumber { get; set; }
    [JsonPropertyName("output")] public string AgentOutput { get; set; } = "";
    [JsonPropertyName("succeeded")] public bool Succeeded { get; set; }
    [JsonPropertyName("eval_details")] public string? EvaluationDetails { get; set; }
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; }
}