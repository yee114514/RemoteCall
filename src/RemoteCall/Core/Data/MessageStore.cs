using RemoteCall.Core.Config;

namespace RemoteCall.Core.Data;

/// <summary>
/// 消息与设备的存储。所有操作线程安全，写操作会立即落盘。
/// </summary>
public sealed class MessageStore
{
    public const int MaxDeviceNameLength = 24;
    public const string UnnamedDevice = "未命名设备";
    public const string UnknownDevice = "未知设备";

    private readonly object _sync = new();
    private readonly string _path;
    private readonly Action<string>? _log;
    private StoreSnapshot _snapshot;
    private int _retention = CoreDefaults.MessageRetention;
    private int _version;

    public MessageStore(string folder, Action<string>? log = null)
    {
        _log = log;
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "store.json");
        _snapshot = AtomicJsonFile.Load<StoreSnapshot>(_path, log) ?? new StoreSnapshot();
        _snapshot.Devices ??= new List<DeviceRecord>();
        _snapshot.Messages ??= new List<MessageRecord>();
        if (_snapshot.NextMessageId <= 0)
        {
            _snapshot.NextMessageId = _snapshot.Messages.Count == 0 ? 1 : _snapshot.Messages.Max(m => m.Id) + 1;
        }

        Trim();
    }

    public string FilePath => _path;

    /// <summary>
    /// 每发生一次修改就自增，供网页端轮询时判断是否需要重绘。
    /// </summary>
    public int Version
    {
        get
        {
            lock (_sync)
            {
                return _version;
            }
        }
    }

    /// <summary>新消息加入。</summary>
    public event Action<MessageView>? MessageAdded;

    /// <summary>消息被更新（完成状态变化等）。</summary>
    public event Action<MessageView>? MessageUpdated;

    /// <summary>任何变化（新增/更新/删除/改名/裁剪）。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 设置保留条数。<c>-1</c> 表示不限制（不裁剪）。
    /// </summary>
    public void SetRetention(int retention)
    {
        _retention = retention < 0 ? CoreDefaults.Unlimited : Math.Clamp(retention, 50, 10000);
        Trim();
    }

    /// <summary>
    /// 添加一条消息。
    /// </summary>
    public MessageView Add(string text, string deviceId, string? deviceName, string ip, string nameSource = DeviceNameSource.Auto)
    {
        MessageView view;
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            EnsureDeviceLocked(deviceId, deviceName, ip, now, nameSource);
            var record = new MessageRecord
            {
                Id = _snapshot.NextMessageId++,
                Text = text,
                DeviceId = deviceId,
                Ip = ip,
                CreatedAt = now,
                Completed = false,
            };
            _snapshot.Messages.Add(record);
            TrimLocked();
            SaveLocked();
            _version++;
            view = ToViewLocked(record);
        }

        MessageAdded?.Invoke(view);
        Changed?.Invoke(this, EventArgs.Empty);
        return view;
    }

    /// <summary>
    /// 设置消息的完成状态。返回 null 表示消息不存在。
    /// </summary>
    public MessageView? SetCompleted(long id, bool completed)
    {
        MessageView? view = null;
        lock (_sync)
        {
            var record = _snapshot.Messages.FirstOrDefault(m => m.Id == id);
            if (record == null)
            {
                return null;
            }

            if (record.Completed != completed)
            {
                record.Completed = completed;
                record.CompletedAt = completed ? DateTimeOffset.UtcNow : null;
                SaveLocked();
                _version++;
            }

            view = ToViewLocked(record);
        }

        MessageUpdated?.Invoke(view);
        Changed?.Invoke(this, EventArgs.Empty);
        return view;
    }

    public bool Delete(long id)
    {
        bool removed;
        lock (_sync)
        {
            removed = _snapshot.Messages.RemoveAll(m => m.Id == id) > 0;
            if (removed)
            {
                SaveLocked();
                _version++;
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>
    /// 清空消息。<paramref name="onlyCompleted"/> 为 true 时只删已完成的。
    /// </summary>
    public int Clear(bool onlyCompleted)
    {
        int removed;
        lock (_sync)
        {
            removed = onlyCompleted
                ? _snapshot.Messages.RemoveAll(m => m.Completed)
                : RemoveAllMessagesLocked();
            if (removed > 0)
            {
                SaveLocked();
                _version++;
            }
        }

        if (removed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>
    /// 查询消息，按时间倒序（最新在前）。
    /// </summary>
    public List<MessageView> Query(MessageFilter filter, int limit)
    {
        limit = Math.Clamp(limit, 1, 2000);
        lock (_sync)
        {
            IEnumerable<MessageRecord> source = _snapshot.Messages;
            source = filter switch
            {
                MessageFilter.Pending => source.Where(m => !m.Completed),
                MessageFilter.Completed => source.Where(m => m.Completed),
                _ => source,
            };

            return source
                .OrderByDescending(m => m.Id)
                .Take(limit)
                .Select(ToViewLocked)
                .ToList();
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _snapshot.Messages.Count;
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _snapshot.Messages.Count(m => !m.Completed);
            }
        }
    }

    public List<DeviceView> Devices()
    {
        lock (_sync)
        {
            return _snapshot.Devices
                .OrderByDescending(d => d.LastSeenAt)
                .Select(ToDeviceView)
                .ToList();
        }
    }

    /// <summary>
    /// 修改设备名。历史消息不需要改动，展示时按 deviceId 联查当前名字。
    /// <para><paramref name="nameSource"/> 为 <see cref="DeviceNameSource.User"/> 时视为用户选择，
    /// 之后不会被 UA 自动识别结果覆盖。</para>
    /// </summary>
    public bool RenameDevice(string deviceId, string? name, string nameSource = DeviceNameSource.User)
    {
        var sanitized = SanitizeDeviceName(name);
        var source = string.Equals(nameSource, DeviceNameSource.Auto, StringComparison.OrdinalIgnoreCase)
            ? DeviceNameSource.Auto
            : DeviceNameSource.User;

        lock (_sync)
        {
            var device = _snapshot.Devices.FirstOrDefault(d => d.Id == deviceId);
            if (device == null)
            {
                return false;
            }

            // 自动识别不能覆盖用户手动改过的名字
            if (source == DeviceNameSource.Auto && device.NameSource == DeviceNameSource.User)
            {
                return true;
            }

            if (device.Name == sanitized && device.NameSource == source)
            {
                return true;
            }

            device.Name = sanitized;
            device.NameSource = source;
            SaveLocked();
            _version++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public string ResolveDeviceName(string deviceId)
    {
        lock (_sync)
        {
            var device = _snapshot.Devices.FirstOrDefault(d => d.Id == deviceId);
            return device == null ? UnknownDevice : DisplayNameOf(device);
        }
    }

    /// <summary>
    /// 确保设备存在，并更新它最后出现的 IP/时间与名字。
    /// <para>名字来源为 Auto 时不会覆盖用户手动改过的名字。</para>
    /// </summary>
    public DeviceView EnsureDevice(string deviceId, string? name, string ip, string nameSource = DeviceNameSource.Auto)
    {
        DeviceView view;
        lock (_sync)
        {
            var device = EnsureDeviceLocked(deviceId, name, ip, DateTimeOffset.UtcNow, nameSource);
            SaveLocked();
            view = ToDeviceView(device);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return view;
    }

    /// <summary>
    /// 查一个设备（不存在返回 null）。
    /// </summary>
    public DeviceView? GetDevice(string deviceId)
    {
        lock (_sync)
        {
            var device = _snapshot.Devices.FirstOrDefault(d => d.Id == deviceId);
            return device == null ? null : ToDeviceView(device);
        }
    }

    /// <summary>
    /// 按保留条数裁剪最旧的消息。
    /// </summary>
    public void Trim()
    {
        lock (_sync)
        {
            if (TrimLocked())
            {
                SaveLocked();
                _version++;
            }
        }
    }

    public static string SanitizeDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return UnnamedDevice;
        }

        var filtered = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (filtered.Length == 0)
        {
            return UnnamedDevice;
        }

        return filtered.Length <= MaxDeviceNameLength ? filtered : filtered[..MaxDeviceNameLength];
    }

    private DeviceRecord EnsureDeviceLocked(string deviceId, string? name, string ip, DateTimeOffset now, string nameSource = DeviceNameSource.Auto)
    {
        var source = string.Equals(nameSource, DeviceNameSource.Auto, StringComparison.OrdinalIgnoreCase)
            ? DeviceNameSource.Auto
            : DeviceNameSource.User;
        var sanitized = SanitizeDeviceName(name);

        var device = _snapshot.Devices.FirstOrDefault(d => d.Id == deviceId);
        if (device == null)
        {
            device = new DeviceRecord
            {
                Id = deviceId,
                Name = sanitized,
                NameSource = source,
                LastIp = ip,
                FirstSeenAt = now,
                LastSeenAt = now,
            };
            _snapshot.Devices.Add(device);
            return device;
        }

        device.LastIp = ip;
        device.LastSeenAt = now;

        if (sanitized == UnnamedDevice)
        {
            return device;
        }

        if (source == DeviceNameSource.Auto && device.NameSource == DeviceNameSource.User)
        {
            // 用户改过的名字，不被自动识别覆盖
            return device;
        }

        device.Name = sanitized;
        device.NameSource = source;
        return device;
    }

    private static DeviceView ToDeviceView(DeviceRecord device)
    {
        return new DeviceView(
            device.Id,
            DisplayNameOf(device),
            string.IsNullOrEmpty(device.NameSource) ? DeviceNameSource.Auto : device.NameSource,
            device.LastIp,
            device.FirstSeenAt,
            device.LastSeenAt);
    }

    private bool TrimLocked()
    {
        if (_retention < 0)
        {
            return false; // -1：不限制，全部保留
        }

        var overflow = _snapshot.Messages.Count - _retention;
        if (overflow <= 0)
        {
            return false;
        }

        _snapshot.Messages.RemoveRange(0, overflow);
        return true;
    }

    private int RemoveAllMessagesLocked()
    {
        var count = _snapshot.Messages.Count;
        _snapshot.Messages.Clear();
        return count;
    }

    private MessageView ToViewLocked(MessageRecord record)
    {
        var device = _snapshot.Devices.FirstOrDefault(d => d.Id == record.DeviceId);
        return new MessageView(
            record.Id,
            record.Text,
            record.DeviceId,
            device == null ? UnknownDevice : DisplayNameOf(device),
            record.Ip,
            record.CreatedAt,
            record.Completed,
            record.CompletedAt);
    }

    private static string DisplayNameOf(DeviceRecord device)
    {
        return string.IsNullOrWhiteSpace(device.Name) ? UnnamedDevice : device.Name;
    }

    private void SaveLocked()
    {
        AtomicJsonFile.Save(_path, _snapshot, _log);
    }
}
