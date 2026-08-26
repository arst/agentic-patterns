---
{
  "title": "Orchestrator-Workers",
  "summary": "Let an orchestrator choose request-specific tasks, validate them, run fixed workers with bounded concurrency, and synthesize the results while naming any that failed.",
  "category": "Orchestration",
  "projects": [ { "flavor": "AgentFramework", "path": "OrchestratorWorkers.AgentFramework" } ]
}
---

## What it is

Orchestrator-Workers uses a central model to decide which independent subtasks a particular
request needs. The host validates that typed plan, dispatches each task to a fixed worker registry,
and asks a synthesizer to combine the results — successes and failures alike. A failed task is
carried in with its error and an explicit instruction not to invent an output, so the gap is at
least visible to the synthesizer; the host abstains outright when every worker fails.

This is dynamic fan-out, but not an open-ended team protocol:

- **Parallelization:** the host knows the branches in advance.
- **Orchestrator-Workers:** the orchestrator chooses branches from the request.
- **Magentic:** a manager continuously controls a multi-round team with replanning and stalls.

## When to use it

- The useful research angles vary substantially by request.
- Tasks are independent once decomposed and can run concurrently.
- Worker roles need different instructions, tools, data, or permissions.

Use static **Parallelization** when the same branches always run; it is cheaper and easier to
validate. Use **Prompt Chaining** when tasks depend on earlier outputs.

## How the demo works

The request asks whether a Nordic coffee-subscription business should launch in Germany. The
orchestrator returns a typed `WorkPlan`, choosing from market, competitor, and regulatory roles.
`PlanValidator` rejects unknown roles, excessive task count, duplicate IDs, and oversized
instructions before any worker runs.

```mermaid
flowchart LR
    U[User request] --> O[Orchestrator<br/>typed WorkPlan]
    O --> V[Host validation]
    V -->|valid| R[Fixed WorkerRegistry]
    R -->|bounded concurrency| W1[Market]
    R --> W2[Competition]
    R --> W3[Regulation]
    W1 --> A{WorkerRegistry.Assess}
    W2 --> A
    W3 --> A
    A -->|complete| S[Synthesizer]
    A -->|partial: some tasks FAILED| S
    A -->|all failed| X[Abstain: no synthesis]
    S -->|partial| P[Answer flagged incomplete]
    S -->|complete| Ans[Answer]
    V -->|invalid| D[Reject before execution]
```

`WorkerRegistry` uses `SemaphoreSlim` to cap concurrency and captures per-task failures without
discarding successful reports. Every result enters the synthesis evidence, failed tasks included,
each tagged `STATUS: FAILED` with its error so the synthesizer cannot silently paper over a gap.
`WorkerRegistry.Assess` labels the run `Complete`, `Partial`, or `Abstained`; an all-failed run
skips synthesis entirely, and a partial run tells the synthesizer to call out unsupported
conclusions instead of inferring them.

## Key APIs

- `agent.RunAsync<WorkPlan>(...)` — structured dynamic decomposition.
- `PlanValidator.Validate(...)` — trusted-host validation before dispatch.
- `WorkerRegistry` — fixed role-to-executor mapping; the model cannot create arbitrary workers.
- `Task.WhenAll(...)` + `SemaphoreSlim` — concurrent execution with a hard concurrency ceiling.
- `WorkerRegistry.Assess(...)` — labels a run `Complete`, `Partial`, or `Abstained` from its results.

## What to watch in the output

First inspect the serialized validated plan: its tasks should reflect the request rather than a
hard-coded fan-out. Worker outputs follow, then one synthesis, labelled `COMPLETE` or `PARTIAL` —
or, if every worker failed, an abstention with no synthesis at all. A worker failure becomes a
failed `WorkerResult`; it does not erase independent successful evidence, and it is not hidden
from the synthesizer either.
