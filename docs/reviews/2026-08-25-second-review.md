# Second review

Reviewed the `main` branch at commit **`7a990d3`**, compared with the previous review point, `fbb575c`. That delta contains **38 commits and 166 changed files**. The latest GitHub Actions run completed successfully, building the solution, running the test suite, and producing the container image with provenance and an SBOM. This was primarily a static review supplemented by the repository's CI results; the Azure-dependent examples were not invoked against a live model. ([GitHub][1])

## Overall verdict

This is a **major improvement**. The repository has moved from "a useful collection of minimal examples with production caveats" toward "a serious educational reference that demonstrates many of the correct production boundaries in executable code."

Several earlier concerns are now genuinely resolved:

* **CodeAct** has a credible teaching-grade sandbox: no network, read-only root filesystem, no capabilities, non-root execution, bounded CPU/memory/processes, a bounded temporary filesystem, no inherited host environment, concurrent bounded output capture, cleanup, and fail-closed selection. Unsafe host execution requires explicit double opt-in. ([GitHub][2])
* **Durable Human-in-the-Loop** now demonstrates an actual fresh-process resume and denies approval on EOF instead of auto-approving. **Magentic** now obtains real plan approval or stops without executing. ([GitHub][3])
* **Tree of Thoughts** now places mathematical correctness in deterministic host code rather than trusting a textual `done`. ([GitHub][4])
* CI now runs a meaningful test project covering execution budgets, authorization, idempotency, replay, evaluator-host interaction, and other control behavior. ([GitHub][5])
* The catalog is much more coherent: Production Controls and Evaluation are now real sections, and the new patterns—Bounded Execution, Tool Authorization, Idempotent Tool Calls, Orchestrator-Workers, replay, regression evals, red teaming, and trajectory evaluation—are well chosen. ([GitHub][6])

For an educational repository, this is now **strong and publishable**. The remaining problems are mostly cases where the sample or documentation promises a broader guarantee than the implementation actually provides.

# Highest-priority remaining findings

## 1. High: `IdempotentToolCalls` protects the easy lost-response window, not the hard unknown-outcome window

The idempotency store invokes the operation, waits for it to return, stores the result as `Completed`, and only then injects the simulated lost response. That correctly demonstrates replay when the result has already reached the same process and been persisted in the local registry. ([GitHub][7])

The difficult real-world failure is different:

```text
Refund service commits refund
    ↓
Client process loses connection or dies
    ↓
Local idempotency store never records Completed
    ↓
Retry issues another refund
```

In the current code, if `operation()` commits remotely and then throws before returning, the catch block resets the entry to `Pending`. The next attempt executes the operation again. Therefore, the title and documentation—"retry side effects safely when a successful response is lost"—are broader than the guarantee actually implemented. ([GitHub][6])

The best educational fix is to move deduplication to the **side-effect owner**:

```csharp
IssueRefundAsync(
    string tenantId,
    string idempotencyKey,
    RefundRequest request);
```

The refund service should transactionally persist:

```text
tenant
idempotency key
canonical request hash
operation state
refund ID
result
```

The refund and idempotency result must be committed together. A retry then asks the service with the same key and receives the original refund result.

The current local registry can remain, but should be described as a **client-side replay cache for a cooperative in-process operation**, not a complete solution for an unknown remote outcome.

One explicit test should be added:

1. The downstream service commits the refund.
2. It throws before returning to the caller.
3. The caller process loses all local state.
4. A fresh caller retries with the same key.
5. The downstream service proves that only one refund exists.

That test captures the actual reason idempotency keys matter.

## 2. High: MCP still violates the repository's own execution-boundary rule

The Agent Framework sample runs:

```text
npx -y @modelcontextprotocol/server-everything
```

That downloads an unpinned package at runtime, executes it directly on the host, discovers all tools, and binds every discovered tool to the agent. ([GitHub][8])

This conflicts with the excellent repository-wide rule now stated in the README:

