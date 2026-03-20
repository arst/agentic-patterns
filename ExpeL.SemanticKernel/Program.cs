using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Shared;

var kernel = Settings.Kernel;
var chat = kernel.GetRequiredService<IChatCompletionService>();

const string memoryPath = "expel_memory.json";

// ExpeL's power comes from cross-task generalisation.
// Running multiple tasks lets the agent find patterns
// that transfer — not just fix one specific failure.

var tasks = new[]
{
    new TaskDefinition(
        "task-1",
        """
        Write a Python function 'find_duplicates(nums: list[int]) -> list[int]'
        that returns a sorted list of integers appearing more than once.
        Handle empty input — return [] in that case.
        """,
        EvaluateFindDuplicates
    ),
    new TaskDefinition(
        "task-2",
        """
        Write a Python function 'is_palindrome(s: str) -> bool'
        that returns True if the string is a palindrome ignoring case and spaces.
        Handle empty strings — return True in that case.
        """,
        EvaluateIsPalindrome
    ),
    new TaskDefinition(
        "task-3",
        """
        Write a Python function 'flatten(nested: list) -> list'
        that recursively flattens an arbitrarily nested list of integers.
        Handle empty lists — return [] in that case.
        """,
        EvaluateFlatten
    )
};

//Run the ExpeL loop across all tasks

Console.WriteLine("=== ExpeL Agent===\n");

var memory = LoadMemory(memoryPath);

foreach (var task in tasks)
{
    Console.WriteLine($"\n{'═',60}");
    Console.WriteLine($"Task: {task.Id}");
    Console.WriteLine($"{'═',60}\n");
    Console.WriteLine(task.Description);

    await RunTaskWithExpeL(chat, task, memory, 3);

    // After each task, extract insights from the experience bank
    // and update the living rule set — this is what makes it
    // cross-task: insights from task-1 inform task-2, and so on.
    await ExtractAndUpdateInsightsAsync(chat, memory);

    SaveMemory(memoryPath, memory);
}

Console.WriteLine("\n=== Final Insight Set ===\n");
foreach (var insight in memory.Insights)
    Console.WriteLine($"[{insight.Score:+0;-0}] Rule {insight.Id}: {insight.Rule}");


// ════════════════════════════════════════════════════════════
// STEP 1 — RUN TASK WITH EXPEL
// Attempts the task, prepending current insights before each try.
// Both successful and failed trials are stored in the bank.
// ════════════════════════════════════════════════════════════

async Task RunTaskWithExpeL(
    IChatCompletionService svc,
    TaskDefinition taskDef,
    ExpeLMemory memory,
    int maxAttempts)
{
    var lowTemp = new OpenAIPromptExecutionSettings { Temperature = 0.2 };

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        Console.WriteLine($"\n── Attempt {attempt}/{maxAttempts} ──\n");

        // ── Build prompt with injected insights ───────────────
        // Unlike Reflexion (task-specific reflections),
        // ExpeL injects GENERAL cross-task rules here.
        var history = new ChatHistory();
        history.AddSystemMessage(BuildSystemPromptWithInsights(memory));
        history.AddUserMessage(taskDef.Description);

        var response = await svc.GetChatMessageContentAsync(history, lowTemp);
        var output = response.Content ?? "";

        Console.WriteLine($"Output:\n{output}\n");

        var trial = new Trial
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            TaskId = taskDef.Id,
            TaskDescription = taskDef.Description,
            AttemptNumber = attempt,
            AgentOutput = output,
            Timestamp = DateTime.UtcNow
        };

        trial.Succeeded = taskDef.Evaluator(output, trial);

        Console.WriteLine(trial.Succeeded
            ? "✅ PASSED\n"
            : "❌ FAILED\n");

        // ── Store in experience bank (success AND failure) ────
        // This is the key ExpeL difference from Reflexion:
        // we keep successful trials too, so the insight extractor
        // can contrast good vs bad approaches.
        memory.ExperienceBank.Add(trial);

        if (trial.Succeeded) break;

        if (attempt == maxAttempts)
            Console.WriteLine("Max attempts reached.\n");
    }
}


// ════════════════════════════════════════════════════════════
// STEP 2 — EXTRACT AND UPDATE INSIGHTS
// Compares successful vs failed trials, generates new rules,
// then applies AGREE/EDIT/REMOVE/ADD to the living rule set.
// ════════════════════════════════════════════════════════════

