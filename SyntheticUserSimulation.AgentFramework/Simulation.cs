namespace SyntheticUserSimulation.AgentFramework;

public sealed record Persona(string Name, string Goal, string Behavior);

public sealed record UserMove(string Message, bool Stop = false);

public sealed record DialogueTurn(string User, string Agent);

public sealed record SimulationResult(
    Persona Persona,
    IReadOnlyList<DialogueTurn> Turns,
    bool ReachedTurnLimit);

public sealed class SimulationHarness
{
    public async Task<SimulationResult> RunAsync(
        Persona persona,
        Func<IReadOnlyList<DialogueTurn>, CancellationToken, Task<UserMove>> nextUser,
        Func<string, CancellationToken, Task<string>> target,
        int maxTurns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(nextUser);
        ArgumentNullException.ThrowIfNull(target);
        if (maxTurns <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTurns));

        var turns = new List<DialogueTurn>();
        for (var turn = 0; turn < maxTurns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var move = await nextUser(turns, cancellationToken);
            if (move.Stop)
                return new(persona, turns, false);

            var answer = await target(move.Message, cancellationToken);
            turns.Add(new(move.Message, answer));
        }

        return new(persona, turns, true);
    }
}
