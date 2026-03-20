internal record TaskDefinition(
    string Id,
    string Description,
    bool UseHeuristicEval,
    Func<string, Trial, bool>? Evaluator
);