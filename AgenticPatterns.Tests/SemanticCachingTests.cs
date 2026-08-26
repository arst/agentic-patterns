using Microsoft.Extensions.AI;
using SemanticCaching.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

public class SemanticCachingTests
{
    // Identical vectors -> cosine similarity 1.0, guaranteed over the 0.9 threshold.
    // The eviction test's three questions get their own mutually orthogonal vectors below
    // so they don't all collide on the shared default vector.
    private static readonly Dictionary<string, float[]> Vectors = new()
    {
        ["What is the capital of France?"] = [1f, 0f, 0f],
        ["Tell me France's capital city"] = [1f, 0f, 0f],
        ["What is the tallest mountain?"] = [0f, 1f, 0f],
        ["first question about refunds"] = [1f, 0f, 0f],
        ["second question about shipping"] = [0f, 1f, 0f],
        ["third question about warranties"] = [0f, 0f, 1f]
    };

    private static ChatResponse Reply(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static SemanticCachingChatClient MakeCache(ScriptedChatClient inner, CacheNamespace? ns = null) =>
        new(inner, new FixedEmbeddingGenerator(Vectors), ns ?? Ns(), TimeSpan.FromMinutes(10), 100);

    private static CacheNamespace Ns(string tenant = "tenant-a", string tools = "tools-v1") =>
        new(tenant, "principal-hash", "system-hash", tools, "gpt-x", "data-rev-1");

    private static SemanticCachingChatClient Client(CacheNamespace ns, TimeSpan? lifetime = null, int max = 100) =>
        new(new ScriptedChatClient(Reply("cached answer")), new FixedEmbeddingGenerator(Vectors), ns,
            lifetime ?? TimeSpan.FromMinutes(10), max);

    private static ChatMessage[] Ask(string text) => [new ChatMessage(ChatRole.User, text)];

    [Fact]
    public async Task SimilarQuery_SameContext_IsAHit_AndReturnsACopy()
    {
        var inner = new ScriptedChatClient(Reply("Paris"), Reply("should never be served"));
        var cache = MakeCache(inner);

        List<ChatMessage> First() => [new(ChatRole.System, "Be terse."), new(ChatRole.User, "What is the capital of France?")];
        List<ChatMessage> Second() => [new(ChatRole.System, "Be terse."), new(ChatRole.User, "Tell me France's capital city")];

        var original = await cache.GetResponseAsync(First());
        var hit = await cache.GetResponseAsync(Second());

        Assert.Equal(1, inner.Calls); // second answer came from the cache
        Assert.Equal(1, cache.Hits);
        Assert.Equal("Paris", hit.Text);
        Assert.NotSame(original, hit); // a copy, never the shared cached instance
    }

    [Fact]
    public async Task SameQuery_DifferentSystemPrompt_IsAMiss()
    {
        var inner = new ScriptedChatClient(Reply("Paris (terse)"), Reply("Paris (verbose)"));
        var cache = MakeCache(inner);

        await cache.GetResponseAsync(
            [new(ChatRole.System, "Be terse."), new(ChatRole.User, "What is the capital of France?")]);
        await cache.GetResponseAsync(
            [new(ChatRole.System, "Answer at length."), new(ChatRole.User, "What is the capital of France?")]);

        Assert.Equal(2, inner.Calls); // different context must never reuse the answer
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public async Task SameQuery_DifferentConversationHistory_IsAMiss()
    {
        var inner = new ScriptedChatClient(Reply("contextual answer"), Reply("fresh answer"));
        var cache = MakeCache(inner);

        await cache.GetResponseAsync(
        [
            new(ChatRole.User, "Earlier question"),
            new(ChatRole.Assistant, "Earlier answer"),
            new(ChatRole.User, "What is the capital of France?")
        ]);
        await cache.GetResponseAsync([new(ChatRole.User, "What is the capital of France?")]);

        Assert.Equal(2, inner.Calls); // different history must never share an answer
    }

    [Fact]
    public async Task DifferentQuery_SameContext_IsAMiss()
    {
        var inner = new ScriptedChatClient(Reply("Paris"), Reply("Mount Everest"));
        var cache = MakeCache(inner);

        await cache.GetResponseAsync([new(ChatRole.User, "What is the capital of France?")]);
        var second = await cache.GetResponseAsync([new(ChatRole.User, "What is the tallest mountain?")]);

        Assert.Equal(2, inner.Calls);
        Assert.Equal("Mount Everest", second.Text);
    }

    [Fact]
    public async Task DifferentTenantsNeverShareACachedAnswer()
    {
        var a = Client(Ns(tenant: "tenant-a"));
        var b = Client(Ns(tenant: "tenant-b"));
        await a.GetResponseAsync(Ask("what is our refund window?"));
        await b.GetResponseAsync(Ask("what is our refund window?"));
        Assert.Equal(0, a.Hits);
        Assert.Equal(0, b.Hits);
    }

    [Fact]
    public async Task ADifferentToolSchemaIsADifferentPartition()
    {
        var client = Client(Ns(tools: "tools-v1"));
        await client.GetResponseAsync(Ask("what is our refund window?"));
        var upgraded = Client(Ns(tools: "tools-v2"));
        await upgraded.GetResponseAsync(Ask("what is our refund window?"));
        Assert.Equal(0, upgraded.Hits);
        Assert.Equal(1, upgraded.Misses);
    }

    [Fact]
    public void PartitionKeyDiffersWhenOnlyTenantIdDiffers()
    {
        var messages = Ask("what is our refund window?");
        var keyA = SemanticCachingChatClient.PartitionKey(Ns(tenant: "tenant-a"), messages, null);
        var keyB = SemanticCachingChatClient.PartitionKey(Ns(tenant: "tenant-b"), messages, null);
        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void PartitionKeyDiffersWhenOnlyToolSchemaHashDiffers()
    {
        var messages = Ask("what is our refund window?");
        var keyA = SemanticCachingChatClient.PartitionKey(Ns(tools: "tools-v1"), messages, null);
        var keyB = SemanticCachingChatClient.PartitionKey(Ns(tools: "tools-v2"), messages, null);
        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void PartitionKeyCoversFunctionCallsAndResultsNotJustText()
    {
        // Neither message below carries TextContent, so the old `.Text`-based digest saw
        // both histories as identical ("Assistant:" / "Tool:" with nothing to compare) —
        // a tool call with a different argument, or a different result, must not collide.
        ChatMessage[] History(string argument, string result) =>
        [
            new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "Lookup", new Dictionary<string, object?> { ["id"] = argument })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", result)]),
            new ChatMessage(ChatRole.User, "what is our refund window?")
        ];

        var keyA = SemanticCachingChatClient.PartitionKey(Ns(), History("acct-1", "active"), null);
        var keyB = SemanticCachingChatClient.PartitionKey(Ns(), History("acct-2", "suspended"), null);

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public async Task ExpiredEntriesAreNotServed()
    {
        var client = Client(Ns(), lifetime: TimeSpan.Zero);
        await client.GetResponseAsync(Ask("what is our refund window?"));
        await client.GetResponseAsync(Ask("what is our refund window?"));
        Assert.Equal(0, client.Hits);
        Assert.Equal(2, client.Misses);
    }

    [Fact]
    public async Task ThePartitionIsBoundedAndEvictsOldest()
    {
        var client = Client(Ns(), max: 2);
        await client.GetResponseAsync(Ask("first question about refunds"));
        await client.GetResponseAsync(Ask("second question about shipping"));
        await client.GetResponseAsync(Ask("third question about warranties"));
        await client.GetResponseAsync(Ask("first question about refunds"));   // evicted
        Assert.Equal(0, client.Hits);
        Assert.Equal(4, client.Misses);
    }

    [Fact]
    public async Task ConcurrentCallersDoNotCorruptTheCache()
    {
        var client = Client(Ns());
        await Task.WhenAll(Enumerable.Range(0, 500)
            .Select(_ => client.GetResponseAsync(Ask("what is our refund window?"))));
        Assert.Equal(500, client.Hits + client.Misses);
    }
}
