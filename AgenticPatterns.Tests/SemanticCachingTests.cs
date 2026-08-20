using Microsoft.Extensions.AI;
using SemanticCaching.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

public class SemanticCachingTests
{
    // Identical vectors -> cosine similarity 1.0, guaranteed over the 0.9 threshold
    private static readonly Dictionary<string, float[]> Vectors = new()
    {
        ["What is the capital of France?"] = [1f, 0f, 0f],
        ["Tell me France's capital city"] = [1f, 0f, 0f],
        ["What is the tallest mountain?"] = [0f, 1f, 0f]
    };

    private static ChatResponse Reply(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static SemanticCachingChatClient MakeCache(ScriptedChatClient inner) =>
        new(inner, new FixedEmbeddingGenerator(Vectors));

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
    public async Task DifferentQuery_SameContext_IsAMiss()
    {
        var inner = new ScriptedChatClient(Reply("Paris"), Reply("Mount Everest"));
        var cache = MakeCache(inner);

        await cache.GetResponseAsync([new(ChatRole.User, "What is the capital of France?")]);
        var second = await cache.GetResponseAsync([new(ChatRole.User, "What is the tallest mountain?")]);

        Assert.Equal(2, inner.Calls);
        Assert.Equal("Mount Everest", second.Text);
    }
}
