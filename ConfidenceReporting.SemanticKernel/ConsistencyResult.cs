internal record ConsistencyResult(
    string MajorityAnswer,
    double Score,
    int Runs,
    List<string> AllAnswers
);