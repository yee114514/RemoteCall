namespace RemoteCall.Core.Server;

/// <summary>
/// 最近若干条运行日志，供设置页/管理页展示。
/// </summary>
public sealed class LogBuffer
{
    private const int Capacity = 200;

    private readonly object _sync = new();
    private readonly Queue<LogEntry> _entries = new();

    public void Add(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_sync)
        {
            _entries.Enqueue(new LogEntry(DateTimeOffset.UtcNow, message));
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    public List<LogEntry> Snapshot()
    {
        lock (_sync)
        {
            return _entries.ToList();
        }
    }

    public string Text()
    {
        lock (_sync)
        {
            return string.Join(Environment.NewLine, _entries.Select(e => $"{e.Time.ToLocalTime():HH:mm:ss} {e.Message}"));
        }
    }
}

public sealed record LogEntry(DateTimeOffset Time, string Message);
