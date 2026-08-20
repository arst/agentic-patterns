using System.Collections.Frozen;

namespace ToolAuthorization.AgentFramework;

public sealed record ToolCapability
{
    public ToolCapability(string subjectId, string tenantId, string toolName,
        IReadOnlyDictionary<string, string> resourceConstraints, decimal? maximumAmount,
        DateTimeOffset expiresAt, string nonce, bool oneTimeUse = false)
    {
        SubjectId = subjectId;
        TenantId = tenantId;
        ToolName = toolName;
        ResourceConstraints = resourceConstraints.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        MaximumAmount = maximumAmount;
        ExpiresAt = expiresAt;
        Nonce = nonce;
        OneTimeUse = oneTimeUse;
    }

    public string SubjectId { get; }
    public string TenantId { get; }
    public string ToolName { get; }
    public IReadOnlyDictionary<string, string> ResourceConstraints { get; }
    public decimal? MaximumAmount { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string Nonce { get; }
    public bool OneTimeUse { get; }
}
