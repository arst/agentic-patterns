---
{
  "title": "Progressive Agent Rollout",
  "summary": "Move a candidate from shadow to canary to wider traffic only while evaluation windows remain healthy, and roll back automatically on regression.",
  "category": "Evaluation",
  "projects": [ { "flavor": "AgentFramework", "path": "ProgressiveAgentRollout.AgentFramework" } ]
}
---

## What it is

Agent changes can regress quality without crashing. Progressive rollout limits the blast radius:
first run the candidate in shadow while serving the control, then expose a small deterministic
canary, ramp it, and finally serve it to everyone. Each stage advances only after enough online
and offline evidence passes a predefined gate.

## When to use it

- Releasing a new prompt, model, tool set, retrieval strategy, or policy.
- Quality, safety, latency, or failure metrics can be compared with a stable control.
- Traffic is large enough to form meaningful evaluation windows.

For tiny workloads, an offline regression gate plus deliberate approval may provide better
evidence than pretending a handful of requests is statistically meaningful.

## How the demo works

RolloutController starts in Shadow. It always runs the candidate there but never serves its
answer. Healthy windows promote it through a 5% canary, a 25% ramp, and full traffic. Request IDs
are SHA-256 bucketed so routing is stable. A later score and failure-rate regression immediately
moves the controller to RolledBack, where the candidate no longer runs or serves.

~~~mermaid
flowchart LR
    S[Shadow<br/>0% served] -->|healthy window| C[Canary<br/>5%]
    C -->|healthy window| R[Ramp<br/>25%]
    R -->|healthy window| F[Full<br/>100%]
    S -->|regression| B[Rolled back]
    C -->|regression| B
    R -->|regression| B
    F -->|regression| B
~~~

## Key APIs

- Route returns separate RunCandidate and ServeCandidate decisions.
- Observe collects a minimum window before promotion or rollback.
- RolloutPolicy owns sample count, score-regression, failure-rate, and traffic thresholds.

## Production boundary

The demo uses simple window averages, not statistical significance, guardrail-specific metrics,
or a deployment control plane. Production gates should include confidence intervals, safety
metrics, minimum exposure time, alerting, and an audited rollback integration. See the
[pattern catalog entry](https://agentic-design.ai/patterns/evaluation-monitoring/progressive-agent-rollout).
