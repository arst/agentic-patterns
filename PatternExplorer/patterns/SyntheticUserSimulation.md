---
{
  "title": "Synthetic User Simulation",
  "summary": "Let diverse personas drive bounded multi-turn conversations against an agent, then turn discovered failures into regression cases.",
  "category": "Evaluation",
  "projects": [ { "flavor": "AgentFramework", "path": "SyntheticUserSimulation.AgentFramework" } ]
}
---

## What it is

Static prompts miss failures that emerge only after several turns. A synthetic user is another
agent given a goal and behavior—impatient, confused, adversarial, or goal-shifting—and allowed to
react to the target agent's latest response. Diverse simulations expose context loss, policy
drift, hallucination, and brittle recovery before real users do.

## When to use it

- Conversation state and follow-up behavior matter.
- Real interaction data is scarce, sensitive, or arrives too late.
- Red-team and regression suites need candidate scenarios to review.

Synthetic users are generators, not ground truth. Their findings need deterministic checks,
independent judges, or human review.

## How the demo works

SimulationHarness alternates one persona move with one target-agent response. The simulator sees
the full transcript on every turn, while the support agent keeps its own Agent Framework session.
An impatient customer and an adversarial caller each drive a scenario. The host—not either
model—enforces the three-turn limit.

~~~mermaid
sequenceDiagram
    participant H as SimulationHarness
    participant U as Synthetic user agent
    participant T as Target support agent
    H->>U: persona + transcript
    U-->>H: next user move
    H->>T: user message
    T-->>H: response
    H->>U: updated transcript
    Note over H: stop signal or hard turn limit
~~~

## Key APIs

- Persona separates the user's goal from behavioral pressure.
- SimulationHarness.RunAsync owns alternation, cancellation, and the hard turn budget.
- AgentSession preserves the target agent's multi-turn context.

## Production boundary

The sample prints transcripts; it does not declare them failures automatically. A real pipeline
adds persona diversity, seeded reproducibility, privacy controls, rubric or invariant checks, and
a reviewed path from discovered failure to a golden regression case. See the
[pattern catalog entry](https://agentic-design.ai/patterns/evaluation-monitoring/synthetic-user-simulation).
