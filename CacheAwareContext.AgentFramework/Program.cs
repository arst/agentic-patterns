using Microsoft.Extensions.AI;
using Shared;

// Cache-aware context layout: providers cache prompt PREFIXES, so an agent whose message
// list is a stable prefix + append-only history gets most of its input tokens served
// from cache — and a single volatile byte at the TOP of the prompt invalidates all of it.
// Three layouts run the same 4-turn conversation over a large system prompt:
//   A) stable prefix — cache hits from turn 2 on
//   B) timestamp at the TOP of the system prompt — every turn is a full cache miss
//   C) the same timestamp moved into the LATEST user message — cache hits again
// This is about the provider's prompt cache (cached input tokens), not response caching —
// SemanticCaching covers that. Note: prefix caching needs a >=1024-token prefix and a
// model/deployment that supports it; on unsupported deployments all counts read 0.

var policy = BuildSystemPrompt();
Console.WriteLine($"System prompt: {policy.Length:N0} chars (large on purpose — caching needs a >=1024-token prefix)\n");

// Provider caches live ~5-10 minutes across requests, so a rerun of a fully deterministic
// demo would hit the PREVIOUS run's cache and muddy the comparison. A per-run session id
// at the top of the prompt — constant within the run, unique across runs — isolates it.
var runId = Guid.NewGuid().ToString("N")[..8];

string[] questions =
[
    "What does the Standing Desk Pro cost, and how long is its warranty?",
    "Can I return the Ergo Chair Elite after five weeks?",
    "Which products ship outside the EU?",
    "Summarize the cheapest and the most expensive product in the catalog."
];

await RunLayout("A) Stable prefix (fixed system prompt, append-only history)",
    turn => policy, timestampInUserMessage: false);
await RunLayout("B) Volatile prefix (timestamp at the TOP of the system prompt)",
    turn => $"Current time: 2026-08-19T09:{turn:00}:00Z\n\n{policy}", timestampInUserMessage: false);
await RunLayout("C) Volatile data moved to the END (timestamp in the latest user message)",
    turn => policy, timestampInUserMessage: true);

Console.WriteLine("Same conversation, same tokens paid for the first turn — layout alone decides " +
                  "whether turns 2-4 hit the cache.");
return;

async Task RunLayout(string label, Func<int, string> systemPrompt, bool timestampInUserMessage)
{
    Console.WriteLine($"---- {label} ----");
    var history = new List<ChatMessage>();

    for (var turn = 0; turn < questions.Length; turn++)
    {
        var question = timestampInUserMessage
            ? $"{questions[turn]}\n(Current time: 2026-08-19T09:{turn:00}:00Z)"
            : questions[turn];

        List<ChatMessage> messages =
            [new(ChatRole.System, $"Session: {runId}-{label[0]}\n{systemPrompt(turn)}"), .. history, new(ChatRole.User, question)];

        var response = await Settings.ChatClient.GetResponseAsync(messages,
            new ChatOptions { MaxOutputTokens = 120 });

        history.Add(new ChatMessage(ChatRole.User, question));
        history.AddRange(response.Messages);

        Console.WriteLine($"  Turn {turn + 1}: input tokens {response.Usage?.InputTokenCount,5} | " +
                          $"served from cache {response.Usage?.CachedInputTokenCount ?? 0,5}");
    }

    Console.WriteLine();
}

// A support-agent system prompt bulky enough to cross the 1024-token caching threshold:
// return/warranty policy plus a 30-product catalog.
static string BuildSystemPrompt()
{
    string[] names = ["Standing Desk Pro", "Ergo Chair Elite", "Monitor Arm Duo", "Laptop Stand Air", "Desk Mat XL", "Cable Tray Slim"];
    var catalog = string.Join("\n", Enumerable.Range(0, 30).Select(i =>
        $"- SKU-{i:00} '{names[i % names.Length]} {i / names.Length + 1}': price {149 + i * 37} EUR, " +
        $"warranty {i % 3 + 1} years, return window {(i % 2 == 0 ? 30 : 14)} days, " +
        $"ships {(i % 5 == 0 ? "worldwide" : "EU only")}, in stock: {(i % 4 != 3 ? "yes" : "no")}."));

    return $"""
        You are the support assistant for NordicDesk, an office-furniture web shop.
        Answer strictly from the policy and catalog below. Be concise and factual.

        ## Returns
        Items may be returned within their per-product return window if unused and in
        original packaging. Refunds are issued to the original payment method within 10
        business days. Return shipping is free within the EU; customers outside the EU
        pay return shipping. Assembled furniture can only be returned if disassembled
        and repacked. Clearance items are final sale.

        ## Warranty
        Warranty periods are per product (see catalog). Warranty covers manufacturing
        defects, motor failures on powered desks, and gas-lift failures on chairs. It
        does not cover normal wear, misuse, or damage from third-party modifications.
        Claims require the order number and photos of the defect.

        ## Shipping
        EU orders ship free above 100 EUR, otherwise 9 EUR flat. Worldwide-eligible
        products ship at cost with duties paid by the recipient. Delivery estimates:
        EU 2-5 business days, worldwide 7-21 business days.

        ## Catalog
        {catalog}
        """;
}