async Task ExtractAndUpdateInsightsAsync(
    IChatCompletionService svc,
    ExpeLMemory memory)
{
    Console.WriteLine("\nExtracting cross-task insights\n");

    var successfulTrials = memory.ExperienceBank
        .Where(t => t.Succeeded)
        .TakeLast(3) // limit context size
        .ToList();

    var failedTrials = memory.ExperienceBank
        .Where(t => !t.Succeeded)
        .TakeLast(3)
        .ToList();

    // Need at least one of each to contrast
    if (successfulTrials.Count == 0 || failedTrials.Count == 0)
    {
        Console.WriteLine("Not enough trial diversity yet — skipping insight extraction.\n");
        return;
    }

    // Extract new candidate insights
    var candidateInsights = await ExtractCandidateInsightsAsync(
        svc, successfulTrials, failedTrials, memory.Insights);

    Console.WriteLine($"Candidate insights extracted:\n{candidateInsights}\n");

    // Apply AGREE/EDIT/REMOVE/ADD operations
    // Parse the LLM's structured output and mutate the rule set.
    ApplyInsightOperations(memory, candidateInsights);

    Console.WriteLine("Updated insight set:");
    foreach (var insight in memory.Insights.OrderByDescending(i => i.Score))
        Console.WriteLine($"  [{insight.Score:+0;-0}] Rule {insight.Id}: {insight.Rule}");
    Console.WriteLine();
}

async Task<string> ExtractCandidateInsightsAsync(
    IChatCompletionService svc,
    List<Trial> successes,
    List<Trial> failures,
    List<Insight> existingInsights)
{
    var successBlock = string.Join("\n\n---\n\n", successes.Select(t =>
        $"Task: {t.TaskId}\nOutput:\n{t.AgentOutput}"));

    var failureBlock = string.Join("\n\n---\n\n", failures.Select(t =>
        $"Task: {t.TaskId}\nOutput:\n{t.AgentOutput}"));

    var existingRules = existingInsights.Count == 0
        ? "(none yet)"
        : string.Join("\n", existingInsights
            .OrderByDescending(i => i.Score)
            .Select(i => $"Rule {i.Id} [{i.Score:+0;-0}]: {i.Rule}"));

    var history = new ChatHistory();
    history.AddSystemMessage("""
                             You are an expert at extracting generalizable learning rules from AI agent experiences.
                             Your rules must be GENERAL and HIGH LEVEL — applicable across different tasks,
                             not just the specific examples shown.
                             Focus on critiquing reasoning patterns, edge case handling, and code quality habits.
                             """);

    // This prompt mirrors the ExpeL methodology
    history.AddUserMessage(
        $"""
         By examining and contrasting the successful trials against the failed trials,
         and the list of existing rules, perform the following operations so that
         the new list of rules is GENERAL and HIGH LEVEL critiques of the failed trials
         or proposed ways of thought, so they can be used to avoid similar failures
         when encountered with DIFFERENT questions in the future.
         Have an emphasis on critiquing how to perform better reasoning and implementation.

         === SUCCESSFUL TRIALS ===
         {successBlock}

         === FAILED TRIALS ===
         {failureBlock}

         === EXISTING RULES ===
         {existingRules}

         === AVAILABLE OPERATIONS ===
         AGREE <RULE NUMBER>: <EXISTING RULE>        (rule is strongly relevant — keep as-is)
         REMOVE <RULE NUMBER>: <EXISTING RULE>       (contradictory, duplicated, or no longer useful)
         EDIT <RULE NUMBER>: <NEW MODIFIED RULE>     (rule needs broadening or improving)
         ADD <NEW RULE NUMBER>: <NEW RULE>           (new distinct insight not covered by existing rules)

         Rules that are not AGREE'd, EDIT'd, or REMOVE'd are automatically copied unchanged.
         Output ONLY the operation lines, one per line. No preamble, no explanation.
         """);

    var settings = new OpenAIPromptExecutionSettings { Temperature = 0.3 };
    var response = await svc.GetChatMessageContentAsync(history, settings);
    return response.Content?.Trim() ?? "";
}


