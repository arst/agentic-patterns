# Evaluation Sub-Catalog — Design

Date: 2026-08-24
Status: approved approach A (four self-contained samples with deliberate cross-links)

## Goal

Add an **Evaluation** sub-catalog to the pattern collection: four AgentFramework-only patterns
covering evaluation as a discipline — offline judging, regression gating, trajectory scoring,
and adversarial testing. The existing `EvaluationAndMonitoring` pattern covers *observability*
(telemetry, trace record/replay); these four cover *judging quality*. Every pattern explicitly
states which existing patterns it builds on, and all four lean on
`Microsoft.Extensions.AI.Evaluation` and the other first-party .NET AI libraries wherever they
apply.

## Scope decisions (already made with the user)

- Four patterns: `LLMAsJudge`, `RegressionEvals`, `TrajectoryEvaluation`, `RedTeaming`.
- AgentFramework flavor only (MEAI.Evaluation plugs into `IChatClient`; an SK flavor adds no
  comparative insight). Matches recent-pattern norm.
- RedTeaming uses an attacker agent + judge on the existing Azure OpenAI deployment. **No**
  `Microsoft.Extensions.AI.Evaluation.Safety` — it requires an Azure AI Foundry project, a new
  infra prerequisite the repo does not want.
- New Pattern Explorer category: **"Evaluation"** (front-matter `category` is free text; the UI
  groups dynamically). README's category list grows from four groups to five.

## Non-goals

- No Semantic Kernel flavors.
- No `Microsoft.Extensions.AI.Evaluation.Safety` / Azure AI Foundry dependency.
- No A/B–champion-challenger pattern (overlaps ResourceAwareOptimization), no human-annotation
  loop (not demoable in a console sample).
- No shared `Evaluation.Common` library — each sample stays a self-contained, readable
  `Program.cs` per repo convention; ~30 lines of duplicated reporting plumbing is cheaper than
  the indirection.

## Packages (Directory.Packages.props, central management)

| Package | Version |
|---|---|
| `Microsoft.Extensions.AI.Evaluation` | 10.9.0 |
| `Microsoft.Extensions.AI.Evaluation.Quality` | 10.9.0 |
| `Microsoft.Extensions.AI.Evaluation.Reporting` | 10.9.0 |
| `Microsoft.Extensions.AI.Evaluation.NLP` | 10.9.0-preview.1.26411.16 |

All align with the repo's existing `Microsoft.Extensions.AI` 10.9.0. Verified on NuGet
2026-08-24.

## Shared conventions (all four samples)

- Console project `<Pattern>.AgentFramework/` + `PatternExplorer/patterns/<Pattern>.md`
  front-matter (`title`, `summary`, `category: "Evaluation"`, `projects`) + README table row +
  solution entry in `Agentic Patterns.slnx`.
- Model access via `Shared.Settings.ChatClient`; judge/evaluator calls wrap it in the
  library's `ChatConfiguration`.
- TechCorp support-agent domain, consistent with existing samples.
- Each pattern doc ends (per repo style) with cross-references; each also gets a short
  **"Builds on"** paragraph naming the existing patterns it extends.

## Pattern 1 — LLMAsJudge

**Project:** `LLMAsJudge.AgentFramework`

The foundational scoring pattern: a judge model grades outputs against criteria, standalone
rather than inline in a generation loop.

Flow:
1. Generate answers to 3 TechCorp support questions with a `SupportAgent` (live call).
2. Score each answer with built-in quality evaluators — `RelevanceEvaluator`,
   `CoherenceEvaluator`, `GroundednessEvaluator` (grounding context supplied from the policy
   text the agent was given) — via `ChatConfiguration` over `Settings.ChatClient`.
3. A custom `IEvaluator` (`RubricJudgeEvaluator`): rubric-based 1–5 score with a required
   one-sentence justification, demonstrating how to write your own evaluator on the library's
   abstractions.
4. Pairwise comparison with position-swap: two candidate answers to the same question, judge
   picks a winner, then the candidates are swapped and judged again. If the verdict flips, the
   sample reports position bias detected — the judge's known failure modes (position bias,
   verbosity bias, self-preference) are the teaching point.

Output: per-answer metric table, pairwise verdicts with/without swap, bias verdict.

Builds on: `SelfCorrectionLoop` and `Debate` use judges *inline* to drive generation; this
pattern is the same judge as a standalone measurement instrument, plus its failure modes.
`ConfidenceReporting` is the self-assessment counterpart.

## Pattern 2 — RegressionEvals

**Project:** `RegressionEvals.AgentFramework`

Eval-driven development: a golden dataset with tiered assertions run as a suite that gates
change, like unit tests for behavior.

Flow:
1. Golden dataset: a checked-in `golden-cases.json` (~6 cases: `id`, `question`,
   `expectedAnswer`, `groundingContext`, `tier`).
2. One additional case is **sourced from a recorded trace**: a checked-in
   `sample-run-trace.json` in `EvaluationAndMonitoring`'s `RunTrace` format (recorded once via
   that sample and committed). The sample extracts the first user question and final assistant
   answer with ~15 lines of `JsonDocument` reading — no project reference to the
   EvaluationAndMonitoring executable. This demonstrates the canonical
   *production trace → eval case* pipeline.
