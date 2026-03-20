using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

const string MemoryPath = "expel_memory_maf.json";

var taskAgent =
    new ChatClientAgent(
        Settings.ChatClient,
        name: "TaskAgent",
        instructions: """
                      You are a skilled software engineer.
                      Solve the given task carefully. Provide complete, working code with no placeholders.
                      If learned rules are provided, apply them carefully to avoid known failure patterns.
                      Always handle edge cases explicitly (empty inputs, nulls, boundary values).
                      """
    );

AIAgent insightAgent = new ChatClientAgent(
    Settings.ChatClient,
    name: "InsightAgent",
    instructions: """
                  You are an expert at extracting generalizable learning rules from AI agent experiences.
                  Your rules must be GENERAL and HIGH LEVEL — applicable across different tasks,
                  not just the specific examples shown.
                  Focus on reasoning patterns, edge case handling, and implementation quality habits.
                  Output ONLY operation lines (AGREE / REMOVE / EDIT / ADD), one per line.
                  No preamble, no explanation, no markdown.
                  """
);

AIAgent evaluatorAgent = new ChatClientAgent(
    Settings.ChatClient,
    name: "EvaluatorAgent",
    instructions: """
                  You are a strict code reviewer. Evaluate whether the provided code correctly
                  solves the given task. Respond with JSON only:
                  {"passed": true/false, "reason": "<one sentence>"}
                  Be strict — incomplete implementations, missing edge cases, or pseudocode = false.
                  """
);

var tasks = new[]
{
    new TaskDefinition(
        "task-1",
        """
        Write a Python function 'find_duplicates(nums: list[int]) -> list[int]'
        that returns a sorted list of integers appearing more than once.
        Handle empty input — return [] in that case.
        """,
        true,
        EvaluateFindDuplicates
    ),
    new TaskDefinition(
        "task-2",
        """
        Write a Python function 'is_palindrome(s: str) -> bool'
        that returns True if the string is a palindrome ignoring case and spaces.
        Handle empty strings — return True in that case.
        """,
        true,
        EvaluateIsPalindrome
    ),
    new TaskDefinition(
        "task-3",
        """
        Write a Python function 'flatten(nested: list) -> list'
        that recursively flattens an arbitrarily nested list of integers.
        Handle empty lists — return [] in that case.
        """,
        false, // use LLM evaluator for this one
        null
    )
};

//Run ExpeL loop across all tasks

Console.WriteLine("=== ExpeL Agent (Microsoft Agent Framework) ===\n");

var memory = LoadMemory(MemoryPath);

foreach (var task in tasks)
{
    Console.WriteLine($"\n{'═',60}");
    Console.WriteLine($"Task: {task.Id}");
    Console.WriteLine($"{'═',60}\n{task.Description}");

    await RunTaskWithExpeL(taskAgent, evaluatorAgent, task, memory, 3);

    // After each task: extract cross-task insights and update the rule set
    await ExtractAndUpdateInsightsAsync(insightAgent, memory);

    SaveMemory(MemoryPath, memory);
}

Console.WriteLine("\n=== Final Insight Set ===\n");
foreach (var insight in memory.Insights.OrderByDescending(i => i.Score))
    Console.WriteLine($"[{insight.Score:+0;-0}] Rule {insight.Id}: {insight.Rule}");


async Task RunTaskWithExpeL(
    AIAgent taskAgt,
    AIAgent evalAgt,
    TaskDefinition taskDef,
    ExpeLMemory memory,
    int maxAttempts)
{
    var lowTemp = new ChatClientAgentRunOptions(
        new ChatOptions { Temperature = 0.2f });

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        Console.WriteLine($"\n── Attempt {attempt}/{maxAttempts} ──\n");

        // In MAF, inject insights into the user message per-run.
        // The agent's base instructions stay stable;
        // context (insights) is passed explicitly each time.
        var injectedPrompt = BuildInjectedPrompt(taskDef.Description, memory);

        var response = await taskAgt.RunAsync(injectedPrompt, options: lowTemp);
        var output = response.Text ?? "";

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

        // Use heuristic or LLM evaluator depending on task config
        trial.Succeeded = taskDef.UseHeuristicEval
            ? taskDef.Evaluator!(output, trial)
            : await EvaluateWithLLMAsync(evalAgt, taskDef.Description, output, trial);

        Console.WriteLine(trial.Succeeded ? "V PASSED\n" : "X FAILED\n");

        // Store BOTH successes and failures — ExpeL needs both for contrast
        memory.ExperienceBank.Add(trial);

        if (trial.Succeeded) break;
        if (attempt == maxAttempts) Console.WriteLine("Max attempts reached.\n");
    }
}

