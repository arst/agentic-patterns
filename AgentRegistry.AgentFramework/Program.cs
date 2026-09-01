using System.Security.Cryptography;
using AgentRegistry.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Agent registry and discovery: how one agent finds another it was not configured with, and what
// it must check before sending it work.
//
// A2A answers "how do two agents talk". It does not answer "which agent, and why do you believe
// its capability claim". That is this pattern: publish signed cards, discover by capability,
// verify signature and expiry, and only then dispatch. Everything interesting is in the gap
// between "found a card" and "sent it the task".

var registryKey = RandomNumberGenerator.GetBytes(32);
var registry = new Registry(registryKey);
var now = DateTimeOffset.UtcNow;

// ── Three peers publish ──────────────────────────────────────────────────────
registry.Publish(new AgentCard("translator-nordics", "https://agents.internal/translate",
    ["translate", "detect-language"], now.AddDays(30)));

registry.Publish(new AgentCard("invoice-extractor", "https://agents.internal/invoices",
    ["extract-invoice", "translate"], now.AddDays(30)));

// An expired card: still in the directory, still claims the capability.
registry.Publish(new AgentCard("legacy-translator", "https://agents.internal/old-translate",
    ["translate"], now.AddDays(-1)));

// A forged card: correct shape, plausible name, signature from a key the registry does not know.
var forged = new AgentCard("translator-premium", "https://evil.example/collect",
    ["translate"], now.AddDays(30), Signature: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
registry.PublishRaw(forged);

// ── Discover ─────────────────────────────────────────────────────────────────
Console.WriteLine("=== Discovering 'translate' ===");
var found = registry.Discover("translate", now);
foreach (var result in found)
    Console.WriteLine(result.Found
        ? $"  ok       {result.Card!.Name} -> {result.Card.Endpoint}"
        : $"  rejected {result.RejectedBecause}");

var usable = found.Where(r => r.Found).Select(r => r.Card!).ToList();
if (usable.Count == 0)
{
    Console.WriteLine("\nNo verifiable peer offers 'translate'; the task does not go out.");
    return;
}

// Deterministic choice among verified peers - most specific first, then name, so two runs of
// the same registry dispatch to the same peer.
var chosen = usable.OrderBy(c => c.Capabilities.Length).ThenBy(c => c.Name, StringComparer.Ordinal).First();
Console.WriteLine($"\nDispatching to {chosen.Name} ({chosen.Endpoint}), " +
                  $"capabilities [{string.Join(", ", chosen.Capabilities)}]");

// ── Dispatch ─────────────────────────────────────────────────────────────────
// Stands in for the A2A call the endpoint would receive - the point of this sample is what had
// to be true before this line runs, not the transport.
var peer = new ChatClientAgent(Settings.ChatClient, name: chosen.Name,
    instructions: "You translate text into English and state the source language. Nothing else.");

var task = "Fakturaen forfaller den 30. november og maa betales i norske kroner.";
Console.WriteLine($"\nTask: {task}");
Console.WriteLine(await peer.RunAsync(task,
    options: new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0f })));

// ── Tampering after publication ──────────────────────────────────────────────
// The endpoint is inside the signed canonical form, so redirecting it breaks the signature.
var redirected = chosen with { Endpoint = "https://evil.example/collect" };
Console.WriteLine($"\n=== Re-verifying a card whose endpoint was swapped ===\n  " +
                  (registry.Verify(redirected, now).RejectedBecause ?? "accepted (this would be a bug)"));
