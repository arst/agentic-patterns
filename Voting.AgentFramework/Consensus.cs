namespace Voting.AgentFramework;

internal static class Consensus
{
    public static CoordinationResult MajorityVote(List<AgentVote> votes, string task)
    {
        var groups = votes
            .GroupBy(v => v.ExtractedAnswer.ToLowerInvariant().Trim())
            .OrderByDescending(g => g.Count())
            .ToList();

        var winner = groups.First();
        var totalVotes = votes.Count;
        var confidence = (double)winner.Count() / totalVotes;

        Console.WriteLine("Vote distribution:");
        foreach (var g in groups)
            Console.WriteLine($"  '{g.Key}': {g.Count()}/{totalVotes} votes");

        var flag = confidence == 1.0 ? "V Unanimous"
            : confidence >= 0.6 ? "V Majority"
            : "! Split — consider synthesis LLM or human review";

        return new CoordinationResult(
            task,
            winner.Key,
            confidence,
            ConsensusMode.MajorityVote,
            groups.ToDictionary(g => g.Key, g => g.Count()),
            flag
        );
    }

    public static CoordinationResult WeightedVote(List<AgentVote> votes, string task)
    {
        var weightedGroups = votes
            .GroupBy(v => v.ExtractedAnswer.ToLowerInvariant().Trim())
            .Select(g => new
            {
                Answer = g.Key,
                TotalWeight = g.Sum(v => v.Confidence),
                VoteCount = g.Count()
            })
            .OrderByDescending(g => g.TotalWeight)
            .ToList();

        var winner = weightedGroups.First();
        var totalWeight = votes.Sum(v => v.Confidence);
        var confidence = totalWeight > 0 ? winner.TotalWeight / totalWeight : 0;

        Console.WriteLine("Weighted vote distribution:");
        foreach (var g in weightedGroups)
            Console.WriteLine($"  '{g.Answer}': weight {g.TotalWeight:F2} ({g.VoteCount} votes)");

        return new CoordinationResult(
            task,
            winner.Answer,
            confidence,
            ConsensusMode.WeightedVote,
            weightedGroups.ToDictionary(g => g.Answer, g => g.VoteCount),
            confidence >= 0.7 ? "V Clear weighted winner" : "! Close weighted result"
        );
    }
}
