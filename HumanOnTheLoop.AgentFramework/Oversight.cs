namespace HumanOnTheLoop.AgentFramework;

public sealed record ProposedAction(string Name, string Detail, bool Reversible);

public enum Oversight { Proceed, Halted, AwaitingAck }

/// Human-on-the-loop, not human-in-the-loop. The difference is the default.
///
///   in-the-loop:  the agent stops at every step and waits. Safe, and unusable past a handful of
///                 steps - the human becomes the throughput limit and starts approving blind.
///   on-the-loop:  the agent proceeds by default and the human watches, with a real ability to
///                 interrupt. Throughput is the agent's; the human spends attention only where
///                 something looks wrong.
///
/// That default is only defensible if it does not apply to everything. An irreversible action
/// gets in-the-loop treatment - silence is not consent when there is nothing to undo - so
/// "reversible?" becomes the single field that decides which regime an action falls under.
public static class OversightPolicy
{
    public static Oversight Decide(ProposedAction action, bool interrupted, bool acknowledged) =>
        (interrupted, action.Reversible, acknowledged) switch
        {
            (true, _, _) => Oversight.Halted,          // an interrupt beats everything
            (_, false, false) => Oversight.AwaitingAck, // irreversible: silence is not consent
            (_, false, true) => Oversight.Proceed,
            _ => Oversight.Proceed                      // reversible and unobjected: go
        };
}
