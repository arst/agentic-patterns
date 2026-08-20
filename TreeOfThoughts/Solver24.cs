using System.Text.RegularExpressions;

namespace TreeOfThoughts;

// The model proposes moves; this host code is the authority on whether they are legal
// and whether the puzzle is actually solved. Never trust a textual "done".
internal static class Solver24
{
    // ponytail: doubles + epsilon; Game-of-24 fractions stay well inside 1e-6
    private const double Tol = 1e-6;

    private static readonly Regex StepPattern = new(
        @"^\s*(-?[\d.]+)\s*([+*/-])\s*(-?[\d.]+)\s*=\s*(-?[\d.]+)", RegexOptions.Compiled);

    /// <summary>
    /// Deterministically verifies a step sequence against the starting numbers, tracking its own
    /// multiset (the model's "remaining" claim is ignored). A "done" step is only valid as the
    /// last step with exactly one remaining number ≈ 24. A valid prefix without "done" passes.
    /// </summary>
    public static bool Verify(IReadOnlyList<string> steps, double[] start, out string reason)
    {
        var remaining = start.ToList();

        foreach (var (step, i) in steps.Select((s, i) => (s, i)))
        {
            if (step.StartsWith("done", StringComparison.OrdinalIgnoreCase))
            {
                if (i == steps.Count - 1 && remaining is [var last] && Math.Abs(last - 24) < Tol)
                {
                    reason = "";
                    return true;
                }

                reason = $"'done' claimed but remaining numbers are [{string.Join(", ", remaining)}]";
                return false;
            }

            var m = StepPattern.Match(step);
            if (!m.Success)
            {
                reason = $"unparseable step '{step}'";
                return false;
            }

            double a = double.Parse(m.Groups[1].Value), b = double.Parse(m.Groups[3].Value),
                claimed = double.Parse(m.Groups[4].Value);
            if (!Remove(remaining, a))
            {
                reason = $"{a} is not among the remaining numbers";
                return false;
            }

            if (!Remove(remaining, b))
            {
                reason = $"{b} is not among the remaining numbers";
                return false;
            }

            var actual = m.Groups[2].Value switch { "+" => a + b, "-" => a - b, "*" => a * b, _ => a / b };
            if (Math.Abs(actual - claimed) > Tol)
            {
                reason = $"{a} {m.Groups[2].Value} {b} = {actual}, not {claimed}";
                return false;
            }

            remaining.Add(actual);
        }

        reason = "";
        return true;
    }

    private static bool Remove(List<double> pool, double value)
    {
        var i = pool.FindIndex(x => Math.Abs(x - value) < Tol);
        if (i < 0) return false;
        pool.RemoveAt(i);
        return true;
    }
}
