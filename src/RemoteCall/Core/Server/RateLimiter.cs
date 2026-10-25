namespace RemoteCall.Core.Server;

/// <summary>
/// 按 key（通常是 IP）的滑动窗口限流。
/// </summary>
public sealed class RateLimiter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _buckets = new(StringComparer.Ordinal);
    private readonly int _limit;
    private readonly TimeSpan _window;

    public RateLimiter(int limit, TimeSpan window)
    {
        _limit = Math.Max(1, limit);
        _window = window;
    }

    /// <summary>
    /// 尝试占用一次额度。失败时 <paramref name="retryAfterSeconds"/> 为建议的重试等待秒数。
    /// </summary>
    public bool TryAcquire(string key, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            if (!_buckets.TryGetValue(key, out var queue))
            {
                queue = new Queue<DateTimeOffset>();
                _buckets[key] = queue;
            }

            while (queue.Count > 0 && now - queue.Peek() > _window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= _limit)
            {
                var oldest = queue.Peek();
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((_window - (now - oldest)).TotalSeconds));
                return false;
            }

            queue.Enqueue(now);
            if (_buckets.Count > 4096)
            {
                // 防御性清理，避免大量异常 IP 撑爆字典
                foreach (var stale in _buckets.Where(kv => kv.Value.Count == 0 || now - kv.Value.Last() > _window).Select(kv => kv.Key).ToList())
                {
                    _buckets.Remove(stale);
                }
            }

            return true;
        }
    }

    public void Reset(string key)
    {
        lock (_sync)
        {
            _buckets.Remove(key);
        }
    }
}
