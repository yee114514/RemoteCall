namespace RemoteCall.Core.Server;

/// <summary>
/// 服务运行状态（对 UI 只读展示）。
/// </summary>
public sealed class ServerStatus
{
    private readonly object _sync = new();
    private StatusSnapshot _snapshot = new();

    /// <summary>状态更新后触发（在更新线程上，UI 需要自行切线程）。</summary>
    public event EventHandler? Updated;

    public void Update(Action<MutableStatus> mutate)
    {
        lock (_sync)
        {
            var mutable = new MutableStatus
            {
                LanEnabled = _snapshot.LanEnabled,
                LanRunning = _snapshot.LanRunning,
                LanPort = _snapshot.LanPort,
                LanError = _snapshot.LanError,
                AdminRunning = _snapshot.AdminRunning,
                AdminPort = _snapshot.AdminPort,
                AdminError = _snapshot.AdminError,
                LanUrls = _snapshot.LanUrls,
                RequestCount = _snapshot.RequestCount,
                ActiveConnections = _snapshot.ActiveConnections,
                LastError = _snapshot.LastError,
                StartedAt = _snapshot.StartedAt,
            };

            mutate(mutable);

            _snapshot = new StatusSnapshot
            {
                LanEnabled = mutable.LanEnabled,
                LanRunning = mutable.LanRunning,
                LanPort = mutable.LanPort,
                LanError = mutable.LanError,
                AdminRunning = mutable.AdminRunning,
                AdminPort = mutable.AdminPort,
                AdminError = mutable.AdminError,
                LanUrls = mutable.LanUrls,
                RequestCount = mutable.RequestCount,
                ActiveConnections = mutable.ActiveConnections,
                LastError = mutable.LastError,
                StartedAt = mutable.StartedAt,
            };
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }

    public StatusSnapshot Snapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    public sealed class MutableStatus
    {
        public bool LanEnabled { get; set; }
        public bool LanRunning { get; set; }
        public int LanPort { get; set; }
        public string? LanError { get; set; }
        public bool AdminRunning { get; set; }
        public int AdminPort { get; set; }
        public string? AdminError { get; set; }
        public IReadOnlyList<string> LanUrls { get; set; } = Array.Empty<string>();
        public long RequestCount { get; set; }
        public int ActiveConnections { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
    }
}

/// <summary>
/// <see cref="ServerStatus"/> 的不可变快照。
/// </summary>
public sealed record StatusSnapshot
{
    public bool LanEnabled { get; init; }
    public bool LanRunning { get; init; }
    public int LanPort { get; init; }
    public string? LanError { get; init; }
    public bool AdminRunning { get; init; }
    public int AdminPort { get; init; }
    public string? AdminError { get; init; }
    public IReadOnlyList<string> LanUrls { get; init; } = Array.Empty<string>();
    public long RequestCount { get; init; }
    public int ActiveConnections { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
}
