using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Ralph loop: run an agent in a loop with a FRESH context every iteration until a plan
// is satisfied. No conversation carries over — all state lives in files: PLAN.md is the
// task list, PROGRESS.md is the append-only log each iteration leaves for the next one,
// and the produced files are the actual work. (The original Ralph pattern communicates
// progress via git history; this demo uses PROGRESS.md for the same role.)
// Contrast Reflexion, which retries WITHIN one growing context, and DurableExecution,
// which checkpoints a workflow — here the context is deliberately thrown away each turn.

var workDir = Directory.CreateDirectory(
    Path.Combine(Path.GetTempPath(), "ralph-loop", Guid.NewGuid().ToString("N"))).FullName;
Console.WriteLine($"Workdir: {workDir}\n");

File.WriteAllText(Path.Combine(workDir, "PLAN.md"),
    """
    # Plan: two-day Copenhagen travel guide

    - [ ] research.md — list 6 sights with a one-line note each
    - [ ] itinerary.md — a 2-day itinerary that uses the sights from research.md
    - [ ] summary.md — a 5-sentence pitch for the trip, consistent with both files
    """);
File.WriteAllText(Path.Combine(workDir, "PROGRESS.md"), "# Progress log\n");

AITool[] fileTools =
[
    AIFunctionFactory.Create(() => string.Join("\n", Directory.GetFiles(workDir).Select(Path.GetFileName)),
        "list_files", "List the files in the working directory."),
    AIFunctionFactory.Create((string name) => File.ReadAllText(Path.Combine(workDir, Path.GetFileName(name))),
        "read_file", "Read a file from the working directory."),
    AIFunctionFactory.Create((string name, string content) =>
    {
        File.WriteAllText(Path.Combine(workDir, Path.GetFileName(name)), content);
        return $"Wrote {name} ({content.Length} chars).";
    }, "write_file", "Write (or overwrite) a file in the working directory.")
];

var agent = new ChatClientAgent(Settings.ChatClient,
    """
    You are ONE iteration of a loop; you have no memory of previous iterations.
    First read PLAN.md and PROGRESS.md. Then complete exactly ONE unchecked task
    ("- [ ]") from the plan: create its file, mark the task done ("- [x]") by rewriting
    PLAN.md, and append one line describing what you did to PROGRESS.md.
    If every task is already checked, change nothing. Finally reply with a single line:
    "DONE: <task>" or "ALL-DONE".
    """,
    tools: fileTools);

// The HOST decides when the work is done, not the agent: checked boxes only count
// when the artifact each task promises actually exists and is non-empty.
string[] artifacts = ["research.md", "itinerary.md", "summary.md"];
List<string> missing = [.. artifacts];

for (var iteration = 1; iteration <= 8; iteration++)
{
    // A brand-new session per iteration — the fresh context IS the pattern.
    var session = await agent.CreateSessionAsync();
    var response = await agent.RunAsync("Run one iteration of the loop.", session);

    var toolCalls = response.Messages
        .SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count();
    Console.WriteLine($"Iteration {iteration}: {response.Text.ReplaceLineEndings(" ").Trim()}  [{toolCalls} tool calls, context discarded]");

    var planDone = !File.ReadAllText(Path.Combine(workDir, "PLAN.md")).Contains("- [ ]");
    missing = [.. artifacts.Where(f => new FileInfo(Path.Combine(workDir, f)) is not { Exists: true, Length: > 0 })];

    if (planDone && missing.Count == 0)
        break;

    if (planDone)
    {
        // Agent checked boxes it didn't earn — host unchecks them and leaves feedback.
        var plan = File.ReadAllText(Path.Combine(workDir, "PLAN.md"));
        foreach (var f in missing)
            plan = plan.Replace($"- [x] {f}", $"- [ ] {f}");
        File.WriteAllText(Path.Combine(workDir, "PLAN.md"), plan);
        File.AppendAllText(Path.Combine(workDir, "PROGRESS.md"),
            $"- HOST CHECK: {string.Join(", ", missing)} missing or empty — task re-opened.\n");
        Console.WriteLine($"  Host check failed: {string.Join(", ", missing)} missing or empty — re-opened.");
    }
}

if (missing.Count > 0)
    Console.WriteLine($"\nFAILED: plan not satisfied — missing or empty: {string.Join(", ", missing)}");

Console.WriteLine($"\n---- PLAN.md ----\n{File.ReadAllText(Path.Combine(workDir, "PLAN.md"))}");
Console.WriteLine($"\n---- PROGRESS.md (how iterations talked to each other) ----\n{File.ReadAllText(Path.Combine(workDir, "PROGRESS.md"))}");
Console.WriteLine($"\n---- Files produced ----\n{string.Join("\n", Directory.GetFiles(workDir).Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length} bytes)"))}");
