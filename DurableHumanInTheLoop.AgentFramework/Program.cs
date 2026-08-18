using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;
using Shared;

// Durable human-in-the-loop: a workflow-level approval gate that survives a process restart.
//
// Contrast with HumanInTheLoop.AgentFramework: there the gate is at the TOOL level
// (ToolApprovalRequestContent inside a single agent run, same process). Here the gate is a
// workflow RequestPort: the agent drafts a refund email, the workflow emits a RequestInfoEvent
// for human sign-off and checkpoints to disk, the process EXITS, and a fresh process resumes
// from the checkpoint — which re-emits the same pending request (same RequestId) — collects the
// human's decision via SendResponseAsync, and completes.

if (args is ["resume", var stateFile])
{
    await ResumePhaseAsync(stateFile);
    return;
}

// ---- Phase 1: draft, request approval, checkpoint, exit the process ----
Console.WriteLine($"=== Phase 1 (pid {Environment.ProcessId}): draft refund, request human approval ===");

var checkpointDirectory = Directory.CreateDirectory(
    Path.Combine(Path.GetTempPath(), "durable-hitl-checkpoints", Guid.NewGuid().ToString("N")));
Console.WriteLine($"Checkpoint store: {checkpointDirectory.FullName}");

CheckpointInfo? lastCheckpoint = null;

// FileSystemJsonCheckpointStore holds an exclusive lock on the directory — dispose before phase 2 reopens it.
using (var store = new FileSystemJsonCheckpointStore(checkpointDirectory))
{
    var environment = InProcessExecution.Lockstep
        .WithCheckpointing(CheckpointManager.CreateJson(store));

    await using var run = await environment.RunStreamingAsync(BuildWorkflow(),
        "Customer ORD-5521 demands a full refund (EUR 1,249) for a smart speaker that keeps dropping off WiFi.");

    var approvalPending = false;
    await foreach (var evt in run.WatchStreamAsync())
    {
        switch (evt)
        {
            case ExecutorCompletedEvent completed:
                Console.WriteLine($"  Executed {completed.ExecutorId}");
                break;

            case RequestInfoEvent requestInfo when requestInfo.Request.TryGetDataAs<ApprovalRequest>(out var request):
                approvalPending = true;
                Console.WriteLine($"  Human approval requested (request id {requestInfo.Request.RequestId}):");
                Console.WriteLine($"  --- drafted email ---\n{Indent(request!.DraftedEmail)}\n  ---------------------");
                break;

            case SuperStepCompletedEvent { CompletionInfo.Checkpoint: { } checkpoint }:
                lastCheckpoint = checkpoint;
                Console.WriteLine($"  Checkpoint saved: {checkpoint.CheckpointId}");
                break;

            case WorkflowErrorEvent error:
                Console.WriteLine($"  Workflow error: {error.Exception}");
                return;
        }

        // The checkpoint written AFTER the request contains the pending approval — safe to "crash" now.
        if (approvalPending && evt is SuperStepCompletedEvent { CompletionInfo.Checkpoint: not null })
            break;
    }
}

var pendingFile = Path.Combine(checkpointDirectory.FullName, "pending-approval.txt");
File.WriteAllLines(pendingFile, [checkpointDirectory.FullName, lastCheckpoint!.SessionId, lastCheckpoint.CheckpointId]);
Console.WriteLine($"  Pending approval persisted to {pendingFile}");
Console.WriteLine("\n*** Phase 1 process exits — the approval is still outstanding, only disk state survives ***\n");

// Simulated restart: launch a brand-new process that knows nothing but the state file.
using var freshProcess = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, ["resume", pendingFile])
{
    UseShellExecute = false // inherit this console so the human can answer in phase 2
})!;
await freshProcess.WaitForExitAsync();
return;

