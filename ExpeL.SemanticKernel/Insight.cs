using System.Text.Json.Serialization;

internal class Insight
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("rule")] public string Rule { get; set; } = "";
    [JsonPropertyName("score")] public int Score { get; set; } // upvoted by AGREE, reset by EDIT, pruned at -3
}