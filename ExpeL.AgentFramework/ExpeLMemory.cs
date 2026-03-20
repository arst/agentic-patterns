using System.Text.Json.Serialization;

internal class ExpeLMemory
{
    [JsonPropertyName("experience_bank")] public List<Trial> ExperienceBank { get; set; } = [];
    [JsonPropertyName("insights")] public List<Insight> Insights { get; set; } = [];
}