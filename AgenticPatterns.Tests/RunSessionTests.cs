using PatternExplorer;
using Xunit;

namespace AgenticPatterns.Tests;

/// Exercises RunSession's registry, lookup and channel behavior directly - never through
/// Start/RunAsync, which would spawn a real `dotnet run` child process.
public class RunSessionTests
{
    [Fact]
    public void Two_sessions_are_independently_retrievable_and_isolated()
    {
        var a = new RunSession();
        var b = new RunSession();
        RunSession.Register(a);
        RunSession.Register(b);
        try
        {
            Assert.Same(a, RunSession.TryGet(a.Id, a.Token));
            Assert.Same(b, RunSession.TryGet(b.Id, b.Token));

            a.Cancel();

            Assert.True(a.CancellationToken.IsCancellationRequested);
            Assert.False(b.CancellationToken.IsCancellationRequested);
        }
        finally
        {
            RunSession.Unregister(a);
            RunSession.Unregister(b);
        }
    }

    [Fact]
    public void TryGet_returns_null_for_wrong_token_or_wrong_id()
    {
        var session = new RunSession();
        RunSession.Register(session);
        try
        {
            Assert.Null(RunSession.TryGet(session.Id, "wrong-token"));
            Assert.Null(RunSession.TryGet("wrong-id", session.Token));
        }
        finally
        {
            RunSession.Unregister(session);
        }
    }

    [Fact]
    public void Register_throws_once_the_live_run_cap_is_reached()
    {
        var sessions = Enumerable.Range(0, 8).Select(_ => new RunSession()).ToList();
        foreach (var s in sessions) RunSession.Register(s);
        try
        {
            var extra = new RunSession();
            Assert.Throws<InvalidOperationException>(() => RunSession.Register(extra));
        }
        finally
        {
            foreach (var s in sessions) RunSession.Unregister(s);
        }
    }

    [Fact]
    public void Bounded_channel_drops_oldest_instead_of_growing_or_blocking()
    {
        var session = new RunSession();

        for (var i = 0; i < 5000; i++)
            Assert.True(session.Writer.TryWrite(new Chunk("out", i.ToString())));

        var received = new List<Chunk>();
        while (session.Reader.TryRead(out var chunk)) received.Add(chunk);

        Assert.True(received.Count <= 4096);
        Assert.DoesNotContain(received, c => c.T == "0");
        Assert.Contains(received, c => c.T == "4999");
    }
}
