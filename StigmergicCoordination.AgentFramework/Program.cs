using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Shared;
using Shared.Sandbox;
using StigmergicCoordination.AgentFramework;

// Stigmergic coordination: N workers build components of one system WITHOUT exchanging
// a single message. All coordination flows through the shared environment — a workspace
// directory, compiler-enforced C# contracts, and a build gate. The orchestrator only
// launches workers and runs the gate; it never relays information between agents.
// Contrast MultiAgentCollaboration, where the same domain task is coordinated by dialogue.
//
// The gate compiles model-written files, which is untrusted code — build tasks, source
// generators, and MSBuild targets all run during a build. It runs inside the same
// constrained-execution boundary CodeAct uses (see BuildGate.cs) and FAILS CLOSED when no
// container runtime is available, unless the same double opt-in CodeAct offers is set.

// Fail closed BEFORE creating a workspace or spending a single model call: no container
// runtime and no explicit double opt-in means the sample refuses to compile anything.
var useSandbox = SandboxRunner.IsAvailable("docker");
if (!useSandbox && !BuildGate.IsUnsafeHostBuildRequested())
{
    Console.Error.WriteLine(BuildGate.FailClosedMessage);
    return 1;
}

// C2: world-readable so the sandbox's non-root uid can read the bind mount, regardless of
// the operator's umask - see BuildGate.CreateWorkspaceDirectory.
var workspace = BuildGate.CreateWorkspaceDirectory(
    Path.Combine(Path.GetTempPath(), "stigmergy", Guid.NewGuid().ToString("N")));
Console.WriteLine($"Workspace: {workspace}\n");

// Minor: a plain `return` inside the round loop below cannot outrun Ctrl-C, so the SIGINT
// handler deletes the workspace directly rather than relying on the try/finally to run.
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ =>
{
    try { Directory.Delete(workspace, recursive: true); }
    catch (IOException) { } catch (UnauthorizedAccessException) { }
});

// ---- The environment: contracts + integration gate, written by the HOST ----

const string Contracts =
    """
    namespace Campaign;

    public record ProductSpec(string Name, string Category, string Audience);
    public record Slogan(string Text, string Justification);
    public record PricingTier(string Name, decimal MonthlyPriceEur, string Pitch);
    public record CampaignBrief(string Headline, Slogan Slogan, PricingTier[] Tiers, string Summary);

    public interface ISloganModule
    {
        Slogan CreateSlogan(ProductSpec spec);
    }

    public interface IPricingModule
    {
        PricingTier[] GetTiers(ProductSpec spec);
    }

    public interface IBriefAssembler
    {
        CampaignBrief Assemble(ProductSpec spec, Slogan slogan, PricingTier[] tiers);
    }
    """;

// Compile-time contract test: proves each agreed class name exists and implements its
// interface. It is never executed — the compiler IS the integration test, so no
// model-written code ever runs (see the repository's untrusted-execution rule).
const string Gate =
    """
    namespace Campaign;

    public static class IntegrationGate
    {
        public static (ISloganModule, IPricingModule, IBriefAssembler) Wire() =>
            (new SloganModule(), new PricingModule(), new BriefAssembler());
    }
    """;

await BuildGate.WriteWorldReadableAsync(Path.Combine(workspace, "Contracts.cs"), Contracts, CancellationToken.None);
await BuildGate.WriteWorldReadableAsync(Path.Combine(workspace, "IntegrationGate.cs"), Gate, CancellationToken.None);
await BuildGate.WriteWorldReadableAsync(Path.Combine(workspace, "Campaign.csproj"),
    """
    <Project Sdk="Microsoft.NET.Sdk">
        <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
        </PropertyGroup>
    </Project>
    """, CancellationToken.None);

// ---- The workers: one file each, briefed from the environment, never from each other ----

const string CommonRules =
    """
    Output ONLY the complete C# source for your one file — no markdown fences, no prose.
    Use file-scoped `namespace Campaign;`. Do NOT redeclare any contract type or interface;
    your file compiles alongside the shared Contracts.cs.
    """;

