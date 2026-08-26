namespace RegressionEvals.AgentFramework;

// A trace is ground truth about what HAPPENED, never about what SHOULD have happened. Extracting
// one gives a candidate; a reviewer supplies or confirms the expected result before it can gate a
// release. Promoting the model's own historical answer freezes its mistakes into the suite.
public record CandidateCase(string Id, string Question, string ObservedAnswer, string SourceTrace);
public record GoldenCase(string Id, string Question, string ExpectedAnswer, string Tier, string ReviewedBy);
