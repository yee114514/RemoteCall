using RemoteCall.Core.Config;

namespace RemoteCall.Core.Auth;

/// <summary>
/// 一条持久化的登录会话。
/// </summary>
public sealed class SessionRecord
{
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string LastIp { get; set; } = "";
}

internal sealed class SessionFile
{
    public List<SessionRecord> Sessions { get; set; } = new();
}

/// <summary>
/// 会话存储：只保存令牌的 SHA256，重启后仍然有效。
/// </summary>
public sealed class SessionStore
{
    private const int MaxSessions = 500;

    private readonly object _lock = new();
    private readonly string _path;
    private readonly Action<string>? _log;
    private readonly List<SessionRecord> _sessions;

    public SessionStore(string path, Action<string>? log = null)
    {
        _path = path;
        _log = log;
        _sessions = AtomicJsonFile.Load<SessionFile>(path, log)?.Sessions ?? new List<SessionRecord>();
        Sweep();
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>
    /// 创建会话，返回给客户端的明文令牌。
    /// <para><paramref name="lifetime"/> 传 <see cref="TimeSpan.MaxValue"/> 表示永不过期（「不限制」）。</para>
    /// </summary>
    public (string Token, DateTimeOffset ExpiresAt) Create(TimeSpan lifetime, string ip)
    {
        var now = DateTimeOffset.UtcNow;
        var token = PasswordHasher.CreateSessionToken();
        var record = new SessionRecord
        {
            TokenHash = PasswordHasher.HashToken(token),
            CreatedAt = now,
            ExpiresAt = lifetime == TimeSpan.MaxValue ? DateTimeOffset.MaxValue : now.Add(lifetime),
            LastIp = ip,
        };

        lock (_lock)
        {
            _sessions.RemoveAll(s => s.ExpiresAt <= now);
            while (_sessions.Count >= MaxSessions)
            {
                _sessions.RemoveAt(0);
            }

            _sessions.Add(record);
            SaveLocked();
        }

        return (token, record.ExpiresAt);
    }

    /// <summary>
    /// 校验令牌是否有效；有效时返回记录。
    /// </summary>
    public bool TryValidate(string? token, out SessionRecord? record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var hash = PasswordHasher.HashToken(token);
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            for (var i = _sessions.Count - 1; i >= 0; i--)
            {
                if (_sessions[i].ExpiresAt <= now)
                {
                    _sessions.RemoveAt(i);
                }
            }

            record = _sessions.FirstOrDefault(s => string.Equals(s.TokenHash, hash, StringComparison.Ordinal));
            return record != null;
        }
    }

    public void Remove(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var hash = PasswordHasher.HashToken(token);
        lock (_lock)
        {
            var removed = _sessions.RemoveAll(s => string.Equals(s.TokenHash, hash, StringComparison.Ordinal));
            if (removed > 0)
            {
                SaveLocked();
            }
        }
    }

    /// <summary>
    /// 清除所有会话（改密码后调用）。
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (_sessions.Count == 0)
            {
                return;
            }

            _sessions.Clear();
            SaveLocked();
        }
    }

    /// <summary>
    /// 清理过期会话。
    /// </summary>
    public void Sweep()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var removed = _sessions.RemoveAll(s => s.ExpiresAt <= now);
            if (removed > 0)
            {
                SaveLocked();
            }
        }
    }

    private void SaveLocked()
    {
        AtomicJsonFile.Save(_path, new SessionFile { Sessions = _sessions }, _log);
    }
}