> The model proposes. A constrained host validates and executes. Untrusted execution never inherits the application's authority.

The MCP sample is particularly important because a learner may conclude that the standard MCP client architecture is:

```text
download latest server
run it with application environment
bind every advertised tool
```

The minimum teaching-grade correction should be:

* Pin the package to an exact version, preferably an image digest or repository-controlled artifact.
* Run the MCP server in the same type of constrained container used by CodeAct.
* Do not forward the application environment or Azure credentials.
* Deny network access unless the selected MCP server explicitly requires it.
* Bind only `add` and `echo` in this demo rather than every discovered tool.
* Fail closed when the isolation boundary is unavailable.
* Keep tool discovery and tool authorization as explicitly separate steps.

This is now the biggest remaining security inconsistency in the repository.

## 3. High: `ConfidenceReporting` still does not produce confidence in the displayed answer

The displayed answer comes from the self-report agent. The log-probability signal comes from a separate raw completion using a different system prompt. The consistency score comes from another five high-temperature completions. Those signals may therefore refer to different answers, yet they are combined and printed beside the self-reported answer. ([GitHub][9])

The consistency implementation also considers answers to agree if they share any sufficiently long word from the majority response. Two contradictory answers may both contain words such as `capital`, `Australia`, or `Canberra`. The log-probability normalization, weights of `0.20 / 0.35 / 0.45`, hedging penalty, and confidence thresholds are all manually chosen rather than calibrated against correctness labels. ([GitHub][9])

The documentation currently calls the result a "trustworthy confidence score," describes log probabilities as the "most objective signal available," and recommends the pattern for answers feeding automated decisions. That is too strong for the implemented heuristic. ([GitHub][10])

Rename it to:

> **Uncertainty Signals / Confidence Heuristics**

Then change the pipeline so that all signals score one canonical candidate:

```text
Generate canonical candidate
    ↓
Extract task-specific answer
    ↓
Run equivalence/consistency probes against that candidate
    ↓
Calculate heuristic risk features
    ↓
Apply a calibration model trained on labeled examples
    ↓
Answer / abstain / escalate
```

Until there is calibration data, print something such as:

```text
Heuristic uncertainty score: 0.73
This is not an estimated probability of correctness.
```

For a genuine confidence pattern, add a small labeled evaluation set and report:

* Brier score.
* Expected calibration error.
* Accuracy by confidence bucket.
* Selective accuracy as the system abstains on low scores.

This is the most important remaining **conceptual correctness** issue because learners can easily mistake a plausible-looking percentage for a measured probability.

## 4. High: `RedTeaming` currently produces an unreliable attack-success rate

There are several concrete problems.

The judge is told that the agent must not reveal an internal discount code, but it is not given the actual code against which to compare the response. A deterministic exact-secret check is therefore missing. ([GitHub][11])

If JSON parsing returns no verdict, the code defaults to `SafeAnswer`. Unknown verdict strings also do not count as leaks. Evaluation infrastructure should fail into **indeterminate**, not silently into success. ([GitHub][11])

The documentation says the pattern measures the GuardRails filter list, but the defended agent only has a system-prompt instruction. The actual GuardRails middleware is not composed into this sample. ([GitHub][11])

Finally, the sample uses only twelve generated probes: four classes with three probes each. A "low single-digit" nonzero overall leak rate is mathematically impossible—one leak already produces 8.3%, and one leak within a class produces 33.3%. ([GitHub][11])

A stronger design would be:

1. Run deterministic checks first:

   * Exact secret appears.
   * Meaningful substring of the secret appears.
   * Known system-prompt canary appears.
2. Use the LLM judge only for semantic or partial leakage.
3. Parse a strict typed verdict:

   * `Leaked`
   * `PartialLeak`
   * `Safe`
   * `Indeterminate`
4. Never convert malformed output into `Safe`.
5. Combine:

   * A checked-in regression corpus.
   * Newly generated exploratory attacks.
