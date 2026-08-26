---
{
  "title": "Reasoning and Acting",
  "summary": "Interleave thinking with tool calls so each fact the model gathers informs the next step.",
  "category": "Reasoning & generation",
  "projects": [ { "flavor": "SemanticKernel", "path": "ReasoningAndActing" } ]
}
---

## What it is

ReAct interleaves two things that usually happen separately: **reasoning** about what you still
need to know, and **acting** to find out. The model thinks, calls a tool, reads the result,
thinks again with that result in hand, and repeats until it can answer. Neither half works alone —
reasoning without tools invents facts, tools without reasoning cannot decide which one to call
next.

Compared to plain Tool Use, the difference is the *loop* and the running commentary between
calls. Compared to Planning, the difference is that nothing is decided upfront; the next action
depends on what the last one returned.

## When to use it

- Questions that need several lookups where later lookups depend on earlier results.
- Research-shaped work: gather, compare, compute, conclude.
- Anywhere you want a traceable record of why each call was made.

Skip it when one tool call answers the question — that is Tool Use, and the extra reasoning
scaffold is just tokens. Skip it for a fixed known sequence of steps; a hard-coded chain is
cheaper and cannot wander. Always cap the loop: an unbounded ReAct agent can call tools until
your budget runs out.

## How the demo works

A local kernel is built with `Settings.CreateKernelBuilder()` so the shared `Settings.Kernel`
singleton stays untouched, then `ResearchTools` is registered as a plugin. It exposes two
`[KernelFunction]` methods: `GetPopulation(country)`, a simulated lookup returning strings like
"Approximately 40.1 million (2024 estimate)", and `Calculate(expression)`, which evaluates a math
expression with `new DataTable().Compute(...)`.

The question is *"Which country has a larger population — Canada or Australia? And what is the
approximate ratio?"* — deliberately unanswerable in one call. The agent has to look up both
countries, then divide. `FunctionChoiceBehavior.Auto()` lets it keep calling until it stops.

```mermaid
flowchart LR
    Q[Which is larger<br/>Canada or Australia] --> A[Chat completion service]
    A -->|GetPopulation Canada| T1[ResearchTools]
    T1 -->|40.1 million| A
    A -->|GetPopulation Australia| T2[ResearchTools]
    T2 -->|26.5 million| A
    A -->|Calculate 40.1 divided by 26.5| T3[ResearchTools]
    T3 -->|equals 1.513| A
    A --> R[Final answer with ratio]
```

SK 1.79 exposes no max-auto-invoke setting on `FunctionChoiceBehavior`, so the system prompt still
carries *"Use at most 10 tool calls before giving your final answer."* — but that sentence is only
a hint the model can ignore. The actual control is `ToolCallBudgetFilter`, an
`IAutoFunctionInvocationFilter` registered on the local kernel as a single instance created for
this run — the counter lives on the instance, so the budget is per run, not per process. It counts
every auto-invoked call; the 10th runs, and the 11th never reaches the tool because the filter sets
`context.Terminate = true`, which ends SK's auto-invocation loop.

Throwing from a filter does *not* end it. SK 1.79 wraps every auto-invoked call in a catch-all that
converts any exception into a tool-result error message and keeps looping, so a throwing filter
blocks the tool body, hands the model its own budget refusal as tool output to paraphrase, and lets
the loop run on to SK's internal auto-invoke ceiling instead of stopping at 10. `Terminate` is the
stop SK honours, and it is the same mechanism **Goal Settings and Monitoring**'s
`GoalMonitoringFilter` uses for its own max-iteration bound. Because SK returns normally after
terminating (with empty content), the filter exposes the stop as a `BudgetExhausted` flag rather
than an exception; `Program.cs` reads it and prints a `PARTIAL` result — the same shape
**Bounded Execution** uses for its own hard stops.
`AgenticPatterns.Tests.ToolCallBudgetFilterRealLoopTests` drives this against a real SK
auto-invocation loop (a stubbed HTTP handler that always returns a tool call, no network) and pins
both the exact `Terminate` behaviour and the fact that reverting to a throwing filter blows past the
budget by more than an order of magnitude.

## Key APIs

- `Settings.CreateKernelBuilder().Build()` — a private kernel so plugin registration is not global.
- `kernel.Plugins.AddFromType<ResearchTools>()` — registers both tools from one class.
- `[KernelFunction]` + `[Description]` on `GetPopulation` and `Calculate`.
- `new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() }`.
- `chatService.GetChatMessageContentAsync(history, settings, kernel)` — the kernel argument is what
  makes auto-invocation possible.
- `ToolCallBudgetFilter : IAutoFunctionInvocationFilter` — the host-enforced call cap, stopping
  the loop with `context.Terminate = true`; see **Bounded Execution** for the fuller pattern of
  hard, host-enforced run limits.

## What to watch in the output

The demo prints a single block prefixed `ReAct Agent:`. The tool calls themselves are not logged,
so the tell is in the content: the answer should quote the exact simulated figures — 40.1 million
and 26.5 million — and a ratio near 1.5, none of which the model could produce without calling
the plugin. If the model instead wanders past the tool-call budget, the answer block is
replaced by `Result status: PARTIAL`, a `Stop reason:` line naming the exhausted budget, and an
explicit incomplete label — proof the bound stopped the loop rather than the model choosing to
stop. **Tool Use** is the single-call
foundation this loops over; **Middleware** shows how to log every invocation so the
reasoning-acting alternation becomes visible.
