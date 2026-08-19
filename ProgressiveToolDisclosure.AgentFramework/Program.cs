using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Progressive disclosure of tools: an agent with a large tool catalog does not carry
// every definition in every request. It starts with ONE meta-tool, search_tools, that
// searches an index of name+description pairs. Matches are bound for subsequent turns
// via an AIContextProvider (AIContext.Tools is merged per invocation), so the model
// only ever pays for the definitions it actually needs.
// Compare MCP.AgentFramework, which binds every discovered tool up front.

var catalog = BuildCatalog();
var provider = new LoadedToolsProvider();

var searchTools = AIFunctionFactory.Create((string query) =>
{
    var words = query.ToLowerInvariant().Split(' ', ',', ';');
    var matches = catalog
        .Where(f => words.Any(w => w.Length > 2 && $"{f.Name} {f.Description}".ToLowerInvariant().Contains(w)))
        .Take(3).ToList();
    if (matches.Count == 0)
        return "No matching tools. Try different keywords.";

    foreach (var match in matches.Where(m => !provider.Loaded.Contains(m)))
        provider.Loaded.Add(match);
    return "Found and loaded (callable from your NEXT turn, not this one): " +
           string.Join("; ", matches.Select(m => $"{m.Name} — {m.Description}"));
}, "search_tools", "Search the tool catalog by keywords. Matching tools become callable on the next turn.");

var meter = new ToolCountMeter(Settings.ChatClient);
var agent = new ChatClientAgent(meter, new ChatClientAgentOptions
{
    Name = "Concierge",
    ChatOptions = new ChatOptions
    {
        Instructions = "You are a concierge with access to a large tool catalog, but tools must be " +
                       "discovered first: use search_tools to find and load what a request needs. " +
                       "Loaded tools become callable on the following turn — until then, only confirm " +
                       "in one sentence what you loaded; never answer from your own knowledge. Be concise.",
        Tools = [searchTools]
    },
    AIContextProviders = [provider]
});

Console.WriteLine($"Tool catalog: {catalog.Count} tools — {string.Join(", ", catalog.Select(t => t.Name))}\n");

var session = await agent.CreateSessionAsync();

foreach (var input in new[]
         {
             "How many DKK is 250 EUR, and will it rain in Copenhagen tomorrow?",
             "Great — now answer both questions using those tools."
         })
{
    Console.WriteLine($"User: {input}");
    Console.WriteLine($"Agent: {await agent.RunAsync(input, session)}");
    Console.WriteLine($"  [tool definitions sent to the model: {meter.LastToolCount} of {catalog.Count + 1} available]\n");
}

Console.WriteLine($"Loaded on demand: {string.Join(", ", provider.Loaded.Select(t => t.Name))} — " +
                  $"the other {catalog.Count - provider.Loaded.Count} definitions never entered the context.");
return;

static List<AIFunction> BuildCatalog() =>
[
    AIFunctionFactory.Create((string city) => $"{city}: 17°C, light rain expected tomorrow.", "get_weather", "Weather forecast for a city."),
    AIFunctionFactory.Create((decimal amount, string from, string to) => $"{amount} {from} = {amount * 7.46m} {to} (rate 7.46).", "convert_currency", "Convert an amount between currencies."),
    AIFunctionFactory.Create((string symbol) => $"{symbol}: 412.55 USD, +1.2% today.", "get_stock_price", "Latest stock price for a ticker symbol."),
    AIFunctionFactory.Create((string text, string language) => $"[{language}] {text}", "translate_text", "Translate text into a target language."),
    AIFunctionFactory.Create((string city) => $"Top sights in {city}: Nyhavn, Tivoli, The Little Mermaid.", "find_attractions", "Tourist attractions in a city."),
    AIFunctionFactory.Create((string date) => $"No calendar conflicts on {date}.", "check_calendar", "Check calendar availability on a date."),
    AIFunctionFactory.Create((string title, string date) => $"Meeting '{title}' booked for {date}.", "book_meeting", "Book a meeting on the calendar."),
    AIFunctionFactory.Create((string query) => $"Found 3 restaurants matching '{query}'.", "find_restaurants", "Search restaurants by cuisine or name."),
    AIFunctionFactory.Create((string from, string to) => $"Train {from}->{to}: 4h 32m, from 39 EUR.", "find_trains", "Train connections between two cities."),
    AIFunctionFactory.Create((string tracking) => $"Parcel {tracking}: out for delivery.", "track_parcel", "Track a parcel by tracking number."),
    AIFunctionFactory.Create((string city) => $"Air quality in {city}: AQI 31 (good).", "get_air_quality", "Air quality index for a city."),
    AIFunctionFactory.Create((string isbn) => $"ISBN {isbn}: 'Designing Agents', in stock.", "lookup_book", "Look up a book by ISBN."),
    AIFunctionFactory.Create((string domain) => $"{domain} is available for 12 EUR/year.", "check_domain", "Check domain name availability."),
    AIFunctionFactory.Create((string text) => $"Sentiment: positive (0.87).", "analyze_sentiment", "Sentiment analysis for a text."),
    AIFunctionFactory.Create((string city) => $"Sunrise 05:41, sunset 20:33 in {city}.", "get_sun_times", "Sunrise and sunset times for a city.")
];

/// <summary>
/// Supplies the on-demand-loaded tools for each invocation. AIContext.Tools is merged
/// with the agent's base tools (here: just search_tools), so the model's tool list is
/// always "search_tools + whatever has been discovered so far".
/// </summary>
internal sealed class LoadedToolsProvider : AIContextProvider
{
    public List<AIFunction> Loaded { get; } = [];

    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new AIContext { Tools = [.. Loaded] });
}

/// <summary>Delegating IChatClient that records how many tool definitions each model call carried.</summary>
internal sealed class ToolCountMeter(IChatClient inner) : DelegatingChatClient(inner)
{
    public int LastToolCount { get; private set; }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        LastToolCount = options?.Tools?.Count ?? 0;
        return base.GetResponseAsync(messages, options, cancellationToken);
    }
}