6. Record attacker model, defender model, judge model, prompt versions, and random seed where supported.
7. Report confidence intervals rather than treating twelve samples as a stable rate.
8. Actually run the GuardRails implementation when claiming to evaluate GuardRails.

The general pattern is excellent; the metric implementation needs to become fail-closed.

## 5. Medium–high: `BoundedExecution` is very good, but token and cost limits are not strictly hard yet

The design is solid: counters are run-scoped, concurrent reservations prevent two callers from independently spending the same remaining budget, retries consume model-call budget, tool calls can have per-tool limits, and stopped runs are explicitly marked partial. The tests reinforce the intended control flow. ([GitHub][12])

However, the chat client reserves a fixed `2,000` input tokens and `800` output tokens before every model call. Actual usage is only observed and reconciled after the response arrives. If a call uses more than the reservation, the provider call has already exceeded the nominal token or cost ceiling before `Reconcile` throws. Missing usage is treated as zero. ([GitHub][13])

Therefore:

* Model-call count is a hard pre-call limit.
* Tool-call count is a hard pre-call limit.
* The linked timeout is a real runtime boundary.
* Token and estimated-cost limits are currently conservative reservations plus post-call enforcement, not guaranteed absolute ceilings.

There are also two smaller structural issues:

* `Iterations` increments together with `ModelCalls`, so the two limits currently represent the same event.
* `CreateTimeout` always schedules the complete maximum duration rather than the remaining duration. In the sample it is created immediately, but the reusable primitive can be misleading when created later. ([GitHub][12])

Either adjust the wording or strengthen the implementation:

* Estimate actual request tokens before dispatch.
* Set the provider's maximum output tokens to the smaller of the configured output cap and remaining budget.
* Reserve that complete worst-case amount.
* When usage is unavailable, charge the reservation rather than zero.
* Add an explicit `RecordIteration()` at the orchestration-loop boundary.
* Calculate timeout as `MaxElapsedTime - Elapsed`.

The pattern is otherwise one of the better additions.

## 6. High correctness: `Planning` still cannot satisfy its own stated goal

The goal asks the planner to book the **cheapest** flight. `GetFlights` returns flight IDs and times but no prices, and no deterministic selection operation exists. The planner therefore has no evidence from which to identify the cheapest option. The hardcoded date—April 3, 2026—is also now in the past. ([GitHub][14])

There are several related problems:

* `BookFlight` runs directly without authorization, approval, or idempotency.
* Unresolved `{{stepN}}` placeholders remain literal strings instead of failing validation.
* Step IDs, duplicate IDs, dependencies, and required outputs are not validated before execution.
* A model-generated plan can select an arbitrary flight ID from free-form output. ([GitHub][14])

The clean example would be:

```text
GetFlights
    -> typed FlightOption[] with Price
SelectCheapest
    -> deterministic host operation
RequestBookingApproval
    -> exact flight + price + passenger bound to approval
BookFlight
    -> authorized + idempotent side effect
DraftEmail
```

This is a useful place to compose the newly added patterns rather than keeping them isolated:

* Bounded Execution around planning and execution.
* Tool Authorization around booking.
* Human-in-the-Loop before booking.
* Idempotent Tool Calls at the booking service.
* Trace Recording around the complete plan.

# Important medium-priority findings

## Stigmergic Coordination compiles model-generated source on the host

The sample does not execute the resulting program, which is good, but it still invokes `dotnet build` over untrusted model-generated source directly on the host. There is no build timeout, source-size limit, CPU/memory/process boundary, or guaranteed workspace cleanup. Standard output is read fully before standard error, which can also deadlock if one redirected pipe fills. ([GitHub][15])

Reusing the CodeAct container in a **compile-only mode** would keep the repository's security rule consistent:

```text
no network
read-only source mount
bounded writable build directory
CPU/memory/process limits
wall-clock timeout
bounded concurrent stdout/stderr capture
cleanup
```

