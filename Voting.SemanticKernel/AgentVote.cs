internal record AgentVote(
    string AgentName,
    string FullResponse,
    string ExtractedAnswer,
    double Confidence
);