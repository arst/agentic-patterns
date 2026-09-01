using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentRegistry.AgentFramework;

/// What a peer publishes about itself. Everything except `Signature` is signed.
public sealed record AgentCard(
    string Name,
    string Endpoint,
    string[] Capabilities,
    DateTimeOffset ExpiresAt,
    string Signature = "")
{
    /// Canonical bytes to sign: field order fixed here, not by JSON property order, so a peer
    /// that reserialises the card with different formatting still verifies.
    public string Canonical() =>
        JsonSerializer.Serialize(new object[]
            { Name, Endpoint, Capabilities.Order(StringComparer.Ordinal), ExpiresAt.ToUnixTimeSeconds() });
}

public sealed record DiscoveryResult(AgentCard? Card, string? RejectedBecause)
{
    public bool Found => Card is not null;
}

/// Discovery with the verification step that makes it safe.
///
/// "Find an agent that can do X" is the easy half. The half that decides whether this is a
/// feature or a hole is what happens between finding a card and sending it work: an unverified
/// registry is a directory of anything anyone published, and dispatching to it hands your task -
/// and whatever context rides with it - to a name that claimed a capability.
///
/// So: signature first, expiry second, capability third, and only then an endpoint. A card that
/// fails any of them is not "degraded", it is not used.
public sealed class Registry(byte[] signingKey)
{
    readonly List<AgentCard> cards = [];

    public AgentCard Publish(AgentCard card) => Add(card with { Signature = Sign(card, signingKey) });

    /// For the tampering demo: publishes a card exactly as given, signature and all.
    public AgentCard PublishRaw(AgentCard card) => Add(card);

    AgentCard Add(AgentCard card)
    {
        cards.Add(card);
        return card;
    }

    public IReadOnlyList<DiscoveryResult> Discover(string capability, DateTimeOffset now)
    {
        var matches = cards.Where(c =>
            c.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase)).ToList();

        return [.. matches.Select(card => Verify(card, now))];
    }

    public DiscoveryResult Verify(AgentCard card, DateTimeOffset now)
    {
        if (!CryptographicOperations.FixedTimeEquals(Decode(card.Signature),
                Convert.FromBase64String(Sign(card, signingKey))))
            return new DiscoveryResult(null, $"'{card.Name}': signature does not verify");

        if (card.ExpiresAt <= now)
            return new DiscoveryResult(null, $"'{card.Name}': card expired at {card.ExpiresAt:u}");

        return new DiscoveryResult(card, null);
    }

    /// A malformed signature is a failed signature, not an exception: the card is attacker-shaped
    /// input and every path through here must end in accept-or-reject.
    static byte[] Decode(string signature)
    {
        var buffer = new byte[signature.Length];
        return Convert.TryFromBase64String(signature, buffer, out var written) ? buffer[..written] : [];
    }

    // ponytail: HMAC with one shared registry key - enough to show sign/verify without a PKI.
    // A real registry signs per-agent with asymmetric keys and publishes a JWKS, so a compromised
    // consumer cannot mint cards; swap Sign/Verify for that when peers stop trusting each other.
    static string Sign(AgentCard card, byte[] key) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(card.Canonical())));
}