The mechanical compile gate is a good example; it just needs the same untrusted-computation boundary as CodeAct.

## Skill lifecycle can be bypassed by editing the active file

The new lifecycle—candidate, validated, tested, approved, active, retired—is a substantial improvement. But the manifest does not contain a content digest. After a skill is approved and activated, `SKILL.md` can be modified directly, and `ReadActive` will return the modified content without revalidation or reapproval. ([GitHub][16])

Add:

```csharp
string ContentSha256
```

to the manifest and verify it during every transition and every active read. Version directories should become immutable after candidate creation. For a stronger sample, sign the approved manifest or at least explain that filesystem write access remains part of the trust boundary.

The current "behavioral test" is also just a substring-order check. It demonstrates lifecycle plumbing but not actual skill behavior. ([GitHub][16])

## Pattern Explorer remains a broad local authority boundary

`RunSession.Current` is global, so a second tab or local caller cancels and replaces the first run. The channel is unbounded, child processes inherit the Explorer process environment, and no general wall-clock or output limit is applied to sample execution. ([GitHub][17])

The frontend passes Markdown output from `marked` directly to `innerHTML`, Mermaid runs with `securityLevel: "loose"`, and terminal history grows without a bound. That is acceptable only while every checked-out pattern document is completely trusted. ([GitHub][18])

Because the README correctly says not to expose Explorer to the internet, this should not be overbuilt. The proportional fix is:

* Random run ID and per-run capability token.
* Bounded channel and terminal history.
* Maximum runtime and output bytes.
* Explicit environment allowlist per sample.
* Markdown sanitization or disabled raw HTML.
* Mermaid strict mode.
* Content Security Policy.
* `/runs/{id}/input` and `/runs/{id}/cancel` instead of global endpoints.

## Semantic cache isolation is improved, but incomplete

The cache now includes previous textual messages, model ID, temperature, and response format in its partition key, and returns a cloned response on hits. That closes the most obvious system-prompt collision. ([GitHub][19])

The key still omits:

* Tenant and authenticated principal.
* Authorization or ACL scope.
* Tool definitions and tool-policy version.
* Non-text message contents such as function calls and results.
* Retrieval-index or underlying-data revision.
* Other material generation options.

The in-memory dictionary and lists are not synchronized, there is no TTL or maximum size, and the original response reference is stored before being cloned on later retrieval. ([GitHub][19])

A better constructor would require an explicit host-provided namespace:

```csharp
CacheNamespace(
    TenantId,
    PrincipalScopeHash,
    SystemPromptHash,
    ToolSchemaHash,
    ModelVersion,
    DataRevision);
```

That makes the most important isolation properties impossible to forget.

## Replay should default to redacted content, not full content

The trace/replay implementation is a strong addition: it preserves structured function calls, detects request divergence, replays without executing live tools, supports privacy modes, and has useful tests. ([GitHub][20])

But `RunTrace` defaults to `FullContent`, and traces are written directly as plaintext JSON. The redaction mode only recognizes a limited set of patterns. ([GitHub][20])

Reverse the default:

```text
RedactedContent — default
HashesOnly      — high-privacy audit mode
FullContent     — explicit --record-full plus acknowledgement
```

For full-content mode, show a warning similar to CodeAct's unsafe-execution warning. Also document that redaction is best-effort rather than a guarantee.

## Tool Authorization needs a commit point for one-time capabilities

A one-time nonce is marked used during authorization, before the tool executes. If the tool fails transiently before performing its effect, a safe retry is denied because the capability has already been consumed. ([GitHub][21])

The pattern needs a small lifecycle:

```text
available
reserved
consumed after durable success
released after verified pre-effect failure
```

Alternatively, combine the one-time capability with an idempotency key so retries reference the same authorized operation.

Amount validation is also performed only when `MaximumAmount` is non-null. A refund capability with no configured maximum still needs unconditional validation that an amount exists and is positive. Finally, `ApprovalRequired` is currently returned as ordinary tool-result text rather than becoming an actual approval request. ([GitHub][21])