// INSIGHT OPERATIONS PARSER
// Parses the LLM's structured output and mutates the rule set.
// AGREE → upvote score
// REMOVE → remove from list
// EDIT → replace rule text, reset score
// ADD → insert new rule with score 0
void ApplyInsightOperations(ExpeLMemory memory, string operationsText)
{
    foreach (var line in operationsText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var trimmed = line.Trim();

        // AGREE <N>: <rule>
        var agreeMatch = Regex.Match(trimmed, @"^AGREE\s+(\d+):", RegexOptions.IgnoreCase);
        if (agreeMatch.Success && int.TryParse(agreeMatch.Groups[1].Value, out var agreeId))
        {
            var insight = memory.Insights.FirstOrDefault(i => i.Id == agreeId);
            if (insight != null)
            {
                insight.Score++;
                Console.WriteLine($"  AGREE Rule {agreeId} → score now {insight.Score}");
            }

            continue;
        }

        // REMOVE <N>: <rule>
        var removeMatch = Regex.Match(trimmed, @"^REMOVE\s+(\d+):", RegexOptions.IgnoreCase);
        if (removeMatch.Success && int.TryParse(removeMatch.Groups[1].Value, out var removeId))
        {
            var removed = memory.Insights.RemoveAll(i => i.Id == removeId);
            if (removed > 0)
                Console.WriteLine($"  REMOVE Rule {removeId}");
            continue;
        }

        // EDIT <N>: <new rule text>
        var editMatch = Regex.Match(trimmed, @"^EDIT\s+(\d+):\s*(.+)", RegexOptions.IgnoreCase);
        if (editMatch.Success && int.TryParse(editMatch.Groups[1].Value, out var editId))
        {
            var insight = memory.Insights.FirstOrDefault(i => i.Id == editId);
            if (insight != null)
            {
                insight.Rule = editMatch.Groups[2].Value.Trim();
                insight.Score = 0; // reset score on edit — re-prove its value
                Console.WriteLine($"  EDIT Rule {editId}: {insight.Rule}");
            }

            continue;
        }

        // ADD <N>: <new rule text>
        var addMatch = Regex.Match(trimmed, @"^ADD\s+(\d+):\s*(.+)", RegexOptions.IgnoreCase);
        if (addMatch.Success)
        {
            var newId = memory.Insights.Count == 0 ? 1 : memory.Insights.Max(i => i.Id) + 1;
            var newRule = addMatch.Groups[2].Value.Trim();
            memory.Insights.Add(new Insight { Id = newId, Rule = newRule, Score = 0 });
            Console.WriteLine($"  ADD Rule {newId}: {newRule}");
        }
    }

    // Prune insights with a very negative score — they've proven unhelpful
    var pruned = memory.Insights.RemoveAll(i => i.Score <= -3);
    if (pruned > 0)
        Console.WriteLine($"  Pruned {pruned} low-scoring insight(s).");
}


// Injects the top-scoring insights into every task attempt.
// Only inject the highest-scoring rules to keep context lean.

string BuildSystemPromptWithInsights(ExpeLMemory memory)
{
    const int MaxInsightsToInject = 5;

    var topInsights = memory.Insights
        .OrderByDescending(i => i.Score)
        .Take(MaxInsightsToInject)
        .ToList();

    if (topInsights.Count == 0)
        return "You are a skilled software engineer. Solve the task carefully with complete, working code.";

    var rulesBlock = string.Join("\n", topInsights.Select((ins, i) =>
        $"{i + 1}. [{ins.Score:+0;-0}] {ins.Rule}"));

    return
        $"""
         You are a skilled software engineer. Solve the task carefully with complete, working code.

         You have learned the following general rules from past experience.
         Apply them to avoid known failure patterns:

         === LEARNED RULES (ordered by usefulness) ===
         {rulesBlock}
         =============================================
         """;
}


// Evaluators — one per task, swap for real validators
bool EvaluateFindDuplicates(string output, Trial trial)
{
    var checks = new[]
    {
        output.Contains("def find_duplicates"),
        output.Contains("return"),
        output.Contains("[]") || output.Contains("list()"),
        output.Contains("sorted") || output.Contains(".sort()")
    };
    trial.EvaluationDetails = $"Checks: {checks.Count(c => c)}/{checks.Length}";
    return checks.All(c => c);
}

bool EvaluateIsPalindrome(string output, Trial trial)
{
    var checks = new[]
    {
        output.Contains("def is_palindrome"),
        output.Contains("return"),
        output.Contains("lower") || output.Contains("casefold"),
        output.Contains("replace") || output.Contains("strip") || output.Contains("split")
    };
    trial.EvaluationDetails = $"Checks: {checks.Count(c => c)}/{checks.Length}";
    return checks.All(c => c);
}

bool EvaluateFlatten(string output, Trial trial)
{
    var checks = new[]
    {
        output.Contains("def flatten"),
        output.Contains("return"),
        output.Contains("isinstance") || output.Contains("type("),
        output.Contains("for ") && output.Contains("in ")
    };
    trial.EvaluationDetails = $"Checks: {checks.Count(c => c)}/{checks.Length}";
    return checks.All(c => c);
}


// Dummy file based memory persistence — swap for a real database in production

ExpeLMemory LoadMemory(string path)
{
    if (!File.Exists(path)) return new ExpeLMemory();
    var json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<ExpeLMemory>(json) ?? new ExpeLMemory();
}

void SaveMemory(string path, ExpeLMemory memory)
{
    var json = JsonSerializer.Serialize(memory, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(path, json);
    Console.WriteLine($"Memory saved → {path}\n");
}