// Agentic RAG: unlike classic RAG (RAG.AgentFramework), retrieval is not a fixed
// pre-step — it is a TOOL the agent calls at will. The agent decides IF retrieval
// is needed, rewrites the question into a search query, GRADES the results via a
// structured-output grader agent, and re-retrieves with a new query if grading
// says the results are insufficient (Self-RAG / CRAG-flavored loop).

using System.ClientModel;
using System.Numerics.Tensors;
using System.Text;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// ----------------------------------------------
// 1. Embed a small in-memory corpus
// ----------------------------------------------

var azureClient = new AzureOpenAIClient(new Uri(Settings.AzureOpenAi.Endpoint),
    new ApiKeyCredential(Settings.AzureOpenAi.ApiKey));

var embeddingGenerator = azureClient
    .GetEmbeddingClient(Settings.AzureOpenAi.EmbeddingModelDeployment)
    .AsIEmbeddingGenerator();

// Docs for the fictional "Helioform Nimbus 9" smart thermostat. The n8-* chunks
// are deliberate near-misses (right vocabulary, wrong product) so grading matters:
// a query about "battery backup" ranks them high, yet they don't answer the question.
(string Id, string Text)[] chunks =
[
    ("n9-pair",
        "To pair the Nimbus 9 with the Helioform Home app, hold the dial for 5 seconds until the ring pulses blue, then tap 'Add device' in the app. Pairing uses Bluetooth LE and takes under a minute."),
    ("n9-install",
        "Nimbus 9 installation: mount the backplate on the wall, connect the C-wire and heating wires to terminals 1-4, then click the unit onto the backplate. Compatible with most 24V HVAC systems."),
    ("n9-sched",
        "The Nimbus 9 supports up to 10 schedule blocks per day. Schedules can be edited on the dial or in the Helioform Home app and sync automatically across all paired devices."),
    ("n9-eco",
        "Eco Mode on the Nimbus 9 lowers the target temperature by 2°C when the geofence detects everyone has left home, and re-heats automatically 30 minutes before your usual arrival time."),
    ("n9-reserve",
        "If the mains supply is interrupted, the Nimbus 9's internal reserve cell keeps the display, schedules and radio alive for up to 6 hours. All settings are retained indefinitely in flash memory."),
    ("n9-sensor",
        "The Nimbus 9 measures temperature (±0.1°C) and relative humidity (±2%). Sensor readings are logged every 30 seconds and kept for 90 days in the Helioform cloud."),
    ("n9-warranty",
        "The Nimbus 9 carries a 3-year limited warranty covering manufacturing defects. Water damage and unauthorized firmware modifications void the warranty."),
    ("n9-consumption",
        "The Nimbus 9 draws 1.2W in normal operation and 0.3W in standby. Annual power consumption is roughly 9 kWh, comparable to a Wi-Fi router."),
    ("n8-battery",
        "The legacy Nimbus 8 runs on two AA batteries with a typical battery life of 18 months. A low-battery warning appears on the display two weeks before the batteries die."),
    ("n8-adapter",
        "Nimbus 8 owners can buy the optional PSU-8 power adapter to replace batteries entirely. The adapter plugs into the micro-USB service port and requires firmware 2.3 or later.")
];

// ponytail: 10 chunks need a List + cosine loop, not a vector store.
// (Microsoft.SemanticKernel.Connectors.InMemory 1.74.0-preview is also runtime-incompatible
// with the VectorData.Abstractions 10.x that SK 1.79/MEAI resolve to — TypeLoadException.)
var chunkEmbeddings = (await embeddingGenerator.GenerateAsync(chunks.Select(c => c.Text)))
    .Zip(chunks, (e, c) => (c.Id, c.Text, e.Vector)).ToList();

Console.WriteLine($"Indexed {chunks.Length} doc chunks about the Helioform Nimbus 9 (plus Nimbus 8 near-misses).\n");

// ----------------------------------------------
// 2. Retrieval as an AIFunction tool (logged, so every decision is visible)
// ----------------------------------------------

// ponytail: shared mutable "last results" instead of threading chunks through tool args;
// fine for a single-threaded demo, pass chunks explicitly if this ever goes concurrent.
var lastResults = new List<(string Id, string Text, double Score)>();

