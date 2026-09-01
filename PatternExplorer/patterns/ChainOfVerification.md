---
{
  "title": "Chain of Verification",
  "summary": "Draft, plan the checks, answer each one with the draft out of sight, then revise against what came back.",
  "category": "Reasoning & generation",
  "projects": [
    { "flavor": "AgentFramework", "path": "ChainOfVerification.AgentFramework" }
  ]
}
---

## What it is

Answer first, then check the answer — but check it somewhere the answer cannot be seen.

That last clause is the whole pattern. Asking a model to review its own output in the same
context gets you the same output with more confidence: the draft is right there, every token of
it conditioning the review, and "are you sure?" is a question the model answers by re-reading
what it just wrote. Chain of Verification breaks that loop structurally. The draft is decomposed
into individual claims; each claim becomes a narrow question; each question is answered by a
fresh call that has never seen the draft. Only then are the two put side by side.

The difference from **SelfCorrectionLoop** is what does the checking. There, an evaluator agent
judges the whole output against criteria — a better critic, but still a critic reading the thing
it is critiquing. Here the checker is not judging anything; it is answering "in what year was
Cologne founded?" with no idea that a draft exists, let alone what it claimed. Agreement between
two independent measurements means something. Agreement between a claim and a review of that
claim mostly means the review read the claim.

## When to use it

- Factual output with many small, separately checkable specifics — dates, names, figures,
  citations. The more independent claims, the more this pays.
- Anywhere a confident wrong detail is worse than a hedge: briefing notes, summaries of source
  material, anything a person will quote onward.
- When you can afford the calls. This is 1 draft + 1 planning + N verification + 1 revision.
  For a four-city question that is around eight calls for one answer.

Skip it when the claims are not separable (an opinion, a piece of code, a plan — you cannot
verify "the third paragraph" independently of the second), and skip it when the model's own
uncertainty is already the signal you need, where **ConfidenceReporting** costs one call instead
of eight.

## How the demo works

The question — four European cities founded as Roman settlements, with Roman names and founding
years — is chosen because it invites exactly the failure this pattern catches: plausible,
specific, confidently wrong dates.

Four stages, of which only the third is unusual:

1. **Draft.** One agent, told never to hedge, produces the answer with all its specifics.
2. **Plan.** A planner splits the draft into claims. Each claim carries a `value` — the part
   that could be wrong — and a question that checks it. The prompt is explicit: ask *"In what
   year was X founded?"*, never *"Was X founded in 38 BC?"*.
3. **Verify, in isolation.** A separate `Verifier` agent answers each question in its own
   stateless run — no session, no draft, no siblings. The questions run concurrently because
   they are genuinely independent; that independence is the point, and the parallelism is a
   free consequence of it.
4. **Revise.** The reviser sees the draft and the answers together, and is told which wins:
   verification. Without that instruction models defend their drafts.

Between 2 and 3 sits the host's contribution, `VerificationGate`. Models drift toward leading
questions — it is the natural way to phrase a check — and a question containing the drafted
value re-anchors the verifier on the very number under suspicion, turning an independent
measurement back into a request for agreement. The gate tokenises the claim's value and the
question and rejects the question if every token of the value appears in it. Token-level, not
substring: `38 BC` must be caught inside `AD 38 BC-era`, while a question that merely mentions
*BC* is fine.

```mermaid
flowchart TB
    Q[Question] --> D[Drafter]
    D --> Draft[Draft with specifics]
    Draft --> P[Planner: claims + questions]
    P --> G{VerificationGate<br/>does the question<br/>leak the value?}
    G -->|leaks| X[Dropped]
    G -->|clean| V1[Verifier run 1]
    G -->|clean| V2[Verifier run 2]
    G -->|clean| V3[Verifier run N]
    V1 --> R[Reviser]
    V2 --> R
    V3 --> R
    Draft --> R
    R --> F[Verified answer + change list]
```

## Key APIs

- `new ChatClientAgent(client, name:, instructions:)` — four agents, one per stage. Statelessness
  is doing real work here: the `Verifier` cannot leak the draft into a verification run because
  no session ever connects them.
- `agent.RunAsync<VerificationPlan>(...)` — structured output for the claim/question extraction.
- `Task.WhenAll(checks.Select(...))` over the verifier — independent questions, so concurrent
  runs, with `agent.RunAsync(question, options:)` per check.
- `VerificationGate.Validate(claim, question)` — the host's screen. Returns reasons, not a bool,
  so a dropped question prints why it was dropped.

## What to watch in the output

`=== Draft ===` first, with its confident dates. Then the gate: any line starting `[gate] claim N
question rejected` is the planner having written a leading question, which is common enough that
seeing zero of them across a run is the surprising outcome. `=== N verification questions passed
the gate ===` lists each question next to what the draft claimed, which is the clearest view of
what is about to be tested.

The section worth reading closely is `=== Independent answers ===`. Compare each to the
`draft says:` value above it — this is where the pattern either earns its calls or does not.
Then `=== Verified answer ===`, whose `Changes:` list is the actual deliverable: it names what
the draft got wrong. An empty change list means the draft was right, which is a real and
useful result rather than a wasted run.

**SelfCorrectionLoop** is the same instinct with a judging evaluator rather than independent
re-measurement; **Voting** and **SelfConsistency** get independence from sampling the same
question many times instead of decomposing it.
