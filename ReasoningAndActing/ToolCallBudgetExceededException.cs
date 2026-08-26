namespace ReasoningAndActing;

/// <summary>
/// Thrown by <see cref="ToolCallBudgetFilter"/> once the tool-call budget is exhausted. A typed
/// exception, not a message-substring match, so the catch site in Program.cs can't go silently
/// dead if the message text is ever reworded — see BoundedExecution.AgentFramework's
/// BudgetExceededException for the same shape.
/// </summary>
public sealed class ToolCallBudgetExceededException(int maxToolCalls)
    : InvalidOperationException($"Tool-call budget of {maxToolCalls} exhausted.");