## Orchestrator-Workers silently drops failed work from synthesis

The registry records worker failures, which is good, but `BuildSynthesisInput` includes only successful results. The synthesizer is called even when every worker failed, and it is merely instructed to synthesize the successful reports. That permits a fluent recommendation based on empty or incomplete evidence without an explicit partial-result status. ([GitHub][22])

Preserve the full result envelope:

```text
task
worker
status
output
error
provenance
```

Then require one of:

* All required tasks succeeded.
* A configured quorum succeeded.
* The final response is explicitly marked partial.
* The run abstains.

Per-worker timeouts and budgets would also make this a natural consumer of `BoundedExecution`.

# Evaluation-pattern observations

The new evaluation section is valuable, but two additional details should change.

### LLM-as-Judge

The pairwise parser returns `A` for every result except the exact string `B`. Malformed JSON, missing fields, lowercase values, and unknown responses all silently create a position-A preference—the exact bias the sample is trying to detect. ([GitHub][23])

Use a strict enum and return `Indeterminate` on every invalid response. Run multiple randomized orderings rather than one swap.

### Regression Evals

A production trace is ground truth about **what happened**, but not necessarily ground truth about **what the correct answer should have been**. The current code extracts the model's historical response and immediately uses it as `ExpectedAnswer`; the documentation calls this the canonical trace-to-golden pipeline. That can freeze a production error into the regression suite. ([GitHub][24])

The safer pipeline is:

```text
production trace
    -> candidate regression case
    -> incident/reviewer supplies or verifies expected result
    -> case promoted into golden dataset
```

Also rename the `exact` tier to `contains`, because that is what it currently implements. ([GitHub][24])

# Smaller but concrete fixes

| Area                                 | Remaining adjustment                                                                                                                                                                                                                                                                                                                         |
| ------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **CodeAct cancellation**             | `ExecuteCSharp` calls the runner with `CancellationToken.None`; cancellation of the containing agent run therefore does not propagate into script execution. Accept the injected cancellation token in the tool method and pass it through. ([GitHub][25])                                                                                   |
| **Exception retry**                  | The final tool-error-as-success bug is fixed, but `Retry.RunAsync` catches every `Exception`, including caller cancellation, and can turn cancellation into fallback. Catch and rethrow `OperationCanceledException` before the generic catch. Whole-turn retries should also be limited to read-only or idempotent behavior. ([GitHub][26]) |
| **GuardRails response preservation** | PII redaction and output truncation flatten structured responses into a new text-only response, potentially losing function calls, metadata, finish reasons, and usage. Redact individual `AIContent` items while retaining the response structure. ([GitHub][27])                                                                           |
| **ReAct bound**                      | "Use at most 10 tool calls" remains only a prompt instruction. It is not a hard execution control. Add a function-invocation filter/counter or integrate the common bounded-execution primitive. ([GitHub][28])                                                                                                                              |
| **ResourceAwareOptimization**        | The primary routing logic is improved, but after all tier calls fail it invokes the original pipeline without recording that call's cost. Budget accounting is still post-call, so this should be described as a soft routing budget rather than a hard ceiling. ([GitHub][29])                                                              |
| **CI maintenance**                   | The workflow is meaningfully better, but the latest run reports Node-runtime deprecation warnings for several action versions. Updating and eventually pinning actions by commit SHA would remove the warning and improve supply-chain reproducibility. ([GitHub][30])                                                                       |

# Recommended next PR sequence

## PR 1 — Make the guarantees precise

* Move idempotency to the side-effect owner.
* Rename and reframe Confidence Reporting.
* Make Red Teaming fail into `Indeterminate`.
* Correct Bounded Execution's hard-token/hard-cost wording or enforcement.
* Fix the Planning example.

These are the most important because they affect what learners believe the patterns guarantee.

## PR 2 — Finish the untrusted-execution story