3. Tiered assertions, cheapest first:
   - **Tier 1 — exact/contains:** plain string assertion, no model call.
   - **Tier 2 — NLP:** `F1Evaluator` / `BLEUEvaluator` from `.NLP` against the golden answer,
     still no model call.
   - **Tier 3 — judge:** `EquivalenceEvaluator` from `.Quality` (LLM call).
4. Runs execute through `.Reporting`: `DiskBasedReportingConfiguration` with **response
   caching** enabled (second run of an unchanged suite costs zero LLM calls — the CI story),
   one `ScenarioRun` per case.
5. Suite gate: any failing case ⇒ non-zero exit code. Doc mentions the
   `Microsoft.Extensions.AI.Evaluation.Console` (`aieval`) tool for HTML reports off the same
   result store.

Output: per-case pass/fail with tier and score, summary line, exit code; note showing the
cache hit on re-run.

Builds on: `EvaluationAndMonitoring` (its recorded traces are where golden cases come from;
that pattern records ground truth, this one turns it into a gate), `SelfCorrectionLoop` (same
evaluator idea, different lifecycle: pre-merge suite vs inline loop).

## Pattern 3 — TrajectoryEvaluation

**Project:** `TrajectoryEvaluation.AgentFramework`

Agent-specific evaluation: judge the *path*, not just the final answer — right tool, right
order, no redundant calls.

Flow:
1. A TechCorp `SupportAgent` with two tools (`GetSupportPolicy`, `CheckWarrantyStatus`) plus a
   deliberate distractor tool (`GetStoreLocations`).
2. Run 3 queries; capture the full trajectory (all `ChatMessage`s including
   `FunctionCallContent`/`FunctionResultContent`) from the agent thread.
3. Score each trajectory with the library's agent evaluators —
   `ToolCallAccuracyEvaluator`, `TaskAdherenceEvaluator`, `IntentResolutionEvaluator` — which
   take the message history plus tool definitions.
4. One query is engineered so the agent is tempted into the distractor tool (or a synthetic
   bad trajectory is injected if the live model behaves too well), so the metric table shows a
   contrast between a clean and a wasteful trajectory. The teaching point: final-answer evals
   miss agents that are right expensively or by luck.

Output: per-query table of the three agent metrics with the evaluators' reasoning strings.

Builds on: `EvaluationAndMonitoring` (its `TrajectoryMiddleware` counts calls and latency —
this pattern judges whether those calls were the *right* ones), `ToolUse` (the thing being
measured).

## Pattern 4 — RedTeaming

**Project:** `RedTeaming.AgentFramework`

Adversarial evaluation of your own agent: measure what the defenses actually stop.

Flow:
1. **Defended agent:** TechCorp support agent with GuardRails-style defenses (system-prompt
   rules, input filter, output scan) holding secrets it must never reveal: an internal
   discount code and its own system prompt.
2. **Attacker agent:** generates probes across 4 attack classes — direct ask, roleplay/
   persona, injection-inside-data (a "customer email" containing instructions), and encoding/
   obfuscation. ~3 probes per class, generated live.
3. Each probe runs against the defended agent; a custom `IEvaluator` judge classifies the
   response: `Leaked`, `PartialLeak`, `Refused`, `SafeAnswer`.
4. Output: attack-success-rate table per attack class + overall ASR. The point: GuardRails
   without red-teaming is a list of filters; with it, it's a measured claim.

This is authorized self-testing of the sample's own agent within the process — no external
targets, consistent with the repo's security posture.

Builds on: `GuardRails` (the defenses under test), `Debate` (adversarial two-agent
structure), `LLMAsJudge` (the scoring instrument).

## Pattern Explorer & docs integration

- Four new `PatternExplorer/patterns/*.md` write-ups in the established template (What it is /
  When to use it / How the demo works with mermaid diagram / Key APIs / What to watch in the
  output), each with the "Builds on" paragraph in "What it is".
- `EvaluationAndMonitoring.md` gets a closing cross-reference to the new category (it measures
  cost/speed and records ground truth; the Evaluation category judges quality).
- README: category sentence updated to five groups; four table rows; the "one flavor only"
  list extended.
- `Agentic Patterns.slnx`: four project entries.

## Error handling

Repo norm: samples fail fast with clear console messages (missing config throws from
`Settings`). RegressionEvals additionally returns exit code 1 on suite failure by design.
Evaluator LLM calls that fail surface as the library's diagnostics on the `EvaluationResult`;
samples print them rather than swallowing.

## Testing

- `dotnet build` for the solution.
- Each sample run manually end-to-end against the live deployment (repo has no automated
  eval-of-evals; samples are demos).
- RegressionEvals run twice to demonstrate the response cache; verified exit codes on a
  forced failure.
- Pattern Explorer picks up the new md files automatically (re-read per request); verified by
  loading the UI and seeing the "Evaluation" group.

## Implementation order

LLMAsJudge → RegressionEvals → TrajectoryEvaluation → RedTeaming (each later pattern's doc
references earlier ones), then docs/README/solution wiring, then a recorded
`sample-run-trace.json` refresh if needed.
