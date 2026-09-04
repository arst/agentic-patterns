---
{
  "title": "Authenticated Delegation",
  "summary": "Bind agent identity to a signed, short-lived, audience- and intent-scoped grant that the resource server verifies and audits.",
  "category": "Production controls",
  "projects": [ { "flavor": "AgentFramework", "path": "AuthenticatedDelegation.AgentFramework" } ]
}
---

## What it is

An agent acting for a user needs more than a bearer credential with broad account access.
Authenticated delegation combines a verifiable agent identity with a short-lived grant constrained
to the intended audience, capability, resource, amount, and lifetime. The resource server verifies
those constraints itself and attributes every attempt to both user and agent.

This complements Tool Authorization. That pattern enforces a capability inside one host;
authenticated delegation carries narrow authority across a service boundary.

## When to use it

- An agent calls payments, healthcare, enterprise, or other protected services for a user.
- Multiple agents or services must preserve the delegation chain.
- Audit records must answer who delegated what to which agent.

## How the demo works

DelegationAuthority issues an HMAC-signed teaching grant for one agent to create a payment for one
invoice, up to EUR 100, at one audience, for five minutes. DelegatedResourceServer verifies the
signature and every constraint before authorizing EUR 75, refuses EUR 125, and records both
decisions with the user and agent identities.

~~~mermaid
flowchart LR
    U[Authenticated user] --> I[Delegation authority]
    I -->|signed narrow grant| A[Named agent]
    A -->|request + grant| R[Resource server]
    R --> V[Verify signature, audience,<br/>identity, scope, resource,<br/>amount, time]
    V -->|allow or deny| L[Attributable audit log]
~~~

## Key APIs

- DelegationGrant is immutable signed authority, including agent and user identities.
- DelegationAuthority signs a canonical payload and verifies with fixed-time comparison.
- DelegatedResourceServer.Authorize rechecks the concrete request and logs every outcome.

## Production boundary

HMAC keeps the sample dependency-free, but it makes issuer and verifier share a secret. Production
systems should use standard OAuth/OIDC delegation with asymmetric proof, issuer and audience
validation, key rotation, revocation, replay protection, and secure workload identity. See the
[pattern catalog entry](https://agentic-design.ai/patterns/security-privacy/authenticated-delegation).