async Task<string> Search(string query, int topK = 3)
{
    Console.WriteLine($"  [tool] search(\"{query}\", topK: {topK})");
    lastResults.Clear();
    var queryVector = (await embeddingGenerator.GenerateAsync(query)).Vector;
    var sb = new StringBuilder();
    foreach (var (id, text, vector) in chunkEmbeddings
                 .OrderByDescending(c => TensorPrimitives.CosineSimilarity(c.Vector.Span, queryVector.Span))
                 .Take(topK))
    {
        var score = TensorPrimitives.CosineSimilarity(vector.Span, queryVector.Span);
        lastResults.Add((id, text, score));
        Console.WriteLine($"         -> {id} (score {score:F2})");
        sb.AppendLine($"[{id}] (score {score:F2}) {text}");
    }

    return sb.Length > 0 ? sb.ToString() : "No results found.";
}

// ----------------------------------------------
// 3. Grading as a tool: a separate grader agent with structured output
// ----------------------------------------------

var grader = new ChatClientAgent(
    Settings.ChatClient,
    name: "grader",
    instructions: """
                  You grade retrieved documentation chunks against a user question.
                  A chunk is relevant only if it is about the SAME product AND directly helps answer the question.
                  Chunks about a different product model (e.g. Nimbus 8 instead of Nimbus 9) are NOT relevant.
                  Sufficient means: the relevant chunks fully answer the question.
                  If insufficient, propose a rewritten search query using different vocabulary likely to match the docs.
                  """);

async Task<string> GradeResults(string question)
{
    Console.WriteLine($"  [tool] grade_results(\"{question}\")");
    var chunkList = string.Join("\n", lastResults.Select(r => $"[{r.Id}] {r.Text}"));
    var report = (await grader.RunAsync<GradeReport>(
        $"Question: {question}\n\nRetrieved chunks:\n{chunkList}")).Result;

    Console.WriteLine($"         -> {(report.Sufficient ? "SUFFICIENT" : "INSUFFICIENT")}, " +
                      $"relevant: [{string.Join(", ", report.RelevantChunkIds)}]" +
                      (report.Sufficient ? "" : $", suggested query: \"{report.RewrittenQuery}\""));

    return report.Sufficient
        ? $"SUFFICIENT. Answer using only these chunks: {string.Join(", ", report.RelevantChunkIds)}."
        : $"INSUFFICIENT. Discard irrelevant chunks and call search again with query: \"{report.RewrittenQuery}\".";
}

// ----------------------------------------------
// 4. The agentic RAG agent: retrieval loop driven by instructions + tools
// ----------------------------------------------

var agent = new ChatClientAgent(
    Settings.ChatClient,
    name: "AgenticRag",
    instructions: """
                  You answer questions about the Helioform Nimbus 9 smart thermostat using the documentation tools.

                  Follow this loop for every question:
                  1. If the question needs no product documentation (general knowledge, arithmetic), answer directly WITHOUT calling any tool.
                  2. Otherwise rewrite the question into a focused search query and call search(query, topK).
                  3. Call grade_results(question) to check whether the retrieved chunks actually answer the question.
                  4. If grading says INSUFFICIENT, call search again with the suggested query, then grade again.
                     Never call search more than 3 times per question.
                  5. Answer using ONLY chunks the grader marked relevant, citing their ids like [n9-pair].
                  If after 3 searches the docs still don't cover it, say so honestly.
                  """,
    tools:
    [
        AIFunctionFactory.Create(Search, "search",
            "Search the Nimbus product documentation. Returns chunks with ids and similarity scores."),
        AIFunctionFactory.Create(GradeResults, "grade_results",
            "Grade the most recently retrieved chunks for relevance to the user's question. Says whether they suffice and suggests a rewritten query if not.")
    ]);

// ----------------------------------------------
// 5. Three demo questions, three different paths through the loop
// ----------------------------------------------

string[] questions =
[
    "What is 15% of 240?", // no retrieval needed
    "How do I pair the Nimbus 9 with the mobile app?", // single retrieval, direct hit
    "Does the Nimbus 9 have a battery backup for power outages?" // Nimbus 8 near-misses outrank the answer -> grader must discard them (and may trigger rewrite + re-retrieve)
];

foreach (var question in questions)
{
    Console.WriteLine(new string('=', 70));
    Console.WriteLine($"User: {question}");
    var answer = await agent.RunAsync(question);
    Console.WriteLine($"Agent: {answer}\n");
}

// Structured output for the grading step (agent.RunAsync<T> idiom used across this repo)
public sealed record GradeReport(bool Sufficient, string[] RelevantChunkIds, string RewrittenQuery);
