namespace LearningAndAdaptation.AgentFramework;

/// <summary>Carries the question into the workflow for a single turn.</summary>
public record TurnInput(string SessionId, string Question);

/// <summary>Carries the answer forward to the critique step.</summary>
public record AnswerPayload(string SessionId, string Question, string Answer);

/// <summary>Rules extracted by the critique agent for this turn.</summary>
public record LearnedRules(string SessionId, List<string> Rules);
