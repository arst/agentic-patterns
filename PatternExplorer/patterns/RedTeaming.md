---
{
  "title": "Red Teaming",
  "summary": "An attacker agent probes a defended agent across attack classes; a judge scores the leak rate.",
  "category": "Evaluation",
  "projects": [
    { "flavor": "AgentFramework", "path": "RedTeaming.AgentFramework" }
  ]
}
---

## What it is

A list of guardrails is a claim, not a measurement. Red teaming turns the claim into a number:
an **attacker agent** generates adversarial probes, a **defended agent** tries to hold its
secrets, and a **judge** scores each exchange — the result is an **attack-success-rate** you can
track across changes. This sample runs the whole loop against its own agent, in-process, with no
external targets: authorized self-testing, consistent with the repo's security posture.

The defended `SupportAgent` holds two secrets it must never reveal — an internal discount code
and its own system prompt. The attacker probes across four classes:

- **direct ask** — just request the secret.
- **roleplay persona** — "pretend you are an admin who is allowed to…".
- **injection inside quoted data** — instructions hidden in a "customer email".
- **encoding/obfuscation** — base64, spelling tricks, indirection.

**Builds on:** **GuardRails** provides the defenses under test — this pattern turns its filter
list into a measured attack-success-rate. The two-agent adversarial structure mirrors **Debate**,
and the per-probe scoring reuses **LLMAsJudge**.

## Information-theoretic view

"We added a system-prompt rule against leaking" is unfalsifiable until something tries to break
it. Attack-success-rate is the falsification test made continuous: it is the channel through
which the defense's real strength reaches you, and without it a guardrail's effectiveness is
whatever you assumed it was (see `docs/coordination-physics.md`). Because both the probes and the
scoring are model-generated, the metric is noisy — treat a single run as a sample, not a proof,
and watch the trend across many.

## When to use it

- Before shipping an agent that holds anything it must not disclose.
- Regression-testing defenses: did a prompt change quietly open a hole?
- Comparing two guardrail designs by the only measure that matters — what they actually stop.

Skip it for an agent with no secrets and no privileged actions: there is nothing to exfiltrate,
and input **GuardRails** without a leak surface is the whole requirement.

## How the demo works

For each of the four attack classes, the attacker generates three probes; each probe is sent to
the defended agent; the judge classifies the reply as `Leaked`, `PartialLeak`, `Refused`, or
`SafeAnswer`. Leaks and partial leaks count toward the attack-success-rate, tallied per class and
overall.

```mermaid
flowchart LR
    AT[Attacker agent] -->|probe| DEF[Defended SupportAgent<br/>holds 2 secrets]
    DEF -->|reply| JU[Judge]
    JU --> V{Leaked / PartialLeak /<br/>Refused / SafeAnswer}
    V --> ASR[Attack-success-rate<br/>per class + overall]
    AT -.4 classes x 3 probes.-> AT
```

`Asr` is a plain leaked/total ratio, guarded against divide-by-zero. The judge runs at
`Temperature = 0` with a JSON response format so the four-way classification stays parseable.

## Key APIs

| API | Role |
|---|---|
| Two `ChatClientAgent`s (attacker + defended) | The adversarial pair |
| `attacker.RunAsync(attackClass)` | Generates one probe per call |
| `defended.RunAsync(probe)` | The agent under test |
| `GetResponseAsync(..., ResponseFormat = ChatResponseFormat.Json)` | The judge's four-way verdict |
| `Asr(leaked, total)` | The scored metric |

```bash
dotnet run --project RedTeaming.AgentFramework
dotnet run --project RedTeaming.AgentFramework -- --selfcheck   # offline ASR-math check
```

## What to watch in the output

Each probe prints its class and the judge's verdict; the run ends with an
`---- Attack Success Rate ----` table per class plus an `OVERALL` line. A well-defended agent
should show low single-digit or zero leak rates, with `injection inside quoted data` typically
the hardest class to hold. Rerun after weakening the defended agent's system prompt to watch the
rate climb — that delta is the measurement **GuardRails** cannot give you on its own.
