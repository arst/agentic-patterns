---
{
  "title": "Regression Evals",
  "summary": "A golden dataset of reviewed cases with tiered assertions (contains, NLP, judge) run as a gate, cached for CI.",
  "category": "Evaluation",
  "projects": [
    { "flavor": "AgentFramework", "path": "RegressionEvals.AgentFramework" }
  ]
}
---

## What it is

A prompt tweak that fixes one answer quietly breaks three others, and you find out in production.
Regression evals are unit tests for behavior: a checked-in **golden dataset** of cases, each with
an expected answer and an assertion, run as a suite that fails the build when quality drops. This
sample runs the suite through `Microsoft.Extensions.AI.Evaluation.Reporting`, which adds two
things a hand-rolled loop lacks — **response caching** (an unchanged case costs zero model calls
on re-run, the property that makes evals affordable in CI) and a **result store** the `aieval`
tool renders as an HTML report.

The assertions are **tiered cheapest-first**, because not every check needs a model:

- **contains** — a plain `Contains` string check. No model call.
- **nlp** — `F1Evaluator` token overlap against the golden answer. No model call.
- **judge** — `EquivalenceEvaluator`, an LLM-as-judge, for semantic equality. One model call.

Only **reviewed** cases run: a `GoldenCase` with a non-empty `ReviewedBy` is evaluated, everything
else is reported as awaiting review and skipped. The gate itself fails if nothing was evaluated —
a green run over zero reviewed cases is the same class of false confidence as a red regression
that ships anyway.

**Builds on:** **EvaluationAndMonitoring** records the trajectories this pattern turns into
review candidates — one is extracted straight from a recorded `run-trace.json` into `candidates/`
as the pipeline `production trace → candidate case → reviewer supplies/verifies the expected
result → promoted to the golden set`. A trace is ground truth about what *happened*, never about
what *should have* happened, so extraction alone never gates anything. Where **SelfCorrectionLoop**
runs an evaluator inline to fix a single answer, this runs the same class of evaluators as a
pre-merge gate over many. The judge tier is **LLMAsJudge** embedded in a suite.

## Information-theoretic view

An eval suite is a proxy for product quality, and a passing suite alongside a failing product
means the proxy has drifted (see `docs/coordination-physics.md`). The working posture is that the
suite is a hypothesis incidents revise: when a real regression ships green, the fix is a new
golden case, not a shrug. This is why the trace-sourced candidate matters — a captured trajectory
is ground truth about what actually happened, so growing the suite from real traces keeps the
proxy anchored to reality instead of to whatever the author imagined. But a trace only records
what happened, not what *should* have happened: a reviewer must supply or confirm the expected
result before a trace-derived case can gate anything, or the suite would simply freeze the
model's own historical mistakes into its own ground truth.

## When to use it

- You change prompts, models, or tools and need before/after evidence, not an impression.
- You run evaluation in CI and cannot pay for a live model call on every unchanged case.
- You want failures to block merges the way a red test does.

Skip the full harness for a one-off spot check — a single judged answer (see **LLMAsJudge**) is
the honest amount of machinery for a question you will not ask twice.

## How the demo works

Five golden cases live in `golden-cases.json`, each already carrying a `reviewedBy`. At runtime a
sixth candidate is extracted from `sample-run-trace.json` (a minimal `RunTrace` in
EvaluationAndMonitoring's format) by reading its first question and observed answer with
`JsonDocument`, and written to `candidates/from-trace.json` with `reviewedBy: null` — it never
joins the evaluated set. `CasePartition.Partition` splits `golden-cases.json` into cases with a
reviewer (evaluated) and cases without one (reported and skipped). The run prints two separate
counts, because the two states are not the same thing: `N golden case(s) awaiting sign-off` for
`golden-cases.json` rows that already have an expected answer and tier but no reviewer yet, and
`1 candidate case(s) awaiting review` for the trace-extracted `CandidateCase`, which has neither
and needs a reviewer to write the expected answer from scratch. Each evaluated case runs through
a `ScenarioRun` created from a cache-enabled
`DiskBasedReportingConfiguration`; the tier's assertion decides pass/fail; any failure, or an
empty evaluated set, sets the process exit code to 1.

```mermaid
flowchart LR
    T[sample-run-trace.json] -->|extract Q and observed A| C[candidate case in candidates/]
    C -->|reviewer supplies/verifies the expected result| G[golden-cases.json]
    G --> P{CasePartition: reviewedBy?}
    P -->|non-empty| S[Suite runner]
    P -->|empty/missing| W[awaiting sign-off — not evaluated]
    S --> A{tier}
    A -->|contains| X[Contains check]
    A -->|nlp| F[F1Evaluator]
    A -->|judge| E[EquivalenceEvaluator]
    X --> R[pass/fail + exit code]
    F --> R
    E --> R
    S -->|via ReportingConfiguration| Cache[(response cache + result store)]
```

The `contains` and `nlp` tiers never call the model. The `judge` tier does, but its result is
cached by the reporting configuration, so a second run of an unchanged suite is free.

## Key APIs

| API | Role |
|---|---|
| `DiskBasedReportingConfiguration.Create(storageRootPath, evaluators, chatConfiguration, enableResponseCaching, executionName)` | The cached, stored eval harness |
| `reporting.CreateScenarioRunAsync(scenarioName)` | One scenario (one case) |
| `scenarioRun.EvaluateAsync(messages, response, contexts)` | Runs the evaluators, caches the result |
| `F1EvaluatorContext(groundTruth)` / `EquivalenceEvaluatorContext(groundTruth)` | The expected answer per tier |
| `dotnet tool run aieval report --path <store> --output report.html` | Renders the HTML report |

```bash
dotnet run --project RegressionEvals.AgentFramework            # run the suite (exit 1 on failure or 0 reviewed)
dotnet run --project RegressionEvals.AgentFramework            # re-run: judged case served from cache
dotnet run --project RegressionEvals.AgentFramework -- --selfcheck   # offline trace-extraction check
```

## What to watch in the output

The first two lines report what was skipped, split by state: unsigned `golden-cases.json` rows
(fully specified, just need a reviewer's sign-off) and the trace-derived candidate (no expected
answer yet, needs one written from scratch). Each evaluated case then prints a `[PASS]`/`[FAIL]`
line with its tier and the assertion detail,
then the answer. The summary line reports the pass count and the `aieval` command to render the
report; the process exits non-zero if anything failed *or* if nothing was evaluated at all — both
are the CI gate. Run it twice: the second run returns the judged case from the response cache
without a model call. **EvaluationAndMonitoring** is where the trace-sourced candidate comes
from, and **LLMAsJudge** documents the judge tier's own failure modes.
