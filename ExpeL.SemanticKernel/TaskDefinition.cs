internal record TaskDefinition(
    string Id,
    string Description,
    Func<string, Trial, bool> Evaluator
);