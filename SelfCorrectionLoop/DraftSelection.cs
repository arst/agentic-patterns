using System.Globalization;
using System.Text.RegularExpressions;

namespace SelfCorrectionLoop;

internal static class DraftSelection
{
    public static double ParseScore(string feedback) =>
        Regex.Match(feedback, @"SCORE:\s*([01](?:\.\d+)?)") is { Success: true } m
            ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0.0;

    /// <summary>Highest-scoring draft that fits the limit; if none fit, highest-scoring overall.</summary>
    public static (string Draft, double Score) Best(
        IReadOnlyList<(string Draft, double Score)> drafts, int charLimit)
    {
        var fitting = drafts.Where(d => d.Draft.Length <= charLimit).ToList();
        return (fitting.Count > 0 ? fitting : drafts.ToList()).MaxBy(d => d.Score);
    }
}
