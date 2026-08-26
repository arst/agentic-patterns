using System.Text.Json;

namespace LLMAsJudge.AgentFramework;

public enum Preference { A, B, Indeterminate }

// Parses judge verdicts and summarizes them across randomized position-bias orderings.
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

    public readonly record struct PreferenceReport(
        int ReferenceWins, int OtherWins, int Indeterminate, double PositionBiasRate);

    // Indeterminate results (null) are excluded from the bias rate but counted separately.
    public static PreferenceReport Summarize(IReadOnlyList<bool?> results)
    {
        var referenceWins = results.Count(r => r == true);
        var otherWins = results.Count(r => r == false);
        var indeterminate = results.Count(r => r is null);
        var determinate = referenceWins + otherWins;
        var biasRate = determinate == 0 ? 0.0 : (double)otherWins / determinate;
        return new PreferenceReport(referenceWins, otherWins, indeterminate, biasRate);
    }
}