// EXTRACT AND UPDATE INSIGHTS
// InsightAgent contrasts successful vs failed trials,
// outputs AGREE/EDIT/REMOVE/ADD operations on the rule set.

async Task ExtractAndUpdateInsightsAsync(
    AIAgent insightAgt,
    ExpeLMemory memory)
{
    Console.WriteLine("\n── InsightAgent extracting cross-task insights ──\n");

    var successes = memory.ExperienceBank.Where(t => t.Succeeded).TakeLast(3).ToList();
    var failures = memory.ExperienceBank.Where(t => !t.Succeeded).TakeLast(3).ToList();

    if (successes.Count == 0 || failures.Count == 0)
    {
        Console.WriteLine("Not enough diversity yet — skipping.\n");
        return;
    }

    var successBlock = string.Join("\n\n---\n\n", successes.Select(t =>
        $"Task: {t.TaskId}\n{t.AgentOutput}"));
    var failureBlock = string.Join("\n\n---\n\n", failures.Select(t =>
        $"Task: {t.TaskId}\n{t.AgentOutput}"));
    var existingRules = memory.Insights.Count == 0
        ? "(none yet)"
        : string.Join("\n", memory.Insights
            .OrderByDescending(i => i.Score)
            .Select(i => $"Rule {i.Id} [{i.Score:+0;-0}]: {i.Rule}"));

    // Per-run medium temperature — insight extraction benefits
    // from slight creativity to find non-obvious patterns
    var medTemp = new ChatClientAgentRunOptions(
        new ChatOptions { Temperature = 0.35f });

    var operationsText = await insightAgt.RunAsync(
        $"""
         By examining and contrasting the successful trials against the failed trials,
         and the list of existing rules, perform operations so that the updated rule list
         is GENERAL and HIGH LEVEL — useful across different tasks, not just these examples.
         Focus on reasoning patterns, edge case handling, and implementation quality.

         === SUCCESSFUL TRIALS ===
         {successBlock}

         === FAILED TRIALS ===
         {failureBlock}

         === EXISTING RULES ===
         {existingRules}

         === AVAILABLE OPERATIONS ===
         AGREE <RULE NUMBER>: <EXISTING RULE>
         REMOVE <RULE NUMBER>: <EXISTING RULE>
         EDIT <RULE NUMBER>: <NEW MODIFIED RULE>
         ADD <NEW RULE NUMBER>: <NEW RULE>
         """,
        options: medTemp
    );

    Console.WriteLine($"Operations:\n{operationsText.Text}\n");
    ApplyInsightOperations(memory, operationsText.Text ?? "");

    Console.WriteLine("Updated insights:");
    foreach (var ins in memory.Insights.OrderByDescending(i => i.Score))
        Console.WriteLine($"  [{ins.Score:+0;-0}] Rule {ins.Id}: {ins.Rule}");
    Console.WriteLine();
}

