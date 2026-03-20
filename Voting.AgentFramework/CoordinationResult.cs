internal record CoordinationResult(
    string Task,
    string FinalAnswer,
    double Confidence,
    ConsensusMode Mode,
    Dictionary<string, int> VoteBreakdown,
    string Flag
);