using System.Threading.Channels;

namespace EventDrivenAgents.AgentFramework;

public sealed record AgentEvent(string Topic, string Payload, string Source, int Generation);

public enum Refusal { NoSubscriber, GenerationLimit, RunBudgetExceeded }

public sealed record DeadLetter(AgentEvent Event, Refusal Reason);

/// An in-process event bus over a `Channel`, with the one thing an event-driven agent system
/// cannot do without: a budget.
///
/// The bound is in the host counters, not in the channel. The queue itself is unbounded, and
/// deliberately so - a bounded channel bounds how many events may be IN FLIGHT, which is
/// backpressure, and its overflow modes either block a producer or silently drop. What needs
/// bounding here is a different quantity: how many events the run may ACCEPT, and how deep a
/// reaction chain may go. Those are counted in `Publish`, before anything is queued.
///
/// Why it needs bounding at all: agents that publish in reaction to events form a graph nobody
/// wrote down. Two handlers whose outputs feed each other is not a bug you can see in either
/// handler - it is a property of the wiring, and it turns into an infinite billed loop the first
/// time a model phrases an answer slightly differently.
///
/// Refused events are kept with a reason rather than dropped: a silent drop looks exactly like a
/// handler that never fired.
public sealed class EventBus(int maxEvents, int maxGeneration)
{
    readonly Channel<AgentEvent> channel = Channel.CreateUnbounded<AgentEvent>();
    readonly Dictionary<string, List<Func<AgentEvent, Task<IReadOnlyList<AgentEvent>>>>> handlers =
        new(StringComparer.OrdinalIgnoreCase);

    /// Events refused by the budget or the generation cap. A dead letter is a FAILURE - something
    /// that could not be processed.
    public List<DeadLetter> DeadLetters { get; } = [];

    /// Events that completed the workflow: nobody subscribes to them because there is nothing
    /// left to do. These are outputs, not failures, and filing them alongside genuine delivery
    /// failures makes the dead-letter queue useless as an alert - which matters the moment this
    /// bus is composed with **AgentCommunicationFaultTolerance**, where a dead letter means
    /// "requeue or escalate".
    public List<AgentEvent> TerminalEvents { get; } = [];

    public int Published { get; private set; }

    public void Subscribe(string topic, Func<AgentEvent, Task<IReadOnlyList<AgentEvent>>> handler)
    {
        if (!handlers.TryGetValue(topic, out var list)) handlers[topic] = list = [];
        list.Add(handler);
    }

    /// Returns false when the event was not queued - because the run is finished with it
    /// (terminal), or because a limit refused it (dead letter). The two are recorded separately.
    public bool Publish(AgentEvent @event)
    {
        if (Published >= maxEvents)
            return Refuse(@event, Refusal.RunBudgetExceeded);

        if (@event.Generation > maxGeneration)
            return Refuse(@event, Refusal.GenerationLimit);

        if (!handlers.ContainsKey(@event.Topic))
        {
            // Nothing left to react to. That is the workflow ending, not a delivery failing.
            TerminalEvents.Add(@event);
            return false;
        }

        Published++;
        channel.Writer.TryWrite(@event);
        return true;
    }

    bool Refuse(AgentEvent @event, Refusal reason)
    {
        DeadLetters.Add(new DeadLetter(@event, reason));
        return false;
    }

    /// Drains until no work is left. Each handler's output is republished through the same
    /// budget, so a reaction chain is bounded no matter how the handlers are wired.
    public async Task RunToCompletionAsync(Action<AgentEvent>? onDispatch = null)
    {
        while (channel.Reader.TryRead(out var @event))
        {
            onDispatch?.Invoke(@event);

            foreach (var handler in handlers[@event.Topic])
                foreach (var produced in await handler(@event))
                    Publish(produced with { Generation = @event.Generation + 1 });
        }
    }
}
