namespace Steamy.Services;

/// <summary>Bounded, session-only backoff for valid public responses without metadata.</summary>
internal sealed class RecentMetadataMisses(TimeProvider clock)
{
    private const int Capacity = 256;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly Dictionary<int, DateTimeOffset> _expires = new();
    internal bool Contains(int appId)
    {
        lock (_gate)
        {
            if (!_expires.TryGetValue(appId, out var expiry)) return false;
            if (expiry > clock.GetUtcNow()) return true;
            _expires.Remove(appId); return false;
        }
    }
    internal void Remember(int appId)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            foreach (var entry in _expires.Where(entry => entry.Value <= now).ToArray()) _expires.Remove(entry.Key);
            if (_expires.Count >= Capacity && !_expires.ContainsKey(appId))
                _expires.Remove(_expires.MinBy(entry => entry.Value).Key);
            _expires[appId] = now + Lifetime;
        }
    }
    internal void Clear() { lock (_gate) _expires.Clear(); }
}
