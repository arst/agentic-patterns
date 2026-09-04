---
{
  "title": "Computer Use",
  "summary": "Run a bounded screenshot–reason–action–observe loop where the host applies only valid actions to currently visible controls.",
  "category": "Orchestration",
  "projects": [ { "flavor": "AgentFramework", "path": "ComputerUse.AgentFramework" } ]
}
---

## What it is

Computer-use agents operate software through the visual interface: capture the current screen,
ground an action in visible coordinates, apply mouse or keyboard input, then observe the result.
The fresh observation is essential because popups, navigation, and delayed UI updates invalidate
plans made from an older screen.

## When to use it

- A legacy or third-party application has no suitable API.
- The task depends on a rendered interface rather than structured data.
- A disposable, least-privilege desktop can contain mistakes.

Prefer a stable API when one exists. Pixels are slower, more expensive, and more brittle.

## How the demo works

The sample uses a text-serializable virtual desktop, so it is safe and repeatable without taking
control of the user's machine. A consent popup initially hides Settings. On every step the Agent
Framework operator receives only the latest screenshot and proposes one click. VirtualDesktop
rejects unsupported actions and coordinates that do not identify a visible element. The runner
captures the post-action screen and stops when dark mode is enabled or six steps are exhausted.

~~~mermaid
flowchart LR
    S[Capture current screen] --> R[Agent chooses one visible click]
    R --> G[Host validates and applies]
    G --> O[Capture observation]
    O -->|goal not met| S
    O -->|goal met or budget spent| E[Stop]
~~~

## Key APIs

- ScreenSnapshot is the only state supplied to the operator.
- VirtualDesktop.Apply enforces the allowed action vocabulary and visible-coordinate check.
- ComputerUseRunner.RunAsync owns observation and the hard step bound.

## Production boundary

This is interaction-loop logic, not a secure browser or desktop sandbox. Real computer use needs
an isolated VM or browser, restricted network and credentials, confirmation for consequential
actions, secrets redaction, timeouts, and complete audit capture. See the
[pattern catalog entry](https://agentic-design.ai/patterns/tool-use/computer-use).