// ---- Phase 2: fresh process resumes from the checkpoint and delivers the human's answer ----
static async Task ResumePhaseAsync(string stateFile)
{
    var state = File.ReadAllLines(stateFile);
    var checkpoint = new CheckpointInfo(sessionId: state[1], checkpointId: state[2]);

    Console.WriteLine($"=== Phase 2 (pid {Environment.ProcessId}): fresh process resumes from checkpoint {checkpoint.CheckpointId} ===");

    using var store = new FileSystemJsonCheckpointStore(new DirectoryInfo(state[0]));
    var environment = InProcessExecution.Lockstep
        .WithCheckpointing(CheckpointManager.CreateJson(store));

    await using var run = await environment.ResumeStreamingAsync(BuildWorkflow(), checkpoint);

    await foreach (var evt in run.WatchStreamAsync())
        switch (evt)
        {
            case RequestInfoEvent requestInfo when requestInfo.Request.TryGetDataAs<ApprovalRequest>(out var request):
                // The pending request is RE-EMITTED on resume with the same RequestId it had before the restart.
                Console.WriteLine($"  Pending approval re-emitted (request id {requestInfo.Request.RequestId}) — the agent did NOT re-draft:");
                Console.WriteLine($"  --- drafted email ---\n{Indent(request!.DraftedEmail)}\n  ---------------------");
                Console.Write("  Approve sending this refund email? (y/n): ");

                var input = Console.ReadLine();
                var approved = input is null || input.Trim().ToLowerInvariant() is "y" or "yes";
                if (input is null)
                    Console.WriteLine("y  (no console input — auto-approving)");

                await run.SendResponseAsync(requestInfo.Request.CreateResponse(
                    new ApprovalDecision(approved, approved ? "Approved by supervisor." : "Rejected by supervisor.", request.DraftedEmail)));
                break;

            case ExecutorCompletedEvent completed:
                Console.WriteLine($"  Executed {completed.ExecutorId}");
                break;

            case WorkflowOutputEvent output:
                Console.WriteLine($"\n=== Final outcome ===\n{output.Data}");
                return;

            case WorkflowErrorEvent error:
                Console.WriteLine($"  Workflow error: {error.Exception}");
                return;
        }
}

// Each phase builds its own workflow instance — nothing survives the restart except the checkpoint files.
static Workflow BuildWorkflow()
{
    var draft = new DraftRefundExecutor(Settings.ChatClient);
    var approvalPort = RequestPort.Create<ApprovalRequest, ApprovalDecision>("HumanApproval");
    var finalize = new FinalizeExecutor();

    return new WorkflowBuilder(draft)
        .AddEdge(draft, approvalPort)   // ApprovalRequest flows out of the workflow as a RequestInfoEvent
        .AddEdge(approvalPort, finalize) // ApprovalDecision flows back in via SendResponseAsync
        .WithOutputFrom(finalize)
        .Build();
}

static string Indent(string text) =>
    string.Join("\n", text.Split('\n').Select(line => $"  | {line.TrimEnd()}"));

// Checkpointed as JSON — keep these plain records.
public sealed record ApprovalRequest(string CustomerRequest, string DraftedEmail);

public sealed record ApprovalDecision(bool Approved, string Note, string DraftedEmail);

public sealed class DraftRefundExecutor(IChatClient chatClient) : Executor("DraftRefund")
{
    private readonly ChatClientAgent _agent = new(chatClient, name: "RefundDrafter",
        instructions: """
                      You draft customer-facing refund confirmation emails.
                      Write a short, professional email (subject + body, under 120 words)
                      confirming the refund described in the request. Plain text only.
                      """);

    private async ValueTask HandleAsync(string customerRequest, IWorkflowContext context)
    {
        var response = await _agent.RunAsync(customerRequest);
        await context.SendMessageAsync(new ApprovalRequest(customerRequest, response.Text));
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        protocolBuilder
            .ConfigureRoutes(routes => routes.AddHandler<string>(HandleAsync))
            .SendsMessage<ApprovalRequest>();
}

public sealed class FinalizeExecutor() : Executor("Finalize")
{
    // ponytail: rejected -> abort with note; add a revise loop back to DraftRefund if the demo ever needs one.
    private async ValueTask HandleAsync(ApprovalDecision decision, IWorkflowContext context) =>
        await context.YieldOutputAsync(decision.Approved
            ? $"Refund email SENT to customer:\n{decision.DraftedEmail}"
            : $"Refund ABORTED ({decision.Note}). No email sent.");

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        protocolBuilder
            .ConfigureRoutes(routes => routes.AddHandler<ApprovalDecision>(HandleAsync))
            .YieldsOutput<string>();
}
