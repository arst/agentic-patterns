using System.Numerics.Tensors;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Shared;

var builder = Settings.CreateKernelBuilder();
var kernel = builder.Build();
var embeddingService = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

var policyChunks = new[]
{
    new
    {
        Id = "remote-1", Source = "remote-work-policy-2025.pdf",
        Content =
            "Employees may work remotely up to 3 days per week. Manager approval is required for full remote arrangements. Remote work agreements must be renewed annually."
    },
    new
    {
        Id = "remote-2", Source = "remote-work-policy-2025.pdf",
        Content =
            "Remote workers must maintain a dedicated workspace with reliable internet (minimum 50 Mbps). The company provides a one-time €500 home office stipend."
    },
    new
    {
        Id = "pto-1", Source = "leave-policy-2025.pdf",
        Content =
            "Full-time employees receive 25 days of paid time off per year. Unused PTO can be carried over up to 5 days into the next calendar year."
    },
    new
    {
        Id = "pto-2", Source = "leave-policy-2025.pdf",
        Content =
            "Parental leave is 16 weeks fully paid for the primary caregiver and 6 weeks for the secondary caregiver. This applies to both birth and adoption."
    },
    new
    {
        Id = "expense-1", Source = "expense-policy-2025.pdf",
        Content =
            "Business travel expenses require pre-approval for amounts exceeding €500. Economy class is standard for flights under 6 hours. Meal reimbursement is capped at €50/day."
    }
};

// ponytail: 5 chunks need a List + cosine loop, not a vector store.
// (Microsoft.SemanticKernel.Connectors.InMemory 1.74.0-preview is runtime-incompatible with the
// VectorData.Abstractions 10.x this package graph resolves to — TypeLoadException on SearchAsync.)
var embeddings = await embeddingService.GenerateAsync(policyChunks.Select(c => c.Content).ToList());
var index = embeddings.Zip(policyChunks, (e, c) => (c.Source, c.Content, e.Vector)).ToList();

Console.WriteLine($"Indexed {policyChunks.Length} policy chunks.\n");

kernel.Plugins.AddFromFunctions("Policies", [
    KernelFunctionFactory.CreateFromMethod(
        async (string query) =>
        {
            var queryVector = (await embeddingService.GenerateAsync(query)).Vector;
            var results = index
                .Select(p => (p.Source, p.Content, Score: TensorPrimitives.CosineSimilarity(p.Vector.Span, queryVector.Span)))
                .Where(p => p.Score >= 0.5f) // cosine similarity: skip weakly related chunks
                .OrderByDescending(p => p.Score)
                .Take(3)
                .Select(p => $"[{p.Source}] {p.Content}")
                .ToList();

            return results.Count > 0 ? string.Join("\n", results) : "No relevant policy found.";
        },
        "SearchPolicies",
        "Searches the company policy documents and returns the most relevant excerpts with their source names.")
]);

var chat = kernel.GetRequiredService<IChatCompletionService>();
var settings = new PromptExecutionSettings
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
};

var history = new ChatHistory(
    """
    You are a helpful HR assistant that answers questions about company policies.
    Always use the SearchPolicies tool to find relevant policy information before answering.
    Cite your sources by mentioning the document name.
    If no relevant policy is found, say so honestly.
    """);

string[] questions =
[
    "How many days can I work from home per week?",
    "What's the parental leave policy for adoptions?",
    "Can I fly business class on a 4-hour flight?"
];

foreach (var question in questions)
{
    Console.WriteLine($"User: {question}");
    history.AddUserMessage(question);

    var response = await chat.GetChatMessageContentAsync(history, settings, kernel);

    Console.WriteLine($"Agent: {response.Content}\n");
    history.Add(response);
}