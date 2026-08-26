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

            // A valid token, just from the wrong live run, must not unlock this one.
            Assert.Null(RunSession.TryGet(a.Id, b.Token));

            a.Cancel();

            Assert.True(a.IsCancelled);
            Assert.False(b.IsCancelled);
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

/// 2.5a's whole point: a sample launched from Explorer gets ONLY what it needs, never Explorer's
/// own environment (which holds the operator's credentials for every other tool). Deleting
/// `environment.Clear()` from RunSession leaves every other test in this suite green while every
/// child sample silently regains the lot, so it gets its own assertion here. Goes through
/// ApplyChildEnvironment, not Start - no `dotnet run` is spawned.
public class RunSessionEnvironmentTests
{
    const string Secret = "AGENTIC_PATTERNS_TEST_UNRELATED_SECRET";
    const string Allowed = "AzureOpenAi__ApiKey";

    static void WithVariable(string name, string? value, Action body)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try { body(); }
        finally { Environment.SetEnvironmentVariable(name, previous); }
    }

    [Fact]
    public void The_child_gets_the_allowlist_and_dotnet_run_essentials_but_not_the_rest_of_the_host()
    {
        WithVariable(Secret, "leaked", () => WithVariable(Allowed, "sk-test", () =>
        {
            // A real ProcessStartInfo, which pre-populates Environment from this process - the
            // exact thing Clear() has to undo.
            var info = new System.Diagnostics.ProcessStartInfo("dotnet");
            Assert.True(info.Environment.ContainsKey(Secret), "precondition: the host variable is inherited");

            RunSession.ApplyChildEnvironment(info.Environment, new PatternProject("AgentFramework", "Some.Sample"));

            Assert.False(info.Environment.ContainsKey(Secret));
            Assert.Equal("sk-test", info.Environment[Allowed]);
            Assert.True(info.Environment.ContainsKey("PATH"), "`dotnet run` needs PATH");
        }));
    }

    [Fact]
    public void A_variable_outside_the_projects_own_allowlist_is_not_forwarded()
    {
        WithVariable(Secret, "leaked", () =>
        {
            var info = new System.Diagnostics.ProcessStartInfo("dotnet");
            var project = new PatternProject("AgentFramework", "Some.Sample")
            {
                EnvironmentAllowlist = [Allowed]
            };

            RunSession.ApplyChildEnvironment(info.Environment, project);

            Assert.False(info.Environment.ContainsKey(Secret));
        });
    }

    [Fact]
    public void An_allowlisted_variable_that_is_unset_is_simply_absent_not_empty()
    {
        WithVariable(Secret, null, () =>
        {
            var info = new System.Diagnostics.ProcessStartInfo("dotnet");
            var project = new PatternProject("AgentFramework", "Some.Sample") { EnvironmentAllowlist = [Secret] };

            RunSession.ApplyChildEnvironment(info.Environment, project);

            Assert.False(info.Environment.ContainsKey(Secret));
        });
    }
}
