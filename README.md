# agentic-patterns

A collection of agentic patterns, each implemented twice for comparison:

- **`*.SemanticKernel`** — [Semantic Kernel](https://github.com/microsoft/semantic-kernel) (the established SDK; its agent/orchestration surface is now superseded by Agent Framework for new work)
- **`*.AgentFramework`** — [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) (`Microsoft.Agents.AI`, the current recommended stack on top of `Microsoft.Extensions.AI`)

A few patterns exist in only one flavor (e.g. the reasoning techniques `ChainofThoughts`, `ReasoningAndActing`, `Reflexion`, and the Agent-Framework-only patterns `Magentic`, `Handoff`, `DurableExecution`, `DurableHumanInTheLoop`, `ContextCompaction`, `Middleware`, `AgenticRAG`, `Debate`, `CodeAct`, `ProgressiveToolDisclosure`, `ContextOffloading`, `RalphLoop`, `CacheAwareContext`, `SkillLearning`, `StigmergicCoordination`,
and the `Evaluation` patterns `LLMAsJudge`, `RegressionEvals`, `TrajectoryEvaluation`, `RedTeaming`).

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

The image contains the .NET 10 SDK, prebuilt samples, and Node.js with npm/npx. It runs as a
non-root user; the command above also binds only to loopback and drops Linux capabilities. Do
not expose Pattern Explorer directly to the internet: its run endpoints intentionally execute
samples with the supplied credentials. Enabling the two optional CodeAct variables runs generated
code directly inside this outer container instead of a nested Docker sandbox. The generated code
therefore shares the container's credentials, filesystem, and network access.

Running a sample from the UI spawns `dotnet run` for that project and calls your Azure OpenAI
deployment, exactly as running it from the terminal would. Samples that ask for approval get an
input box wired to the process's stdin; the A2A sample starts its server first automatically.

The write-ups live in `PatternExplorer/patterns/*.md` — one file per pattern, re-read on every
request, so edits show up on refresh.

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
| Debate | Opposing agents argue over rounds, a judge rules |
| ExplorationAndDiscovery | Generate → critique → evolve idea loops |
| ReasoningAndActing | ReAct-style reason/act tool loops |
| Reflexion | Episodic retry: attempt → verify → self-reflect → retry with reflections |
| SelfConsistency | Sampled reasoning paths with majority voting |
| SelfCorrectionLoop | Evaluator-Optimizer loop with typed feedback and host-enforced criteria |
| SelfNote | Margin-note taking to aid long-context answers |
| TreeOfThoughts | Branching thought exploration with pruning |
| Voting | Multi-agent voting with confidence weighting |

### Orchestration

| Pattern | What it demonstrates |
|---|---|
| CodeAct | One code-execution tool instead of many bound tools; results stay in the script |
| GoalSetting(s)AndMonitoring | Goal decomposition with progress monitoring |
| Handoff | Agents transferring the conversation to each other |
| HostedTools | Server-side code interpreter and web search tools |
| InterAgentCommunication.A2A | Agent-to-agent communication over the A2A protocol |
| MCP | Consuming Model Context Protocol tool servers |
| Magentic | Manager-driven open-ended multi-agent orchestration |
| MultiAgentCollaboration | Group-chat orchestration |
| OrchestratorWorkers | Dynamic decomposition into validated tasks for a fixed worker registry |
| Parallelization | Concurrent fan-out / fan-in over agents |
| Planning | Typed plan generation + execution |
| Prioritization | Task triage with tools |
| PromptChaining | Multi-step prompt pipelines (workflow-based in AF) |
| RalphLoop | Fresh-context agent loop until the plan file is satisfied; state lives in files |
| Routing | Intent routing to specialist agents (incl. a workflow variant) |
| StigmergicCoordination | Message-free multi-agent build coordinated via shared contracts and a compile gate |
| ToolUse | Function calling basics |

### Knowledge & state

| Pattern | What it demonstrates |
|---|---|
| AgenticRAG | Retrieval as an agent tool: query rewriting, result grading, re-retrieval |
| CacheAwareContext | Stable-prefix message layout so provider prompt caching pays for the input |
| ContextCompaction | Compaction strategies for long-running agent context |
| ContextOffloading | Bulky tool results offloaded to files, recoverable via a read-back tool |
| ExpeL | Learning insights from experience across episodes |
| LearningAndAdaptation | Rule learning across sessions |
| MemoryManagement | Isolated invocation, session, long-term, and authoritative business state |
| ProgressiveToolDisclosure | Search-then-bind tool loading instead of carrying the whole catalog |
| RAG | Retrieval-augmented generation over a vector store |
| SemanticCaching | Exact and similarity-based response caching |
| SkillLearning | Versioned candidate → validated → tested → approved → active → retired skills |

### Production controls

| Pattern | What it demonstrates |
|---|---|
| BoundedExecution | Hard per-run limits on calls, tools, tokens, elapsed time, iterations, and cost |
| ConfidenceReporting | Uncertainty Signals |
| DurableExecution | Workflow checkpointing and resume across restarts |
| DurableHumanInTheLoop | Approval gate that survives a process restart via checkpointing |
| EvaluationAndMonitoring | Telemetry plus privacy-aware model/tool record and replay |
| ExceptionHandlingAndRecovery | Retry, fallback, graceful degradation, and dependency circuit breaking |
| GuardRails | Input/output filtering, PII redaction, injection defense |
| HumanInTheLoop | Tool-call approval gates |
| IdempotentToolCalls | Retry-safe side effects: the dedup record lives with the side effect, not the caller |
| Middleware | Agent-run and function-invocation middleware (logging, latency, tool guards) |
| ResourceAwareOptimization | Model routing under a cost budget |
| ToolAuthorization | Capability-scoped, argument-level authorization before tool execution |

### Evaluation

| Pattern | What it demonstrates |
|---|---|
| LLMAsJudge | Judge-model rubric scoring plus a position-bias probe |
| RedTeaming | Attacker agent vs a defended agent, judge-scored attack-success-rate |
| RegressionEvals | Golden-dataset suite with tiered assertions, cached as a CI gate |
| TrajectoryEvaluation | Scoring the agent's tool-use path with agent evaluators |

## Setup

Requires the .NET 10 SDK and an Azure OpenAI deployment. The `CodeAct` sample additionally
requires Docker or Podman — it sandboxes the code the model writes and refuses to run
without isolation (see the security section below).

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

The A2A samples need the server running first:

```bash
dotnet run --project InterAgentCommunication.A2A.AgentFramework.Server
# in another terminal
dotnet run --project InterAgentCommunication.A2A.AgentFramework.Client
```

Package versions are managed centrally in `Directory.Packages.props`.