// The Pricing worker's brief is deliberately STALE — the kind of spec drift dialogue
// coordination only discovers at integration time. The build gate catches it in round 1
// and the compiler error (plus the real contract) re-grounds the worker in round 2.
(string File, string Role, string Brief)[] workers =
[
    ("SloganModule.cs", "SloganWriter",
        $"You write `public sealed class SloganModule : ISloganModule`. Create a catchy, concise slogan with a one-line justification.\n\nThe shared contract file Contracts.cs:\n{Contracts}\n\n{CommonRules}"),
    ("PricingModule.cs", "PricingAnalyst",
        "You write `public sealed class PricingModule : IPricingModule`, returning three sensible subscription tiers. " +
        "The interface you must implement is: `public interface IPricingModule { IReadOnlyList<decimal> GetPrices(); }`. " +
        "Shared record types like ProductSpec already exist in the project.\n\n" + CommonRules),
    ("BriefAssembler.cs", "BriefWriter",
        $"You write `public sealed class BriefAssembler : IBriefAssembler`. Compose a CampaignBrief from the inputs: a headline, the slogan, the tiers, and a 2-3 sentence summary referencing them.\n\nThe shared contract file Contracts.cs:\n{Contracts}\n\n{CommonRules}"),
];

const string TaskBrief = "Product: an eco-friendly electric vehicle subscription called 'Verdra'. Category: mobility. Audience: urban professionals.";

async Task ProduceAsync((string File, string Role, string Brief) worker, string extraContext)
{
    var agent = new ChatClientAgent(Settings.ChatClient, worker.Brief, worker.Role);
    var code = (await agent.RunAsync($"{TaskBrief}\n{extraContext}")).Text.Trim();
    code = Regex.Replace(code, @"^```\w*\n|\n?```$", ""); // strip fences if the model adds them anyway
    await BuildGate.WriteWorldReadableAsync(Path.Combine(workspace, worker.File), code, CancellationToken.None);
    Console.WriteLine($"[worker] {worker.Role} -> {worker.File} ({code.Split('\n').Length} lines, no messages to other workers)");
}

// ---- The mechanical gate: BuildGate.RunAsync, sandboxed unless the opt-in fallback fired ----

try
{
    await Task.WhenAll(workers.Select(w => ProduceAsync(w, "")));

    for (var round = 1; round <= 3; round++)
    {
        Console.WriteLine($"\n=== Build gate: round {round} ===");
        var errors = await BuildGate.RunAsync(workspace, useSandbox, CancellationToken.None);
        if (errors.Count == 0)
        {
            Console.WriteLine("PASSED — every component satisfies the shared contracts.");
            Console.WriteLine("\n---- Files in the shared environment ----");
            foreach (var f in Directory.GetFiles(workspace, "*.cs").Order())
                Console.WriteLine($"\n>>> {Path.GetFileName(f)}\n{File.ReadAllText(f).Trim()}");
            Console.WriteLine("\nMessages exchanged between workers: 0. The workspace did all the talking.");
            return 0;
        }

        foreach (var error in errors) Console.WriteLine($"  {error}");

        // Errors route by file name to the worker that owns the file — the trace in the
        // environment is the only feedback channel, and it carries the REAL contract with it.
        foreach (var group in errors.GroupBy(e => workers.FirstOrDefault(w => e.Contains(w.File)).File).Where(g => g.Key is not null))
        {
            var worker = workers.First(w => w.File == group.Key);
            Console.WriteLine($"  -> gate feedback for {worker.Role}: rework {worker.File} against the real contract");
            await ProduceAsync(worker,
                $"Your previous {worker.File} failed the build gate:\n{string.Join("\n", group)}\n\n" +
                $"The authoritative shared contract file Contracts.cs is:\n{Contracts}\nRewrite the complete file so it compiles against it.");
        }
    }

    Console.WriteLine("\nFAILED — components still do not satisfy the contracts after 3 rounds.");
    return 1;
}
finally
{
    // Guaranteed cleanup: the workspace must not survive a crash or the success return above.
    try { Directory.Delete(workspace, recursive: true); }
    catch (IOException) { } catch (UnauthorizedAccessException) { }
}
