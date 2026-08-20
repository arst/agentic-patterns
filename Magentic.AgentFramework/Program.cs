using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Specialized.Magentic;
using Microsoft.Extensions.AI;
using Shared;

var chatClient = Settings.ChatClient;

var researcher = new ChatClientAgent(chatClient,
    "You're a market researcher. Provide concise, factual market insights, competitor notes, and risks for the task at hand.",
    "Researcher",
    "Gathers market facts, competitors, and risks");

var analyst = new ChatClientAgent(chatClient,
    "You're a business analyst. Turn research into concrete recommendations: pricing, positioning, and go/no-go reasoning.",
    "Analyst",
    "Analyzes research and derives recommendations");

var writer = new ChatClientAgent(chatClient,
    "You're a business writer. Compose the final deliverable as a short, well-structured brief using the team's findings.",
    "Writer",
    "Writes the final brief");

var manager = new ChatClientAgent(chatClient,
    "You're an orchestration manager. Plan the task, delegate to the right specialist, and track progress until done.",
    "Manager",
    "Magentic orchestrator");

// Magentic: manager builds a task ledger (facts + plan), picks the next speaker each
// round via a progress ledger, and replans when the team stalls or loops.
var workflow = AgentWorkflowBuilder
    .CreateMagenticBuilderWith(manager)
    .AddParticipants([researcher, analyst, writer])
    .WithMaxRounds(10)
    .WithMaxStalls(2)
    .RequirePlanSignoff() // plan surfaces as a review request before execution
    .Build();

var run = await InProcessExecution.RunStreamingAsync(workflow, new ChatMessage(
    ChatRole.User,
    "Produce a short market-entry brief for a Nordic specialty-coffee subscription service expanding into Germany."));
await run.TrySendMessageAsync(new TurnToken(emitEvents: false));

await foreach (var evt in run.WatchStreamAsync().ConfigureAwait(false))
    switch (evt)
    {
        case MagenticPlanCreatedEvent planCreated:
            Console.WriteLine($"=== Plan created ===\n{planCreated.FullTaskLedger.Text.Trim()}\n");
            break;
        case MagenticReplannedEvent replanned:
            Console.WriteLine($"=== Replanned ===\n{replanned.FullTaskLedger.Text.Trim()}\n");
            break;
        case MagenticProgressLedgerUpdatedEvent { ProgressLedger: var ledger }:
            Console.WriteLine($"[ledger] satisfied={ledger.IsRequestSatisfied} loop={ledger.IsInLoop} progress={ledger.IsProgressBeingMade} next={ledger.NextSpeaker}");
            Console.WriteLine($"[ledger] instruction: {ledger.InstructionOrQuestion}\n");
            break;
        case RequestInfoEvent requestInfo when requestInfo.Request.TryGetDataAs<MagenticPlanReviewRequest>(out var review):
        {
            Console.Write("[review] plan sign-off requested. Approve? (y/yes = approve, other text = revision feedback, empty/EOF = reject): ");
            var answer = Console.ReadLine()?.Trim();
            if (answer?.ToLowerInvariant() is "y" or "yes")
            {
                await run.SendResponseAsync(requestInfo.Request.CreateResponse(review!.Approve()));
            }
            else if (!string.IsNullOrEmpty(answer))
            {
                Console.WriteLine("[review] sending revision feedback to the manager\n");
                await run.SendResponseAsync(requestInfo.Request.CreateResponse(review!.Revise(answer)));
            }
            else
            {
                // The API offers only Approve/Revise — fail closed by not responding and stopping the run.
                Console.WriteLine("\n[review] plan rejected — stopping without executing");
                return;
            }
            break;
        }
        case WorkflowOutputEvent output:
            Console.WriteLine("=== Final output ===");
            if (output.As<List<ChatMessage>>() is { } messages)
                foreach (var message in messages) Console.WriteLine($"##{message.AuthorName ?? message.Role.ToString()}: {message.Text.Trim()}");
            else
                Console.WriteLine(output.Data);
            break;
    }
