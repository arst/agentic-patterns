using System.Text.Json.Serialization;

/// Structured shape for the equivalence probe: do two answers to the same question assert the
/// same thing? Fail closed — an unparseable judgement is treated as NOT equivalent by the caller.
internal class EquivalenceResponse
{
    [JsonPropertyName("equivalent")] public bool Equivalent { get; set; }
}
