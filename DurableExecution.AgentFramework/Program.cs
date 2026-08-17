using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

// Durable execution: checkpoint every superstep to disk, "crash" mid-run,
// then resume from the last checkpoint in a fresh workflow instance.

var checkpointDirectory = Directory.CreateDirectory(
    Path.Combine(Path.GetTempPath(), "durable-execution-checkpoints", Guid.NewGuid().ToString("N")));

Console.WriteLine($"Checkpoint store: {checkpointDirectory.FullName}");

// ---- Phase 1: run with checkpointing, crash after Draft ----
Console.WriteLine("\n=== Phase 1: initial run (crashes after Draft) ===");

CheckpointInfo? lastCheckpoint = null;
var crashed = false;

using (var store = new FileSystemJsonCheckpointStore(checkpointDirectory))
{
    var environment = InProcessExecution.Lockstep
        .WithCheckpointing(CheckpointManager.CreateJson(store));

    await using var run = await environment.RunStreamingAsync(BuildWorkflow(), "durable workflows");
    await foreach (var evt in run.WatchStreamAsync())
    {
        switch (evt)
        {
            case ExecutorCompletedEvent completed:
                Console.WriteLine($"  Executed {completed.ExecutorId}");
                break;

            case SuperStepCompletedEvent { CompletionInfo.Checkpoint: { } checkpoint } superStep:
                lastCheckpoint = checkpoint;
                Console.WriteLine($"  Checkpoint saved for superstep {superStep.StepNumber}: {checkpoint.CheckpointId}");
                // Simulated crash: abandon the run after Draft, before Publish ever executes.
                crashed = superStep.StepNumber == 1;
                break;

            case WorkflowErrorEvent error:
                Console.WriteLine($"  Workflow error: {error.Exception}");
                return;
        }

        if (crashed)
            break;
    }
}

Console.WriteLine("  *** process crashed ***");
Console.WriteLine($"  Durable state on disk: {checkpointDirectory.GetFiles("*", SearchOption.AllDirectories).Length} file(s)");

// ---- Phase 2: "process restart" — fresh store, manager and workflow; resume from disk ----
Console.WriteLine("\n=== Phase 2: restarted process resumes from last checkpoint ===");
Console.WriteLine($"  Resuming session {lastCheckpoint!.SessionId} at checkpoint {lastCheckpoint.CheckpointId}");

using var restartedStore = new FileSystemJsonCheckpointStore(checkpointDirectory);
var restartedEnvironment = InProcessExecution.Lockstep
    .WithCheckpointing(CheckpointManager.CreateJson(restartedStore));

await using var resumedRun = await restartedEnvironment.ResumeStreamingAsync(BuildWorkflow(), lastCheckpoint);

await foreach (var evt in resumedRun.WatchStreamAsync())
    switch (evt)
    {
        case ExecutorInvokedEvent invoked:
            Console.WriteLine($"  Executing {invoked.ExecutorId} (Research and Draft were NOT re-run)");
            break;

        case WorkflowOutputEvent output:
            Console.WriteLine($"\nFinal output:\n{output.Data}");
            return;

        case WorkflowErrorEvent error:
            Console.WriteLine($"  Workflow error: {error.Exception}");
            return;
    }

return;

// Each phase builds its own instances — nothing survives the "restart" except the checkpoint files.
static Workflow BuildWorkflow()
{
    var research = new ResearchExecutor();
    var draft = new DraftExecutor();
    var publish = new PublishExecutor();

    return new WorkflowBuilder(research)
        .AddEdge(research, draft)
        .AddEdge(draft, publish)
        .WithOutputFrom(publish)
        .Build();
}

internal sealed record ResearchNotes(string Topic, string[] Findings);

internal sealed record Draft(string Topic, string Text);

internal sealed class ResearchExecutor() : Executor("Research")
{
    private async ValueTask HandleAsync(string topic, IWorkflowContext context)
    {
        var notes = new ResearchNotes(topic,
        [
            $"'{topic}' let long-running workflows survive process restarts.",
            "State is checkpointed after every superstep.",
            "Resume picks up exactly where the last checkpoint left off."
        ]);
        await context.SendMessageAsync(notes);
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder
            .ConfigureRoutes(routes => routes.AddHandler<string>(HandleAsync))
            .SendsMessage<ResearchNotes>();
    }
}

internal sealed class DraftExecutor() : Executor("Draft")
{
    private async ValueTask HandleAsync(ResearchNotes notes, IWorkflowContext context)
    {
        var text = $"# {notes.Topic}\n{string.Join("\n", notes.Findings.Select(f => $"- {f}"))}";
        await context.SendMessageAsync(new Draft(notes.Topic, text));
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder
            .ConfigureRoutes(routes => routes.AddHandler<ResearchNotes>(HandleAsync))
            .SendsMessage<Draft>();
    }
}

internal sealed class PublishExecutor() : Executor("Publish")
{
    private async ValueTask HandleAsync(Draft draft, IWorkflowContext context)
    {
        await context.YieldOutputAsync($"{draft.Text}\n\nPublished at {DateTimeOffset.UtcNow:O}");
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder
            .ConfigureRoutes(routes => routes.AddHandler<Draft>(HandleAsync))
            .YieldsOutput<string>();
    }
}
