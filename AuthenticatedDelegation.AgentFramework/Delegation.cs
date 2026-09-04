using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuthenticatedDelegation.AgentFramework;

public sealed record DelegationGrant(
    string Id,
    string User,
    string Agent,
    string Audience,
    string[] Capabilities,
    string Resource,
    decimal MaxAmount,
    DateTimeOffset NotBefore,
    DateTimeOffset ExpiresAt,
    string Signature = "");

public sealed record ActionRequest(
    string RequestId,
    string Agent,
    string Audience,
    string Capability,
    string Resource,
    decimal Amount,
    DelegationGrant Grant);

public sealed record AuthorizationDecision(bool Allowed, string Reason);

public sealed record AuditEntry(
    string RequestId,
    string User,
    string Agent,
    string Capability,
    bool Allowed,
    string Reason);

public sealed class DelegationAuthority
{
    private readonly byte[] key;

    public DelegationAuthority(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 32)
            throw new ArgumentException("Signing keys must contain at least 32 bytes.", nameof(key));
        this.key = [.. key];
    }

    public DelegationGrant Issue(DelegationGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (grant.ExpiresAt <= grant.NotBefore || grant.Capabilities.Length == 0)
            throw new ArgumentException("The grant must have a valid lifetime and at least one capability.", nameof(grant));

        return grant with { Signature = Sign(grant) };
    }

    public bool Verify(DelegationGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(grant.Signature),
                Convert.FromHexString(Sign(grant)));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private string Sign(DelegationGrant grant)
    {
        // ponytail: HMAC keeps this sample dependency-free; production delegation should use
        // short-lived OAuth/OIDC tokens, asymmetric keys, rotation, and revocation.
        var payload = JsonSerializer.Serialize(new
        {
            grant.Id,
            grant.User,
            grant.Agent,
            grant.Audience,
            Capabilities = grant.Capabilities.Order(StringComparer.Ordinal).ToArray(),
            grant.Resource,
            grant.MaxAmount,
            grant.NotBefore,
            grant.ExpiresAt
        });
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
    }
}

public sealed class DelegatedResourceServer
{
    private readonly DelegationAuthority authority;
    private readonly string audience;

    public DelegatedResourceServer(DelegationAuthority authority, string audience)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        this.authority = authority;
        this.audience = audience;
    }

    public List<AuditEntry> AuditLog { get; } = [];

    public AuthorizationDecision Authorize(ActionRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var grant = request.Grant;
        var reason = !authority.Verify(grant) ? "invalid signature"
            : now < grant.NotBefore || now >= grant.ExpiresAt ? "grant expired or not active"
            : !string.Equals(grant.Audience, audience, StringComparison.Ordinal) ||
              !string.Equals(request.Audience, audience, StringComparison.Ordinal) ? "wrong audience"
            : !string.Equals(request.Agent, grant.Agent, StringComparison.Ordinal) ? "agent identity mismatch"
            : !grant.Capabilities.Contains(request.Capability, StringComparer.Ordinal) ? "capability not delegated"
            : !string.Equals(request.Resource, grant.Resource, StringComparison.Ordinal) ? "resource not delegated"
            : request.Amount < 0 || request.Amount > grant.MaxAmount ? "amount exceeds delegation"
            : "authorized";

        var decision = new AuthorizationDecision(reason == "authorized", reason);
        AuditLog.Add(new(request.RequestId, grant.User, request.Agent, request.Capability, decision.Allowed, reason));
        return decision;
    }
}
