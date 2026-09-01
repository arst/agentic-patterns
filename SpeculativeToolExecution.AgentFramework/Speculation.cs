using System.Diagnostics;

namespace SpeculativeToolExecution.AgentFramework;

/// A tool the host may run before the model has asked for it.
///
/// The bar is deliberately high and the host, not the model, decides who clears it: a tool is
/// speculatable only if running it and throwing the result away is indistinguishable from never
/// running it. Read-only is necessary but not sufficient - a metered API, a rate-limited search,
/// or a read that writes an audit row all fail on the "throwing it away costs nothing" half.
public sealed record SpeculatableTool(string Name, bool ReadOnly, bool FreeToDiscard)
{
    public bool CanSpeculate => ReadOnly && FreeToDiscard;
}

public sealed record SpeculationOutcome(string Key, bool Hit, TimeSpan Saved);

/// Runs likely calls while the model is still deciding, then serves whatever it actually asked
/// for from the results already in flight.
///
/// The win is wall-clock only, and it is bought with wasted calls: every miss is work billed and
/// discarded. That trade is worth taking when the tool is slow and the guess is good, and is
/// pure loss otherwise - so the run prints its hit rate, which is the number that decides
/// whether this pattern belongs in your system at all.
public sealed class Speculator(IReadOnlyDictionary<string, SpeculatableTool> tools)
{
    readonly Dictionary<string, Task<string>> inFlight = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> startedAt = new(StringComparer.OrdinalIgnoreCase);

    public List<SpeculationOutcome> Outcomes { get; } = [];

    /// Starts a speculative call. Refuses anything the policy has not cleared - a speculative
    /// side effect is a real side effect that nobody asked for.
    public bool Speculate(string toolName, string key, Func<Task<string>> call)
    {
        if (!tools.TryGetValue(toolName, out var tool) || !tool.CanSpeculate) return false;
        if (inFlight.ContainsKey(key)) return false;

        startedAt[key] = Stopwatch.GetTimestamp();
        inFlight[key] = call();
        return true;
    }

    /// Serves the call the model actually made: from a speculation if one matches, otherwise by
    /// running it now. Either way the caller gets the same value - speculation is invisible
    /// except in the timing.
    public async Task<string> ResolveAsync(string key, Func<Task<string>> call)
    {
        if (inFlight.Remove(key, out var speculated))
        {
            var saved = Stopwatch.GetElapsedTime(startedAt[key]);
            var result = await speculated;
            Outcomes.Add(new SpeculationOutcome(key, true, saved));
            return result;
        }

        Outcomes.Add(new SpeculationOutcome(key, false, TimeSpan.Zero));
        return await call();
    }

    /// Speculations nobody claimed. Awaited rather than abandoned so the run does not exit with
    /// live work behind it, and counted so the waste is visible.
    public async Task<int> DrainAsync()
    {
        var wasted = inFlight.Count;
        await Task.WhenAll(inFlight.Values);
        inFlight.Clear();
        return wasted;
    }
}