void ApplyInsightOperations(ExpeLMemory memory, string operationsText)
{
    foreach (var line in operationsText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var trimmed = line.Trim();

        var agreeMatch = Regex.Match(trimmed, @"^AGREE\s+(\d+):", RegexOptions.IgnoreCase);
        if (agreeMatch.Success && int.TryParse(agreeMatch.Groups[1].Value, out var agreeId))
        {
            var ins = memory.Insights.FirstOrDefault(i => i.Id == agreeId);
            if (ins != null)
            {
                ins.Score++;
                Console.WriteLine($"  AGREE {agreeId} → {ins.Score}");
            }

            continue;
        }

        var removeMatch = Regex.Match(trimmed, @"^REMOVE\s+(\d+):", RegexOptions.IgnoreCase);
        if (removeMatch.Success && int.TryParse(removeMatch.Groups[1].Value, out var removeId))
        {
            if (memory.Insights.RemoveAll(i => i.Id == removeId) > 0)
                Console.WriteLine($"  REMOVE {removeId}");
            continue;
        }

        var editMatch = Regex.Match(trimmed, @"^EDIT\s+(\d+):\s*(.+)", RegexOptions.IgnoreCase);
        if (editMatch.Success && int.TryParse(editMatch.Groups[1].Value, out var editId))
        {
            var ins = memory.Insights.FirstOrDefault(i => i.Id == editId);
            if (ins != null)
            {
                ins.Rule = editMatch.Groups[2].Value.Trim();
                ins.Score = 0;
                Console.WriteLine($"  EDIT {editId}: {ins.Rule}");
            }

            continue;
        }

        var addMatch = Regex.Match(trimmed, @"^ADD\s+(\d+):\s*(.+)", RegexOptions.IgnoreCase);
        if (addMatch.Success)
        {
            var newId = memory.Insights.Count == 0 ? 1 : memory.Insights.Max(i => i.Id) + 1;
            var newRule = addMatch.Groups[2].Value.Trim();
            memory.Insights.Add(new Insight { Id = newId, Rule = newRule, Score = 0 });
            Console.WriteLine($"  ADD {newId}: {newRule}");
        }
    }

    var pruned = memory.Insights.RemoveAll(i => i.Score <= -3);
    if (pruned > 0) Console.WriteLine($"  Pruned {pruned} low-score insight(s).");
}

string BuildInjectedPrompt(string taskDescription, ExpeLMemory memory)
{
    const int MaxInsights = 5;

    var top = memory.Insights
        .OrderByDescending(i => i.Score)
        .Take(MaxInsights)
        .ToList();

    if (top.Count == 0) return taskDescription;

    var rulesBlock = string.Join("\n", top.Select((ins, i) =>
        $"{i + 1}. [{ins.Score:+0;-0}] {ins.Rule}"));

    return
        $"""
         === LEARNED RULES (apply these to avoid known failure patterns) ===
         {rulesBlock}
         ==================================================================

         Task:
         {taskDescription}
         """;
}

// LLM-BASED EVALUATOR
// For open-ended tasks where heuristics are insufficient.
// The EvaluatorAgent returns structured JSON.

async Task<bool> EvaluateWithLLMAsync(
    AIAgent evalAgt,
    string taskDescription,
    string agentOutput,
    Trial trial)
{
    var jsonOptions = new ChatClientAgentRunOptions(new ChatOptions
    {
        ResponseFormat = ChatResponseFormat.Json,
        Temperature = 0.1f
    });

    var evalResponse = await evalAgt.RunAsync(
        $"""
         Task: {taskDescription}

         Submitted code:
         {agentOutput}
         """,
        options: jsonOptions
    );

    try
    {
        var result = JsonSerializer.Deserialize<EvalResult>(
            evalResponse.Text ?? "{}") ?? new EvalResult();
        trial.EvaluationDetails = result.Reason;
        Console.WriteLine($"LLM eval: {result.Reason}");
        return result.Passed;
    }
    catch
    {
        trial.EvaluationDetails = "LLM eval parse failed";
        return false;
    }
}

// HEURISTIC EVALUATORS

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


// Dummy file based memory persistence — swap for a real database in production

ExpeLMemory LoadMemory(string path)
{
    if (!File.Exists(path)) return new ExpeLMemory();
    return JsonSerializer.Deserialize<ExpeLMemory>(File.ReadAllText(path)) ?? new ExpeLMemory();
}

void SaveMemory(string path, ExpeLMemory memory)
{
    File.WriteAllText(path,
        JsonSerializer.Serialize(memory, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Memory saved → {path}\n");
}