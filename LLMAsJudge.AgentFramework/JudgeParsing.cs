using System.Text.Json;

namespace LLMAsJudge.AgentFramework;

public enum Preference { A, B, Indeterminate }

/// <summary>One pairwise trial: which slot the reference candidate sat in, and what the judge said.
/// The slot must survive into the statistic — fold it away early and the randomisation stops
/// measuring anything.</summary>
public readonly record struct Trial(bool ReferenceInPositionA, Preference Verdict);

// Parses judge verdicts and summarizes them across balanced position orderings.
public static class JudgeParsing
{
    public static Preference Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Preference.Indeterminate;

        string? winner;
        try
        {
            winner = JsonSerializer.Deserialize<Dictionary<string, string>>(json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))?.GetValueOrDefault("winner");
        }
        catch (JsonException)
        {
            return Preference.Indeterminate;
        }

        // Exact-case match only: a judge that writes "a" instead of "A" has not followed the
        // output contract, and its verdict should not be counted. Do not loosen to OrdinalIgnoreCase.
        return winner switch
        {
            "A" => Preference.A,
            "B" => Preference.B,
            _ => Preference.Indeterminate
        };
    }

    // Translates a verdict plus which slot the "reference" candidate occupied into whether the
    // reference candidate won. Indeterminate verdicts carry no position information.
    public static bool? Resolve(Preference verdict, bool referenceInPositionA) => verdict switch
    {
        Preference.A => referenceInPositionA,
        Preference.B => !referenceInPositionA,
        _ => null
    };

    /// <param name="PositionSwing">How much the reference candidate's win rate moved when it
    /// changed slots: |win rate with the reference in A − win rate with it in B|. 0 means the
    /// verdict did not depend on position; 1 means it depended on nothing else. <c>null</c> when
    /// either slot produced no determinate verdict — one slot cannot measure position bias.</param>
    public readonly record struct PreferenceReport(
        int ReferenceWins, int OtherWins, int Indeterminate, double? PositionSwing);

    // Indeterminate verdicts are excluded from the swing but counted separately.
    public static PreferenceReport Summarize(IReadOnlyList<Trial> trials)
    {
        var outcomes = trials.Select(t => Resolve(t.Verdict, t.ReferenceInPositionA)).ToList();

        var inA = ReferenceWinRate(trials, referenceInPositionA: true);
        var inB = ReferenceWinRate(trials, referenceInPositionA: false);

        return new PreferenceReport(
            outcomes.Count(r => r == true),
            outcomes.Count(r => r == false),
            outcomes.Count(r => r is null),
            inA is null || inB is null ? null : Math.Abs(inA.Value - inB.Value));
    }

    // A judge that is simply wrong — it prefers the same candidate in both slots — has a win rate
    // of 0 in both and so a swing of 0: wrong is not the same defect as position-dependent, and
    // this is what folding the slot away before the statistic used to hide.
    private static double? ReferenceWinRate(IReadOnlyList<Trial> trials, bool referenceInPositionA)
    {
        var determinate = trials
            .Where(t => t.ReferenceInPositionA == referenceInPositionA)
            .Select(t => Resolve(t.Verdict, referenceInPositionA))
            .Where(r => r is not null)
            .ToList();

        return determinate.Count == 0
            ? null
            : (double)determinate.Count(r => r == true) / determinate.Count;
    }
}
