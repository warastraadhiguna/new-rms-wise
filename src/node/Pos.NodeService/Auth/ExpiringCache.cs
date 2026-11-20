using System.Collections.Concurrent;

namespace Pos.NodeService.Auth;

/// <summary>
/// Small in-process cache: a value is reused for <c>ttl</c>, then loaded again. Null results are cached too.
/// Two requests that miss at the same time may both load; that is harmless here.
/// </summary>
public sealed class ExpiringCache<TKey, TValue>(TimeSpan ttl, TimeProvider clock) where TKey : notnull
{
    private readonly TimeSpan _ttl = ttl >= TimeSpan.Zero
        ? ttl
        : throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "TTL must not be negative.");

    private readonly ConcurrentDictionary<TKey, (TValue Value, DateTimeOffset ExpiresAt)> _entries = new();

    public async Task<TValue> GetOrLoadAsync(TKey key, Func<TKey, Task<TValue>> load)
    {
        var now = clock.GetUtcNow();
        if (_ttl > TimeSpan.Zero && _entries.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
            return entry.Value;

        var value = await load(key);
        if (_ttl > TimeSpan.Zero)
            _entries[key] = (value, now + _ttl);
        return value;
    }
}
