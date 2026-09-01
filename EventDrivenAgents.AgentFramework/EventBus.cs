using System.Threading.Channels;

namespace EventDrivenAgents.AgentFramework;

public sealed record AgentEvent(string Topic, string Payload, string Source, int Generation);

/// An in-process event bus over a bounded `Channel`, with the one thing an event-driven agent
/// system cannot do without: a budget.
///
/// Agents that publish in reaction to events form a graph nobody wrote down. Two handlers whose
/// outputs feed each other is not a bug you can see in either handler - it is a property of the
/// wiring, and it turns into an infinite billed loop the first time a model phrases an answer
/// slightly differently. So every event carries the generation it belongs to, the bus refuses
/// events past a maximum generation, and the whole run is capped. Unroutable events are kept
/// rather than dropped: a silent drop looks exactly like a handler that never fired.
public sealed class EventBus(int maxEvents, int maxGeneration)
{
    readonly Channel<AgentEvent> channel = Channel.CreateUnbounded<AgentEvent>();
    readonly Dictionary<string, List<Func<AgentEvent, Task<IReadOnlyList<AgentEvent>>>>> handlers =
        new(StringComparer.OrdinalIgnoreCase);

    public List<AgentEvent> DeadLetters { get; } = [];
    public int Published { get; private set; }

    public void Subscribe(string topic, Func<AgentEvent, Task<IReadOnlyList<AgentEvent>>> handler)
    {
        if (!handlers.TryGetValue(topic, out var list)) handlers[topic] = list = [];
        list.Add(handler);
    }

    /// Returns false when the event was refused - over budget, too deep, or nobody subscribes.
    public bool Publish(AgentEvent @event)
    {
        if (Published >= maxEvents || @event.Generation > maxGeneration ||
            !handlers.ContainsKey(@event.Topic))
        {
            DeadLetters.Add(@event);
            return false;
        }

        Published++;
        channel.Writer.TryWrite(@event);
        return true;
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
