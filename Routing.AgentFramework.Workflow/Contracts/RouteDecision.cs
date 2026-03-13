namespace Routing.AgentFramework.Workflow;

public record RouteDecision(
    Route Route,
    string Reason,
    double Confidence);