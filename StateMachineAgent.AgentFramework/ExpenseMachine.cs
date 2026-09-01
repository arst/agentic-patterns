namespace StateMachineAgent.AgentFramework;

public enum State { Intake, NeedInfo, Classify, Plan, Approval, Execute, Verify, Complete, Rejected }

/// What the model is allowed to say at a given state. The model never names a *state*, only a
/// decision; the host maps decision to state. That is the whole discipline of this pattern:
/// the model supplies judgement inside a step, the host owns which step comes next.
public enum Decision { Sufficient, Insufficient, Routine, NeedsApproval, Approve, Reject, Ok, Failed }

public sealed class IllegalTransitionException(State from, Decision decision)
    : InvalidOperationException($"'{decision}' is not a legal decision in state {from}.");

public static class ExpenseMachine
{
    static readonly Dictionary<State, Dictionary<Decision, State>> Transitions = new()
    {
        [State.Intake] = new() { [Decision.Sufficient] = State.Classify, [Decision.Insufficient] = State.NeedInfo },
        [State.NeedInfo] = new() { [Decision.Sufficient] = State.Intake, [Decision.Failed] = State.Rejected },
        [State.Classify] = new() { [Decision.Routine] = State.Plan, [Decision.NeedsApproval] = State.Approval },
        [State.Approval] = new() { [Decision.Approve] = State.Plan, [Decision.Reject] = State.Rejected },
        [State.Plan] = new() { [Decision.Ok] = State.Execute, [Decision.Failed] = State.Rejected },
        [State.Execute] = new() { [Decision.Ok] = State.Verify, [Decision.Failed] = State.Rejected },
        [State.Verify] = new() { [Decision.Ok] = State.Complete, [Decision.Failed] = State.Plan }
    };

    public static bool IsTerminal(State state) => !Transitions.ContainsKey(state);

    /// The legal decisions from here - handed to the model as its menu, so a wrong answer is a
    /// wrong *choice* rather than an invented step.
    public static IReadOnlyList<Decision> Allowed(State state) =>
        Transitions.TryGetValue(state, out var map) ? [.. map.Keys] : [];

    /// Throws rather than guessing. A model that answers outside its menu is a bug to surface,
    /// not a value to coerce into the nearest legal state.
    public static State Next(State from, Decision decision) =>
        Transitions.TryGetValue(from, out var map) && map.TryGetValue(decision, out var to)
            ? to
            : throw new IllegalTransitionException(from, decision);
}

/// Cycles are legal here (Verify -> Plan on a failed check, NeedInfo -> Intake once the gap is
/// filled), so "will it terminate?" cannot be answered by the transition table alone. A per-state
/// visit budget answers it instead: any loop is bounded, and blowing the budget is a real
/// outcome the caller sees, not a silent hang.
public sealed class VisitBudget(int perState)
{
    readonly Dictionary<State, int> visits = [];

    public bool TryVisit(State state)
    {
        visits[state] = visits.GetValueOrDefault(state) + 1;
        return visits[state] <= perState;
    }

    public int Count(State state) => visits.GetValueOrDefault(state);
}
