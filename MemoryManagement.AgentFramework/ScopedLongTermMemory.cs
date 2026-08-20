using System.Text.Json;

namespace MemoryManagement.AgentFramework;

public sealed record MemoryScope(string TenantId, string UserId);

public sealed record StoredMemory(
    MemoryScope Scope,
    string Key,
    string Value,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed class ScopedLongTermMemory
{
    private readonly Dictionary<(MemoryScope Scope, string Key), StoredMemory> _memories = [];
    private readonly Func<DateTimeOffset> _utcNow;

    public ScopedLongTermMemory(Func<DateTimeOffset>? utcNow = null) =>
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public bool Remember(MemoryScope scope, string key, string value, TimeSpan ttl, bool consent)
    {
        if (!consent) return false;
        if (string.IsNullOrWhiteSpace(scope.TenantId) || string.IsNullOrWhiteSpace(scope.UserId) ||
            string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value) || ttl <= TimeSpan.Zero)
            throw new ArgumentException("Scope, key, value, and a positive TTL are required.");
        var now = _utcNow();
        _memories[(scope, key)] = new StoredMemory(scope, key, value, now, now + ttl);
        return true;
    }

    public string? Recall(MemoryScope scope, string key)
    {
        if (!_memories.TryGetValue((scope, key), out var memory)) return null;
        if (memory.ExpiresAt > _utcNow()) return memory.Value;
        _memories.Remove((scope, key));
        return null;
    }

    public int Delete(MemoryScope scope)
    {
        var keys = _memories.Keys.Where(k => k.Scope == scope).ToArray();
        foreach (var key in keys) _memories.Remove(key);
        return keys.Length;
    }

    public string Serialize() => JsonSerializer.Serialize(_memories.Values);

    public static ScopedLongTermMemory Deserialize(string json, Func<DateTimeOffset>? utcNow = null)
    {
        var store = new ScopedLongTermMemory(utcNow);
        foreach (var memory in JsonSerializer.Deserialize<StoredMemory[]>(json) ?? [])
            store._memories[(memory.Scope, memory.Key)] = memory;
        return store;
    }
}
