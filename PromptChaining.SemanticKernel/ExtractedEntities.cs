using System.Text.Json.Serialization;

namespace PromptChaining.SemanticKernel;

public sealed class ExtractedEntities
{
    [JsonPropertyName("people")] public string[] People { get; set; } = Array.Empty<string>();
    [JsonPropertyName("orgs")] public string[] Orgs { get; set; } = Array.Empty<string>();
    [JsonPropertyName("topics")] public string[] Topics { get; set; } = Array.Empty<string>();
}