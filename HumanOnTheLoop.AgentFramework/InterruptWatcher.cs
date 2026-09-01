namespace HumanOnTheLoop.AgentFramework;

/// Reads stdin on a background thread so the main loop can ask "has anyone said anything?"
/// without blocking on a human who is, most of the time, saying nothing.
///
/// A blocking read per step would turn this back into human-in-the-loop: the agent would be
/// waiting on the human at every action, which is exactly what this pattern exists to avoid.
public sealed class InterruptWatcher
{
    readonly Queue<string> lines = new();
    readonly Lock gate = new();

    public InterruptWatcher() =>
        // Background, not awaited: at EOF (piped input, Pattern Explorer) the loop simply ends
        // and every window comes back empty, which is the correct "nobody objected".
        Task.Run(() =>
        {
            while (Console.ReadLine() is { } line)
                lock (gate) lines.Enqueue(line);
        });

    /// Waits out the observation window, then reports what the human typed during it, if anything.
    public async Task<string?> WatchAsync(TimeSpan window)
    {
        await Task.Delay(window);
        lock (gate) return lines.Count > 0 ? lines.Dequeue() : null;
    }
}
