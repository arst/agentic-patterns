internal record InsightOperations(List<InsightOp> Ops);

internal record InsightOp(string Op, int? Id, string? Rule);
