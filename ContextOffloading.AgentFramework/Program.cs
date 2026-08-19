using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Context offloading: bulky tool results are written to files and replaced in the
// conversation by a short stub (count + preview + file path). Unlike compaction
// (ContextCompaction, which is lossy by design), offloaded context is RECOVERABLE —
// the agent holds a read_result tool and reads the full data back the moment a
// question actually needs it. SelfNote goes the other direction (adds notes);
// this pattern evicts payloads without losing them.

var resultsDir = Directory.CreateDirectory(
    Path.Combine(Path.GetTempPath(), "context-offloading", Guid.NewGuid().ToString("N"))).FullName;
Console.WriteLine($"Offload store: {resultsDir}\n");

var searchFlights = new OffloadingFunction(
    AIFunctionFactory.Create(SearchFlights, "search_flights",
        "Search flights between two airports; returns all matching flights as JSON."),
    resultsDir);

var readResult = AIFunctionFactory.Create((string fileName) =>
{
    var path = Path.Combine(resultsDir, Path.GetFileName(fileName)); // no path escapes
    return File.Exists(path) ? File.ReadAllText(path) : $"No such offloaded file: {fileName}";
}, "read_result", "Read back the full content of an offloaded result file by its file name.");

var agent = new ChatClientAgent(Settings.ChatClient, new ChatClientAgentOptions
{
    Name = "TravelAgent",
    ChatOptions = new ChatOptions
    {
        Instructions = "You are a travel assistant. Large tool results are offloaded to files and you " +
                       "only see a stub with a preview. Answer from the stub when it suffices; when a " +
                       "question needs details beyond the preview, use read_result to load the full data. " +
                       "Answer strictly from the data — never invent details. Be concise.",
        Tools = [searchFlights, readResult]
    },
    ChatHistoryProvider = new InMemoryChatHistoryProvider()
});

var session = await agent.CreateSessionAsync();

foreach (var input in new[]
         {
             "Find me flights from CPH to SFO on October 12.",
             "How many options are there roughly, and who flies the route?",
             "Which flight under $900 has wifi and the shortest duration?" // needs the full data back
         })
{
    Console.WriteLine($"User: {input}");
    Console.WriteLine($"Agent: {await agent.RunAsync(input, session)}\n");
}

var historyChars = session.TryGetInMemoryChatHistory(out var history)
    ? history!.Sum(m => m.Text.Length) : 0;
var diskBytes = new DirectoryInfo(resultsDir).GetFiles().Sum(f => f.Length);
Console.WriteLine($"---- In-context history: {historyChars:N0} chars | offloaded to disk (fully recoverable): {diskBytes:N0} bytes ----");
return;

// Deterministic fake flight inventory — deliberately bulky (~40 flights of JSON).
static string SearchFlights(string from, string to)
{
    var flights = Enumerable.Range(0, 40).Select(i => new
    {
        FlightNumber = $"{new[] { "SK", "LH", "AF", "UA", "BA" }[i % 5]}{100 + i * 7}",
        Airline = new[] { "SAS", "Lufthansa", "Air France", "United", "British Airways" }[i % 5],
        Depart = $"2026-10-12T{6 + i % 14:00}:{i % 4 * 15:00}",
        DurationMinutes = 660 + i * 13 % 300,
        Stops = i % 3,
        PriceUsd = 520 + i * 37 % 700,
        Wifi = i % 2 == 0,
        Aircraft = i % 2 == 0 ? "A350-900" : "787-9",
        Baggage = "1 carry-on included, first checked bag 75 USD",
        Fare = i % 3 == 0 ? "Economy Flex (changes allowed)" : "Economy Light (no changes)"
    });
    return JsonSerializer.Serialize(new { Route = $"{from}->{to}", Flights = flights },
        new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>
/// Wraps any AIFunction: results over the threshold are written to a numbered file and
/// replaced by a stub with a preview and the file name. The stub — not the payload — is
/// what lands in the conversation history and every later model call.
/// </summary>
internal sealed class OffloadingFunction(AIFunction inner, string resultsDir, int threshold = 600)
    : DelegatingAIFunction(inner)
{
    private int _counter;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var result = await base.InvokeCoreAsync(arguments, cancellationToken);
        var text = result as string ?? JsonSerializer.Serialize(result);
        if (text.Length <= threshold)
            return result;

        var fileName = $"result-{++_counter}.json";
        await File.WriteAllTextAsync(Path.Combine(resultsDir, fileName), text, cancellationToken);
        var stub = $"[offloaded] Full result ({text.Length:N0} chars) written to {fileName} — " +
                   $"recover it with read_result(\"{fileName}\") when details are needed.\n" +
                   $"Preview (first 400 chars):\n{text[..400]}...";
        Console.WriteLine($"  [{Name}: {text.Length:N0} chars -> {fileName}, {stub.Length} char stub kept in context]");
        return stub;
    }
}
