using System.Text.Json;

namespace ControlPlaneAsTool.AgentFramework;

public sealed record Backend(
    string Capability,
    string System,
    string[] RequiredFields,
    Func<IReadOnlyDictionary<string, string>, string> Handler);

public sealed record CapabilityResult(bool Ok, string Payload, string? Backend = null);

/// One tool faces the model; the routing table faces nobody.
///
/// Bind twelve search tools to an agent and you have shipped twelve tool descriptions into every
/// prompt, twelve names the model can confuse, and a tool list that changes whenever a backend
/// is added. Bind `execute_capability` instead and the model chooses a *capability* - a word from
/// a short, stable vocabulary - while a trusted control plane decides which system serves it.
///
/// The security property matters as much as the token one: the model cannot name a backend it
/// was never told about, so a prompt-injected "query the payroll database" has nothing to bind to.
public sealed class ControlPlane(IEnumerable<Backend> backends, IReadOnlySet<string> grantedCapabilities)
{
    readonly Dictionary<string, Backend> byCapability =
        backends.ToDictionary(b => b.Capability, StringComparer.OrdinalIgnoreCase);

    public List<string> AuditLog { get; } = [];

    /// The capability names the model is allowed to see. Everything else about the estate -
    /// system names, endpoints, credentials - stays on this side of the boundary.
    public IReadOnlyList<string> Vocabulary =>
        [.. byCapability.Keys.Where(grantedCapabilities.Contains).Order()];

    public CapabilityResult Execute(string capability, string requestJson)
    {
        if (!byCapability.TryGetValue(capability, out var backend))
            return Deny(capability, $"unknown capability '{capability}'");

        if (!grantedCapabilities.Contains(capability))
            return Deny(capability, $"capability '{capability}' is not granted to this caller");

        Dictionary<string, string>? request;
        try
        {
            request = JsonSerializer.Deserialize<Dictionary<string, string>>(
                string.IsNullOrWhiteSpace(requestJson) ? "{}" : requestJson);
        }
        catch (JsonException ex)
        {
            return Deny(capability, $"request is not a JSON object: {ex.Message}");
        }

        request ??= [];
        var missing = backend.RequiredFields.Where(f => !request.ContainsKey(f)).ToList();
        if (missing.Count > 0)
            return Deny(capability, $"missing required field(s): {string.Join(", ", missing)}");

        AuditLog.Add($"{capability} -> {backend.System}");
        return new CapabilityResult(true, backend.Handler(request), backend.System);
    }

    CapabilityResult Deny(string capability, string reason)
    {
        AuditLog.Add($"{capability} -> DENIED ({reason})");
        // The model is told it failed and why, but never which systems exist.
        return new CapabilityResult(false, $"Denied: {reason}.");
    }
}
