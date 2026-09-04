---
{
  "title": "Self-Healing Operations Loop",
  "summary": "Detect an SLO breach, diagnose it, execute one policy-bounded remediation, verify recovery, and escalate anything uncertain or ineffective.",
  "category": "Production controls",
  "projects": [ { "flavor": "AgentFramework", "path": "SelfHealingOperationsLoop.AgentFramework" } ]
}
---

## What it is

A self-healing loop connects operational observation to a tightly constrained corrective action:
detect a service-level objective breach, diagnose likely cause, check the proposed remediation
against policy, execute it, then verify the service recovered. Diagnosis can be probabilistic;
authority and success criteria cannot be.

The safe loop always has an exit to a human. Low confidence, an out-of-policy action, an execution
error, or failed verification escalates instead of improvising another privileged action.

## When to use it

- Known failure modes have proven, reversible runbook actions.
- Service health has objective thresholds and fresh telemetry.
- The automation identity can be limited to a small action allowlist.

Do not automate novel migrations, data repair, or ambiguous destructive work just because a model
can name an action.

## How the demo works

Checkout p99 rises to 1900 ms after version v43 deploys, breaching a 450 ms SLO. An Agent Framework
diagnostician proposes but cannot execute a remediation. SelfHealingLoop accepts only a
high-confidence action from the host-owned allowlist, invokes one simulated rollback, and verifies
that v42 returns to 310 ms with a healthy error rate.

~~~mermaid
flowchart LR
    D[Detect SLO breach] --> G[Agent diagnoses]
    G --> P{Host policy allows<br/>action + confidence?}
    P -->|no| E[Escalate]
    P -->|yes| R[Execute one remediation]
    R --> V{Verify SLO recovered?}
    V -->|yes| C[Close and report]
    V -->|no| E
~~~

## Key APIs

- HealingPolicy owns SLOs, minimum confidence, and the exact action allowlist.
- Diagnosis is model output and carries no execution authority.
- SelfHealingLoop.Run gates remediation, catches failure, and verifies post-action health.

## Production boundary

The sample runs one synchronous action against supplied telemetry. Production needs durable
incident state, freshness checks, concurrency control, cooldowns, rollback safety, change-system
integration, and paging. Keep remediation credentials narrower than diagnostic access. See the
[pattern catalog entry](https://agentic-design.ai/patterns/fault-tolerance-infrastructure/self-healing-operations-loop).
