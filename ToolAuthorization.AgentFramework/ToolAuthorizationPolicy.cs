using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ToolAuthorization.AgentFramework;

public sealed class ToolAuthorizationPolicy(
    IReadOnlyDictionary<string, RunPrincipal> orderOwners,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, byte> _usedNonces = new(StringComparer.Ordinal);

    public AuthorizationDecision Authorize(RunPrincipal principal, ToolCapability capability,
        string toolName, AIFunctionArguments arguments)
    {
        if (principal.SubjectId != capability.SubjectId || principal.TenantId != capability.TenantId)
            return AuthorizationDecision.Deny("Capability subject or tenant does not match the authenticated caller.");
        if (!string.Equals(toolName, capability.ToolName, StringComparison.Ordinal))
            return AuthorizationDecision.Deny("Capability does not grant this exact tool.");
        if (_timeProvider.GetUtcNow() >= capability.ExpiresAt)
            return AuthorizationDecision.Deny("Capability has expired.");
        var orderId = ReadString(arguments, "orderId");
        if (toolName is "GetOrder" or "UpdateShippingAddress" or "IssueRefund" && orderId is null)
            return AuthorizationDecision.Deny("A valid orderId is required for authorization.");
        if (orderId is not null)
        {
            if (!orderOwners.TryGetValue(orderId, out var owner) || owner != principal)
                return AuthorizationDecision.Deny("Order does not belong to the authenticated customer.");
            if (capability.ResourceConstraints.TryGetValue("orderId", out var allowedOrder) &&
                !string.Equals(orderId, allowedOrder, StringComparison.OrdinalIgnoreCase))
                return AuthorizationDecision.Deny("Order is outside the capability resource scope.");
        }

        if (capability.MaximumAmount is { } maximum)
        {
            decimal? amount;
            try { amount = ReadDecimal(arguments, "amount"); }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return AuthorizationDecision.Deny("A valid amount is required for authorization.");
            }
            if (amount is null or <= 0)
                return AuthorizationDecision.Deny("A valid amount is required for authorization.");
            if (amount > maximum)
                return AuthorizationDecision.RequireApproval($"Amount €{amount:F2} exceeds the capability limit of €{maximum:F2}.");
        }

        if (capability.OneTimeUse && !_usedNonces.TryAdd(capability.Nonce, 0))
            return AuthorizationDecision.Deny("One-time capability has already been used.");

        return AuthorizationDecision.Allow();
    }

    private static string? ReadString(AIFunctionArguments arguments, string name) =>
        arguments.TryGetValue(name, out var value) ? value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString()?.Trim().ToUpperInvariant(),
            null => null,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim().ToUpperInvariant()
        } : null;

    private static decimal? ReadDecimal(AIFunctionArguments arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value) || value is null) return null;
        if (value is JsonElement json && json.TryGetDecimal(out var jsonValue)) return jsonValue;
        return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }
}
