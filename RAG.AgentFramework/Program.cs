using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Connectors.InMemory;
using RAG.AgentFramework;
using Shared;
using TextSearchProvider = Microsoft.Agents.AI.TextSearchProvider;
using TextSearchProviderOptions = Microsoft.Agents.AI.TextSearchProviderOptions;

#pragma warning disable SKEXP0130

var azureClient = new AzureOpenAIClient(new Uri(Settings.AzureOpenAi.Endpoint),
    new ApiKeyCredential(Settings.AzureOpenAi.ApiKey));

var embeddingGenerator = azureClient
    .GetEmbeddingClient("text-embedding-3-small")
    .AsIEmbeddingGenerator();

var vectorStore = new InMemoryVectorStore(new InMemoryVectorStoreOptions { EmbeddingGenerator = embeddingGenerator });
var collection = vectorStore.GetCollection<string, PolicyDocument>("company_policies");
await collection.EnsureCollectionExistsAsync();

var policies = new (string Id, string Source, string Text)[]
{
    ("remote-1", "Remote Work Policy 2025",
        "Employees may work remotely up to 3 days per week. Manager approval is required for full remote. Remote work agreements must be renewed annually."),
    ("remote-2", "Remote Work Policy 2025",
        "Remote workers must maintain a dedicated workspace with reliable internet (min 50 Mbps). The company provides a one-time €500 home office stipend."),
    ("pto-1", "Leave Policy 2025",
        "Full-time employees receive 25 days of paid time off per year. Unused PTO can be carried over up to 5 days into the next calendar year."),
    ("pto-2", "Leave Policy 2025",
        "Parental leave is 16 weeks fully paid for the primary caregiver and 6 weeks for the secondary caregiver. Applies to both birth and adoption."),
    ("expense-1", "Expense Policy 2025",
        "Business travel requires pre-approval for amounts exceeding €500. Economy class is standard for flights under 6 hours. Meal reimbursement capped at €50/day.")
};

var texts = policies.Select(p => p.Text).ToList();
var embeddings = await embeddingGenerator.GenerateAsync(texts);

for (var i = 0; i < policies.Length; i++)
    await collection.UpsertAsync(new PolicyDocument
    {
        Id = policies[i].Id,
        SourceName = policies[i].Source,
        Text = policies[i].Text,
        TextEmbedding = embeddings[i].Vector
    });

Console.WriteLine($"Indexed {policies.Length} policy documents.\n");

async Task<IEnumerable<TextSearchProvider.TextSearchResult>> SearchPoliciesAsync(
    string query, CancellationToken cancellationToken)
{
    // Embed the query and search the vector store
    var queryEmbedding = await embeddingGenerator.GenerateAsync(query, cancellationToken: cancellationToken);

    var searchResults = collection.SearchAsync(
        queryEmbedding.Vector,
        3,
        cancellationToken: cancellationToken);

    var results = new List<TextSearchProvider.TextSearchResult>();
    await foreach (var result in searchResults)
        results.Add(new TextSearchProvider.TextSearchResult
        {
            Text = result.Record.Text ?? "",
            SourceName = result.Record.SourceName
        });

    return results;
}

var textSearchOptions = new TextSearchProviderOptions
{
    SearchTime = TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke
};
var chatClient = Settings.ChatClient;

var agent = new ChatClientAgent(
        chatClient,
        """
        You are a helpful HR assistant that answers questions about company policies.
        Base your answers ONLY on the context provided to you.
        Always cite the source document name in your response.
        If the context doesn't contain relevant information, say so honestly.
        """,
        "PolicyAssistant")
    .AsBuilder()
    .UseAIContextProviders(new TextSearchProvider(SearchPoliciesAsync, textSearchOptions))
    .Build();


// ----------------------------------------------
// 6. Run the RAG-enabled agent
// ----------------------------------------------

var thread = await agent.CreateSessionAsync();

string[] questions =
[
    "How many days can I work from home per week?",
    "What's the parental leave policy for adoptions?",
    "Can I fly business class on a 4-hour flight?"
];

foreach (var question in questions)
{
    Console.WriteLine($"User: {question}");

    var result = await agent.RunAsync(question, thread);

    Console.WriteLine($"Agent: {result}\n");
}