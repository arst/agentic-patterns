using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ToolAuthorization.AgentFramework;

/// <summary>
/// The lifecycle of a one-time capability. <see cref="Authorize"/> reserves; the host commits once
/// the effect is durable, or releases after a <em>verified</em> pre-effect failure.
/// </summary>
public enum CapabilityState { Available, Reserved, Consumed }

public sealed class ToolAuthorizationPolicy(
    IReadOnlyDictionary<string, RunPrincipal> orderOwners,
    TimeProvider? timeProvider = null)
{
    /// <summary>Tools that move money always need a present, positive, parseable amount.</summary>
    // ponytail: "which tools move money" is a hand-maintained set in the policy rather than a
    // property of the tool registration. The ceiling is a silent one: register `IssueCredit`, grant
    // it without a MaximumAmount, forget this line, and it gets no amount floor at all — absent and
    // negative amounts authorize. Upgrade path: move the flag onto the capability/tool registration
    // so adding a money tool cannot compile without declaring it.
    private static readonly HashSet<string> MoneyMovingTools = new(StringComparer.Ordinal) { "IssueRefund" };

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // ponytail: in-process nonce ledger, so a restart forgets every reservation and a crashed host
    // leaks one. Upgrade path: the same three states in the transactional store that already owns
    // the side effect, which is where the commit becomes atomic with the effect.
    private readonly ConcurrentDictionary<string, CapabilityState> _nonceStates = new(StringComparer.Ordinal);

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

        // The amount is validated whenever the tool moves money, not only when the grant happens to
        // carry a ceiling: an absent, negative, or unparseable amount is never authorizable. A
        // configured maximum only adds the ceiling on top of that.
        if (MoneyMovingTools.Contains(toolName) || capability.MaximumAmount is not null)
        {
            decimal? amount;
            try { amount = ReadDecimal(arguments, "amount"); }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return AuthorizationDecision.Deny("A valid amount is required for authorization.");
            }
            if (amount is null or <= 0)
                return AuthorizationDecision.Deny("A valid amount is required for authorization.");
            if (capability.MaximumAmount is { } maximum && amount > maximum)
                return AuthorizationDecision.RequireApproval(
                    $"Amount €{amount:F2} exceeds the capability limit of €{maximum:F2}.", toolName, arguments);
        }

        // Reserve last, so a refusal above never costs the caller a one-time capability.
        if (capability.OneTimeUse && !TryReserve(capability.Nonce))
            return AuthorizationDecision.Deny("One-time capability is already reserved or consumed.");

        return AuthorizationDecision.Allow();
    }

    /// <summary>Call once the effect is durable. Moves <c>Reserved -> Consumed</c>.</summary>
    public void Commit(string nonce) => _nonceStates.TryUpdate(nonce, CapabilityState.Consumed, CapabilityState.Reserved);

    /// <summary>
    /// Call after a <em>verified</em> pre-effect failure — the caller must know the side effect did
    /// not happen. Moves <c>Reserved -> Available</c>; a committed capability stays consumed.
    /// </summary>
    public void Release(string nonce) => _nonceStates.TryUpdate(nonce, CapabilityState.Available, CapabilityState.Reserved);

    /// <summary>Atomic <c>Available -> Reserved</c>; two racing callers cannot both win.</summary>
    private bool TryReserve(string nonce) =>
        _nonceStates.TryAdd(nonce, CapabilityState.Reserved) ||
        _nonceStates.TryUpdate(nonce, CapabilityState.Reserved, CapabilityState.Available);

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
