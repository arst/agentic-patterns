# agentic-patterns

A collection of agentic patterns as runnable .NET samples, built on two SDKs:

- **`*.SemanticKernel`** — [Semantic Kernel](https://github.com/microsoft/semantic-kernel) (the established SDK; its agent/orchestration surface is now superseded by Agent Framework for new work)
- **`*.AgentFramework`** — [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) (`Microsoft.Agents.AI`, the current recommended stack on top of `Microsoft.Extensions.AI`)

Most patterns exist in only one flavor, and everything added since the Agent Framework became the recommended stack is Agent-Framework-only — the second flavor earns its place where the two SDKs express the pattern differently, not as a matter of course. The pairs that remain (`RAG`, `Routing`, `Voting`, `MemoryManagement`, `ToolUse` and others) are the ones where the comparison is the point. Each write-up's front matter lists the flavors that pattern ships.

## Pattern Explorer

The fastest way in is the local browser app: it lists every pattern, explains what it is, when to
use it, and how the sample works (with a diagram and the source files), and runs any sample live
with its console output streamed into the page.

![The Pattern Explorer running the Middleware sample](docs/pattern-explorer.png)

```bash
dotnet run --project PatternExplorer
# then open http://localhost:5080
```

Or run the prebuilt container (published for AMD64 and ARM64):

```dotenv
# pattern-explorer.env (already ignored by git)
AzureOpenAi__Endpoint=https://<resource>.openai.azure.com/
AzureOpenAi__ApiKey=<key>
AzureOpenAi__ChatModelDeployment=<deployment>
AzureOpenAi__EmbeddingModelDeployment=<embedding-deployment>
# Mem0__ApiKey=<key> # only for the Semantic Kernel memory sample
# CodeAct inside the outer Pattern Explorer container (deliberate double opt-in):
# AGENTIC_PATTERNS_ALLOW_UNSAFE_HOST_EXECUTION=true
# AGENTIC_PATTERNS_ACKNOWLEDGE_UNSAFE_CODE_EXECUTION=I_UNDERSTAND_THIS_RUNS_UNTRUSTED_CODE_ON_MY_HOST
```

```bash
docker run --rm --init \
  --name pattern-explorer \
  --publish 127.0.0.1:5080:5080 \
  --env-file pattern-explorer.env \
  --cap-drop ALL \
  --security-opt no-new-privileges \
  --pids-limit 256 \
  --memory 2g \
  --cpus 2 \
  ghcr.io/arst/agentic-patterns:latest
# then open http://localhost:5080
```

The image contains the .NET 10 SDK and prebuilt samples. It runs as a
non-root user; the command above also binds only to loopback and drops Linux capabilities. Do
not expose Pattern Explorer directly to the internet: its run endpoints intentionally execute
samples with the supplied credentials. Enabling the two optional unsafe-execution variables
affects **two** samples, not one: `CodeAct` then runs generated code directly inside this outer
container instead of a nested Docker sandbox, and `StigmergicCoordination` likewise runs its
`dotnet build` of model-written source there. Both then share the container's credentials,
filesystem, and network access. The `MCP` sample cannot run inside this container at all: it needs
a container runtime of its own to sandbox the MCP server, and this image ships neither a Docker
client nor a daemon socket — running it here always hits the fail-closed path and exits. Run `MCP`
from a terminal with Docker installed instead.

Running a sample from the UI spawns `dotnet run` for that project and calls your Azure OpenAI
deployment, exactly as running it from the terminal would. Samples that ask for approval get an
input box wired to the process's stdin; the A2A sample starts its server first automatically.

The write-ups live in `PatternExplorer/patterns/*.md` — one file per pattern, re-read on every
request, so edits show up on refresh. The page serves them under a same-origin
Content-Security-Policy and renders their Markdown with raw HTML escaped rather than executed
(and Mermaid diagrams run in Mermaid's `strict` mode); pattern docs are repo-controlled, so this
is defence in depth rather than protection against untrusted authors.

## Patterns

Pattern Explorer groups the catalog into **Reasoning & generation**, **Orchestration**,
**Knowledge & state**, **Production controls**, and **Evaluation**.

Many write-ups carry an **Information-theoretic view** section explaining *why* the pattern
works or quietly degrades — grounded in the Data Processing Inequality, strategic-communication
theory, and Goodhart's Law. Rather than repeat that theory per pattern, the shared results live
once in **[docs/coordination-physics.md](docs/coordination-physics.md)** — the re-grounding
escape hatch behind agentic RAG, why dialogue channels lose information, the single-agent
ceiling, and when to reach for stigmergy over a manager. Start there for the reasoning that ties
the catalog together; each result states its scope limits and cites a primary source.

### Reasoning & generation

| Pattern | What it demonstrates |
|---|---|
| ChainofThoughts | Step-by-step reasoning in a single prompt |
| ChainOfVerification | Draft, then answer each check with the draft out of sight, then revise |
| Debate | Opposing agents argue over rounds, a judge rules |
| ExplorationAndDiscovery | Generate → critique → evolve idea loops |
| GraphOfThoughts | Thoughts as a host-owned DAG, so two lines can merge instead of one being pruned |
| LeastToMost | Ordered subproblems solved in sequence, earlier answers carried forward as facts |
| ProactiveClarification | Screened clarifying questions, one round, then stated assumptions |
| ReasoningAndActing | ReAct-style reason/act tool loops |
| Reflexion | Episodic retry: attempt → verify → self-reflect → retry with reflections |
| SelfConsistency | Sampled reasoning paths with majority voting |
| SelfCorrectionLoop | Evaluator-Optimizer loop with typed feedback and host-enforced criteria |
| SelfNote | Margin-note taking to aid long-context answers |
| StepBack | Name the governing principle first — with the question's numbers withheld — then apply it |
| TreeOfThoughts | Branching thought exploration with pruning |
| Voting | Multi-agent voting with confidence weighting |

### Orchestration

| Pattern | What it demonstrates |
|---|---|
| AgentRegistry | Discovery by capability with signed agent cards verified before dispatch |
| CodeAct | One code-execution tool instead of many bound tools; results stay in the script |
| ControlPlaneAsTool | One execute_capability tool; a trusted control plane picks the backend |
| EventDrivenAgents | Topic subscriptions instead of an orchestrator, with a generation-capped bus |
| GoalSetting(s)AndMonitoring | Goal decomposition with progress monitoring |
| Handoff | Agents transferring the conversation to each other |
| HostedTools | Server-side code interpreter and web search tools |
| InterAgentCommunication.A2A | Agent-to-agent communication over the A2A protocol |
| Magentic | Manager-driven open-ended multi-agent orchestration |
| MCP | Consuming Model Context Protocol tool servers, sandboxed and allowlisted |
| MixtureOfAgents | Layered proposers: layer 2 answers again having read all of layer 1 |
| MultiAgentCollaboration | Group-chat orchestration |
| OrchestratorWorkers | Dynamic decomposition into validated tasks for a fixed worker registry |
| Parallelization | Concurrent fan-out / fan-in over agents |
| Planning | Typed plan generation, validated before any step executes |
| Prioritization | Task triage with tools |
| PromptChaining | Multi-step prompt pipelines (workflow-based in AF) |
| RalphLoop | Fresh-context agent loop until the plan file is satisfied; state lives in files |
| Routing | Intent routing to specialist agents (incl. a workflow variant) |
| SpeculativeToolExecution | Read-only, free-to-discard tools started before the model asks |
| StateMachineAgent | Host-owned transition table; the model decides only within a state |
| StigmergicCoordination | Message-free multi-agent build coordinated via shared contracts and a compile gate |
| ToolUse | Function calling basics |

### Knowledge & state

| Pattern | What it demonstrates |
|---|---|
| AgenticRAG | Retrieval as an agent tool: query rewriting, result grading, re-retrieval |
| CacheAwareContext | Stable-prefix message layout so provider prompt caching pays for the input |
| ContextAssembly | Pinned-first, deduplicated, budgeted context built across sources with drop reasons |
| ContextCompaction | Compaction strategies for long-running agent context |
| ContextOffloading | Bulky tool results offloaded to files, recoverable via a read-back tool |
| ExpeL | Learning insights from experience across episodes |
| GraphRAG | Entity graph plus community summaries for questions no single chunk answers |
| LearningAndAdaptation | Rule learning across sessions |
| MemoryConsolidation | Recency/importance/relevance retrieval; ripe topics collapse into semantic facts |
| MemoryManagement | Isolated invocation, session, long-term, and authoritative business state |
| MultiSourceContextFusion | Conflicting sources resolved by trust then recency, contested fields surfaced |
| ProgressiveToolDisclosure | Search-then-bind tool loading instead of carrying the whole catalog |
| RAG | Retrieval-augmented generation over a vector store |
| SemanticCaching | Exact and similarity-based response caching |
| SkillLearning | Versioned candidate → validated → tested → approved → active → retired skills |

### Production controls

| Pattern | What it demonstrates |
|---|---|
| AgentCommunicationFaultTolerance | Retry, receiver-side dedup, dead letters, and a reconciliation pass |
| BoundedExecution | Hard per-run limits on calls, tools, and elapsed time; tokens estimated conservatively |
| ConfidenceReporting | Uncertainty signals over one canonical candidate — an uncalibrated heuristic, not a calibrated score |
| ContrastiveExplanation | Why A rather than B, with the flip condition re-run against the rule |
| DualLlm | Privileged planner never sees content; untrusted content supplies values, never control flow |
| DurableExecution | Workflow checkpointing and resume across restarts |
| DurableHumanInTheLoop | Approval gate that survives a process restart via checkpointing |
| EvaluationAndMonitoring | Telemetry plus privacy-aware model/tool record and replay |
| ExceptionHandlingAndRecovery | Retry, fallback, graceful degradation, and dependency circuit breaking |
| GuardRails | Input/output filtering, PII redaction, injection defense |
| HumanInTheLoop | Tool-call approval gates |
| HumanOnTheLoop | Autonomous by default, interruptible; silence is not consent for irreversible actions |
| IdempotentToolCalls | Retry-safe side effects: the dedup record lives with the side effect, not the caller |
| MemoryPoisoningPrevention | A write gate: untrusted sources propose, corroboration or a human publishes |
| Middleware | Agent-run and function-invocation middleware (logging, latency, tool guards) |
| ResourceAwareOptimization | Model routing under a soft, post-call cost budget |
| ToolAuthorization | Capability-scoped, argument-level authorization before tool execution; one-time grants are reserved, then committed after the effect |

### Evaluation

| Pattern | What it demonstrates |
|---|---|
| LLMAsJudge | Judge-model rubric scoring plus a position-bias probe that compares verdicts across balanced candidate orderings |
| RedTeaming | Deterministic leak checks first, judge second, against a GuardRails-style output filter |
| RegressionEvals | Golden-dataset suite of reviewed cases with tiered assertions, cached as a CI gate |
| TrajectoryEvaluation | Scoring the agent's tool-use path with agent evaluators |

## Setup

Requires the .NET 10 SDK and an Azure OpenAI deployment. Three samples additionally require a
container runtime and refuse to run without isolation, because each executes untrusted work:
`CodeAct` (model-generated code, Docker or Podman via `CodeExecutionOptions.ContainerRuntime`),
`MCP` (a third-party MCP server, Docker only), and `StigmergicCoordination` (`dotnet build` over
model-written source, Docker only — a build runs build tasks, source generators and MSBuild
targets). See the security section below.

Configuration is read from `settings/appsettings.json` (linked into every project), environment variables, and user secrets. **Don't put your API key in `appsettings.json`** — it's tracked in git. Use user secrets instead:

```bash
cd Shared
dotnet user-secrets set "AzureOpenAi:Endpoint" "https://<resource>.openai.azure.com/"
dotnet user-secrets set "AzureOpenAi:ApiKey" "<key>"
dotnet user-secrets set "AzureOpenAi:ChatModelDeployment" "<deployment>"
# needed by the RAG/SemanticCaching samples:
dotnet user-secrets set "AzureOpenAi:EmbeddingModelDeployment" "<embedding-deployment>"
```

## Run a sample

```bash
dotnet run --project ToolUse.AgentFramework
```

### Security: samples that execute model-generated code

Model-generated code is **untrusted code** — it may read files, steal credentials, use
the network, or start processes. The repository rule, applied to every pattern that
executes generated code, shell commands, or dynamically selected tools:

> **The model proposes. A constrained host validates and executes. Untrusted execution
> never inherits the application's authority.**

Concretely, for the `CodeAct` sample:

- **The sandbox is the default.** Generated code runs in a locked-down local container;
  Docker or Podman is required. On first run the sample builds the sandbox image itself
  from `CodeAct.AgentFramework/Sandbox/Dockerfile` — a pinned .NET SDK with a NuGet cache
  pre-warmed at build time, so at run time the container needs (and gets) no network.
- **Least privilege is paramount: allow nothing by default.** The container starts with
  nothing — no network, no capabilities, no root, no writable filesystem, no host
  environment, no host paths — and only what compiling and running a BCL-only script
  strictly needs is granted back (a bounded tmpfs for build artifacts and a read-only
  mount of one disposable per-run directory). Nothing is ever allowed preemptively or
  "just in case"; every grant must earn its place. The full flag-by-flag walkthrough is
  in `PatternExplorer/patterns/CodeAct.md`.
- **Execution fails closed.** No container runtime means the sample refuses to run —
  there is no silent fallback to host execution. Running generated code on the host
  requires a deliberate double opt-in (`--allow-unsafe-host-execution` plus an
  acknowledgement environment variable). Pattern Explorer's image accepts
  `AGENTIC_PATTERNS_ALLOW_UNSAFE_HOST_EXECUTION=true` instead of the CLI flag.
- **The included sandbox is a teaching boundary, not a production one.** Production
  systems should use a disposable worker, VM/microVM, or hardened sandbox service with
  no ambient credentials. Never execute model-generated code in the application process
  or on the application host.

The same rule applies to the `MCP` sample: a third-party MCP server is untrusted code too.
`@modelcontextprotocol/server-everything` is pinned at an exact version and baked into an
image at build time (with the `node:22-alpine` base pinned by digest too — a mutable base tag
would reopen the same hole one layer down), run in the same locked-down container as `CodeAct` — every flag from the
same `Shared/Sandbox` defaults, opting out of none of them (no network, no host environment or
credentials, read-only filesystem, dropped capabilities, non-root `--user 65532:65532`, bounded
pids/memory/cpu) — and only an explicit allowlist (`add`, `echo`) of its discovered tools is ever
bound to the agent — discovery and authorization are kept separate. Unlike `CodeAct`, this sample
hardcodes the `docker` CLI, so Podman is not an option for it. Build the image once before running
either flavor:

```bash
docker build -t agentic-patterns/mcp-server-everything:2025.8.18 MCP.AgentFramework/Sandbox
```

See `PatternExplorer/patterns/MCP.md` for the full walkthrough.

And to the `StigmergicCoordination` sample, whose build gate compiles model-written C#: compiling
untrusted source *is* running untrusted code, so `dotnet build` happens inside the same boundary
(no network, read-only source mount, one bounded writable tmpfs, capped cpu/memory/pids, a
wall-clock timeout, bounded output) rather than on the host. It pulls the stock
`mcr.microsoft.com/dotnet/sdk` image rather than building a repo-controlled one, so its image
provenance differs from `CodeAct`'s — the isolation flags do not. Like `CodeAct` it exits 1
without Docker unless the same double opt-in is set; see
`PatternExplorer/patterns/StigmergicCoordination.md`.

The A2A samples need the server running first:

```bash
dotnet run --project InterAgentCommunication.A2A.AgentFramework.Server
# in another terminal
dotnet run --project InterAgentCommunication.A2A.AgentFramework.Client
```

Package versions are managed centrally in `Directory.Packages.props`.
