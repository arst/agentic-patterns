namespace AgentCommunicationFaultTolerance.AgentFramework;

public sealed record Message(string Id, string From, string To, string Body, int Attempt = 1);

public sealed record Delivery(string MessageId, bool Delivered, bool Duplicate, int Attempts, string? Error);

/// A transport that behaves like a real one: it loses things, and it delivers things twice.
///
/// Both failures come from the same place. A network that can drop the ACK forces the sender to
/// choose between "retry and risk a duplicate" and "don't retry and risk a loss" - there is no
/// third option, which is why at-least-once plus receiver-side dedup is the shape everyone
/// converges on. Exactly-once delivery is not a transport you can buy; it is idempotent handling
/// you have to write.
public sealed class FlakyTransport(int seed, double lossRate, double duplicateRate)
{
    readonly Random random = new(seed);

    public bool WillDrop() => random.NextDouble() < lossRate;
    public bool WillDuplicate() => random.NextDouble() < duplicateRate;
}

/// Receiver-side dedup. The record of "I have handled this id" lives WITH the effect, so a
/// duplicate cannot slip between the check and the write.
public sealed class Inbox
{
    readonly Dictionary<string, string> handled = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Handled => handled;

    /// Returns the effect's result and whether this was a replay rather than a first delivery.
    public (string Result, bool Duplicate) Handle(Message message, Func<Message, string> effect)
    {
        if (handled.TryGetValue(message.Id, out var existing)) return (existing, true);

        var result = effect(message);
        handled[message.Id] = result;
        return (result, false);
    }
}

public sealed class ReliableChannel(FlakyTransport transport, Inbox inbox, int maxAttempts)
{
    public List<Message> DeadLetters { get; } = [];

    /// Duplicates the transport delivered that the inbox absorbed. Counted because dedup working
    /// is otherwise completely invisible: a duplicate that is correctly ignored looks exactly
    /// like a duplicate that never arrived, and "nothing happened" is a poor way to demonstrate
    /// the guarantee the whole pattern exists to provide.
    public int DuplicatesAbsorbed { get; private set; }

    public async Task<Delivery> SendAsync(Message message, Func<Message, string> effect)
    {
        string? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (transport.WillDrop())
            {
                lastError = "transport dropped the message";
                // Exponential backoff, deliberately tiny here so the sample stays watchable.
                await Task.Delay(TimeSpan.FromMilliseconds(20 * Math.Pow(2, attempt - 1)));
                continue;
            }

            var (_, duplicate) = inbox.Handle(message with { Attempt = attempt }, effect);

            // The transport may also deliver the same bytes twice. Dedup makes that a no-op
            // rather than a second side effect - which is the entire reason the id exists.
            if (transport.WillDuplicate())
            {
                inbox.Handle(message with { Attempt = attempt }, effect);
                DuplicatesAbsorbed++;
                Console.WriteLine($"    [transport delivered {message.Id} twice] absorbed by the inbox; " +
                                  "the effect did not run again");
            }

            return new Delivery(message.Id, true, duplicate, attempt, null);
        }

        DeadLetters.Add(message);
        return new Delivery(message.Id, false, false, maxAttempts, lastError);
    }

    /// The step people skip. Retries and dead-letters make each message's fate correct; only a
    /// reconciliation pass makes the CONVERSATION correct - it is where you find out that agent B
    /// is missing the one message agent A believes it sent.
    public static IReadOnlyList<string> Reconcile(IEnumerable<Message> sent, Inbox inbox) =>
        [.. sent.Select(m => m.Id).Where(id => !inbox.Handled.ContainsKey(id))];
}
