# Coordination physics

Why some agent architectures work and others quietly degrade. The pattern write-ups in
`PatternExplorer/patterns/` link here instead of repeating the theory; this page states each
result once, with its scope limits and its sources.

## The Data Processing Inequality

If information flows through a chain of processing stages where each stage sees only the
previous stage's output — a Markov chain `X → Y → Z` — then no processing of `Y` can recover
information about `X` that `Y` already lost: `I(X;Z) ≤ I(X;Y)` [1]. Every summarization,
extraction, or paraphrase is such a stage. Whatever it drops is unrecoverable by any downstream
cleverness, including a smarter model.

Scope, stated honestly: the DPI is a theorem only under the Markov condition. A prompt pipeline
where stage *n* receives nothing but stage *n−1*'s text output satisfies it literally — the
inequality binds. An organization, or an agent system with side channels, satisfies it only
approximately, and there the DPI is an analogy that predicts a tendency, not a bound. This page
uses it both ways and says which is which.

## The re-grounding escape hatch

The DPI stops binding the moment a stage stops being a pure function of the previous stage's
output. A tool call, a re-retrieval, or a re-read of the original input adds a second arrow into
the chain — the Markov condition is broken, and the accumulated compression loss is reset at
that stage. This is the single most useful design lever in this document:

- **Agentic RAG** beats naive RAG because the grade→rewrite→re-retrieve loop returns to the
  corpus instead of reasoning over a bad first retrieval.
- **Tool-using agents** beat pure prompt pipelines because a tool result is fresh information
  from the environment, not a further compression of prior context.
- **Compaction** is survivable when the original (files, stored history, source documents)
  remains addressable, so the agent can re-read what the summary dropped.

The design rule: it is fine to compress aggressively *if and only if* the path back to the
source stays open and the agent knows to use it.

## Strategic communication degrades dialogue channels

Crawford and Sobel showed that when a sender and receiver have even slightly divergent
incentives, equilibrium communication is coarse: the sender transmits an interval, not a point,
and no mechanism recovers the lost precision [2]. LLM agents inherit softened versions of this —
an agent prompted to defend a position, appear confident, or satisfy a critic shades what it
reports. Empirically, multi-agent LLM failures concentrate in exactly this layer:
inter-agent misalignment — ignored input, withheld information, derailed conversation — is one
of the three top-level failure categories in the MAST taxonomy of 14 recurring failure modes [3].

The implication is architectural: prefer coordination through a shared *environment* (files,
typed contracts, test results) over coordination through *dialogue*. An artifact in the
environment is the same for every reader; a message is re-encoded — and re-shaded — at every hop.

## Goodhart's Law: every measurement is a proxy

Any metric used as a target stops measuring what it was built to measure [4]. Eval suites,
LLM-as-judge scores, and self-reported confidence are all proxies, and optimization pressure —
including an agent iterating until the checker passes — finds the gap between proxy and goal.
Treat the measurement system itself as a hypothesis that incidents revise: when a passing eval
coexists with a failing product, the eval is the bug.

## The single-agent ceiling

Splitting a task across agents pays twice: each boundary is a compression step (DPI) and a
dialogue channel (Crawford–Sobel), plus coordination overhead. So distribution needs a
compensating benefit — parallelism, specialization, or a context that genuinely does not fit —
or it is net-negative. The empirical numbers agree: across 260 configurations, Kim et al. find
independent multi-agent systems amplify trace-level errors 17.2× through unchecked propagation,
versus 4.4× under centralized coordination whose validation bottleneck intercepts errors before
aggregation; on sequential planning, *every* multi-agent architecture degrades 39–70% below the
single-agent baseline, while genuinely decomposable tasks gain up to +80.8% [5]. For any task
that fits one agent's effective context, use one agent.

## Choosing a coordination mechanism

When a task genuinely exceeds one agent, escalate mechanisms in this order:

1. **Single agent** — the task fits one effective context. Stop here whenever possible.
2. **Stigmergy: shared environment + mechanical contracts** — workers coordinate through
   artifacts (a workspace, typed interfaces, a test gate), not messages. Each worker reads and
   writes the environment; nobody relays state. Dialogue cost per agent stays O(1) — in fact
   zero — where group chat pays O(n²) in pairwise channel noise.
3. **Hierarchy** — a manager for the residual only: ambiguous, cross-cutting judgment calls
   that no contract can encode. Every delegation is a compression of the manager's intent, so
   decompose at information boundaries — seams where the interface between parts is small and
   precise — and let the contracts carry everything they can.

## Mechanical beats advisory verification

A compiler error, a contract test, or a static analyzer verdict is identical no matter how many
layers it passes through — hard constraints do not degrade in transit. Advisory signals (review
comments, style guidance, self-assessed confidence) are re-summarized and re-weighted at every
hop and arrive diluted. Human review sits at the advisory end and degrades into a compliance
checkbox unless the workflow is designed against that: over 25 months of WhatsCode operation at
WhatsApp (3,000+ accepted changes), review settled into a stable equilibrium of roughly 60%
one-click accepts against 40% commandeer-and-revise, with acceptance rates spanning 9–100%
across domains — engaged review persisted, but only as part of a designed workflow, not as a
rubber stamp bolted onto the end [6]. And human judgment itself needs measuring, not assuming: the
METR randomized controlled trial found experienced open-source developers were 19% *slower* with
early-2025 AI tools while predicting a 24% speedup [7]. Spend verification budget on gates the
system cannot argue with; treat everything advisory as a proxy per Goodhart.

## References

1. T. Cover, J. Thomas. *Elements of Information Theory*, 2nd ed., ch. 2 (Data Processing
   Inequality). Wiley, 2006.
2. V. Crawford, J. Sobel. "Strategic Information Transmission." *Econometrica* 50(6), 1982.
3. M. Cemri et al. "Why Do Multi-Agent LLM Systems Fail?" [arXiv:2503.13657](https://arxiv.org/abs/2503.13657).
4. D. Manheim, S. Garrabrant. "Categorizing Variants of Goodhart's Law." [arXiv:1803.04585](https://arxiv.org/abs/1803.04585).
5. Kim et al. "Towards a Science of Scaling Agent Systems." [arXiv:2512.08296](https://arxiv.org/abs/2512.08296).
6. "WhatsCode: Large-Scale GenAI Deployment for Developer Efficiency at WhatsApp." [arXiv:2512.05314](https://arxiv.org/abs/2512.05314).
7. J. Becker, N. Rush, E. Barnes, D. Rein (METR). "Measuring the Impact of Early-2025 AI on
   Experienced Open-Source Developer Productivity." [arXiv:2507.09089](https://arxiv.org/abs/2507.09089).

*Further reading: Jeremy McEntire, "Beyond Code" — the context-engineering framing that
motivated this page.*