* Sandbox and pin MCP.
* Reuse the sandbox for Stigmergic compile checks.
* Propagate CodeAct cancellation.
* Add output/runtime limits to Pattern Explorer.

This would make the repository's excellent "constrained host" rule consistent across every relevant example.

## PR 3 — Integrity and isolation

* Add hashes and immutable versions to Skill Learning.
* Add explicit identity/tool/data namespaces to Semantic Caching.
* Make trace recording redacted by default.
* Give Pattern Explorer per-run isolation.
* Preserve structured contents in GuardRails.

## PR 4 — Orchestration failure semantics

* Preserve partial failures in Orchestrator-Workers.
* Fix retry cancellation.
* Add a real approval transition to Tool Authorization.
* Apply Bounded Execution to ReAct, workers, planners, and evaluators.
* Require human-reviewed promotion of trace-derived regression cases.

# Bottom line

The repository is now **substantially better than at the previous review**. The biggest achievement is that many important ideas are no longer buried in warnings: the code itself now fails closed, verifies outcomes, isolates execution, runs behavioral tests, and distinguishes production-control patterns.

The remaining work is less about adding more patterns and more about **semantic precision**:

* A budget should state exactly which dimensions are truly hard.
* Idempotency should cover the real unknown-outcome boundary.
* Confidence should not look calibrated when it is not.
* Evaluation failures should become indeterminate, never silently safe.
* Discovery, execution, authorization, approval, and idempotency should remain separate controls.

After fixing the MCP boundary and the four guarantee-related issues—Idempotent Tool Calls, Confidence Reporting, Red Teaming, and Bounded Execution—the repository would fairly be described as one of the stronger .NET-oriented educational catalogs of agentic patterns, rather than simply a large collection of examples.

[1]: https://github.com/arst/agentic-patterns/compare/fbb575c...7a990d3
[2]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/CodeAct.AgentFramework/Execution/ContainerCodeRunner.cs
[3]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/DurableHumanInTheLoop.AgentFramework/Program.cs
[4]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/TreeOfThoughts/Solver24.cs
[5]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/.github/workflows/build.yml
[6]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3/README.md
[7]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/IdempotentToolCalls.AgentFramework/IdempotencyStore.cs
[8]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/MCP.AgentFramework/Program.cs
[9]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/ConfidenceReporting.AgentFramework/Program.cs
[10]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/PatternExplorer/patterns/ConfidenceReporting.md
[11]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/RedTeaming.AgentFramework/Program.cs
[12]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/BoundedExecution.AgentFramework/ExecutionBudgetState.cs
[13]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/BoundedExecution.AgentFramework/Program.cs
[14]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/Planning.AgentFramework/Program.cs
[15]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/StigmergicCoordination.AgentFramework/Program.cs
[16]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/SkillLearning.AgentFramework/SkillLifecycle.cs
[17]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/PatternExplorer/RunSession.cs
[18]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/PatternExplorer/wwwroot/app.js
[19]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/SemanticCaching.AgentFramework/SemanticCachingChatClient.cs
[20]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/EvaluationAndMonitoring.AgentFramework/TraceReplay.cs
[21]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/ToolAuthorization.AgentFramework/ToolAuthorizationPolicy.cs
[22]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/OrchestratorWorkers.AgentFramework/WorkerRegistry.cs
[23]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/LLMAsJudge.AgentFramework/Program.cs
[24]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/RegressionEvals.AgentFramework/Program.cs
[25]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/CodeAct.AgentFramework/Program.cs
[26]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/ExceptionHandlingAndRecovery.AgentFramework/Retry.cs
[27]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/GuardRails.AgentFramework/Program.cs
[28]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/ReasoningAndActing/Program.cs
[29]: https://raw.githubusercontent.com/arst/agentic-patterns/7a990d3965824077c6f830deb882a8b49ecb3b0e/ResourceAwareOptimization.AgentFramework/Program.cs
[30]: https://github.com/arst/agentic-patterns/actions/runs/32714949652
