---
{
  "title": "Reversible Action Compensation",
  "summary": "Pair each distributed side effect with an explicit undo and compensate completed work in reverse order when a later step fails.",
  "category": "Orchestration",
  "projects": [ { "flavor": "AgentFramework", "path": "ReversibleActionCompensation.AgentFramework" } ]
}
---

## What it is

A multi-step agent workflow often crosses services that cannot share one database transaction.
Reversible action compensation treats the workflow as a saga: every forward step has an explicit
compensating action, and a failure triggers those compensations in reverse completion order.

Compensation is a new business action, not an ACID rollback. Refunding a charge may itself fail,
so that failure must be recorded and repaired or escalated.

## When to use it

- A workflow reserves, charges, books, publishes, or otherwise changes several systems.
- Earlier effects can be semantically undone when a later step fails.
- At-least-once retries need a stable workflow identity.

Do not pretend an irreversible effect has an undo. Escalate that boundary or place it after the
reversible steps with an explicit human decision.

## How the demo works

The checkout reserves inventory, charges a card, then fails to create a shipping label.
SagaRunner remembers the completed steps and invokes refund before inventory release. Stable
per-step idempotency keys are passed to both the forward and compensating actions. Replaying the
same saga ID returns the recorded result without repeating any effect.

~~~mermaid
sequenceDiagram
    participant W as SagaRunner
    participant I as Inventory
    participant P as Payments
    participant S as Shipping
    W->>I: reserve
    W->>P: charge
    W->>S: create label
    S--xW: failure
    W->>P: compensate: refund
    W->>I: compensate: release
~~~

## Key APIs

- CompensableStep pairs Apply and Compensate.
- SagaRunner.Run owns ordering, failure capture, reverse compensation, and replay deduplication.
- SagaResult distinguishes Completed, Compensated, and CompensationFailed.

## Production boundary

The sample ledger is in memory. Production work needs durable workflow state and idempotency
records stored transactionally with each side effect. It also needs retry and escalation for a
failed compensation. See the [pattern catalog entry](https://agentic-design.ai/patterns/workflow-orchestration/reversible-action-compensation).
