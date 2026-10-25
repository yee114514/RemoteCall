using RemoteCall.Core.Auth;

namespace RemoteCall.Core.Config;

/// <summary>
/// 插件设置的读写、校验与密码管理。所有修改都会立即落盘并触发 <see cref="Changed"/>。
/// </summary>
public sealed class PluginSettingsService
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly Action<string>? _log;
    private PluginSettings _settings;

    public PluginSettingsService(string folder, Action<string>? log = null)
    {
        _log = log;
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "settings.json");
        _settings = AtomicJsonFile.Load<PluginSettings>(_path, log) ?? new PluginSettings();
        Normalize(_settings);
        if (_settings.LanPort == _settings.AdminPort)
        {
            // 配置文件被手工改坏时兜底
            _settings.LanPort = _settings.AdminPort == CoreDefaults.LanPort
                ? CoreDefaults.LanPort + 1
                : CoreDefaults.LanPort;
        }

        EnsurePassword();
        Save();
    }

    /// <summary>设置被修改后触发（读到的对象是修改后的副本，请不要直接改它）。</summary>
    public event EventHandler? Changed;

    public string FilePath => _path;

    /// <summary>当前设置的快照（深拷贝，避免外部直接改动内部状态）。</summary>
    public PluginSettings Settings
    {
        get
        {
            lock (_sync)
            {
                return AtomicJsonFile.DeepClone(_settings);
            }
        }
    }

    /// <summary>当前设置（内部只读引用；仅限本程序内读取，不要修改）。</summary>
    public PluginSettings Current
    {
        get
        {
            lock (_sync)
            {
                return _settings;
            }
        }
    }

    /// <summary>
    /// 用一个修改回调改设置：回调拿到副本，改完校验、落盘、触发事件。
    /// </summary>
    public bool Update(Action<PluginSettings> mutate, out string? error)
    {
        lock (_sync)
        {
            var copy = AtomicJsonFile.DeepClone(_settings);
            mutate(copy);
            error = Validate(copy);
            if (error != null)
            {
                return false;
            }

            Normalize(copy);
            _settings = copy;
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>校验密码是否正确。</summary>
    public bool VerifyPassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        PluginSettings snapshot;
        lock (_sync)
        {
            snapshot = _settings;
        }

        if (string.IsNullOrEmpty(snapshot.PasswordHash) || string.IsNullOrEmpty(snapshot.PasswordSalt))
        {
            return false;
        }

        return PasswordHasher.Verify(password, snapshot.PasswordSalt, snapshot.PasswordHash, snapshot.PasswordIterations);
    }

    /// <summary>设置用户自己的密码（会清空首次生成的明文密码）。</summary>
    public bool SetPassword(string password, out string? error)
    {
        if (!PasswordHasher.IsAcceptablePassword(password))
        {
            error = $"密码长度需在 {PasswordHasher.MinPasswordLength}~{PasswordHasher.MaxPasswordLength} 个字符之间。";
            return false;
        }

        var (salt, hash) = PasswordHasher.Create(password, CoreDefaults.PasswordIterations);
        error = null;
        return Update(s =>
        {
            s.PasswordSalt = Convert.ToBase64String(salt);
            s.PasswordHash = Convert.ToBase64String(hash);
            s.PasswordIterations = CoreDefaults.PasswordIterations;
            s.GeneratedPassword = "";
        }, out error);
    }

    /// <summary>生成一个随机密码并设为当前密码，返回明文。</summary>
    public string GenerateAndSetPassword(out string? error)
    {
        var password = PasswordHasher.GeneratePassword();
        var (salt, hash) = PasswordHasher.Create(password, CoreDefaults.PasswordIterations);
        Update(s =>
        {
            s.PasswordSalt = Convert.ToBase64String(salt);
            s.PasswordHash = Convert.ToBase64String(hash);
            s.PasswordIterations = CoreDefaults.PasswordIterations;
            s.GeneratedPassword = password;
        }, out error);
        return password;
    }

    /// <summary>
    /// 首次启动时没有密码，则设为默认密码（<see cref="CoreDefaults.DefaultPassword"/>），
    /// 并把明文留在 <c>GeneratedPassword</c> 里方便用户查看，用户改密后会被清空。
    /// </summary>
    public void EnsurePassword()
    {
        bool missing;
        lock (_sync)
        {
            missing = string.IsNullOrEmpty(_settings.PasswordHash) || string.IsNullOrEmpty(_settings.PasswordSalt);
        }

        if (!missing)
        {
            return;
        }

        var password = CoreDefaults.DefaultPassword;
        var (salt, hash) = PasswordHasher.Create(password, CoreDefaults.PasswordIterations);
        lock (_sync)
        {
            _settings.PasswordSalt = Convert.ToBase64String(salt);
            _settings.PasswordHash = Convert.ToBase64String(hash);
            _settings.PasswordIterations = CoreDefaults.PasswordIterations;
            _settings.GeneratedPassword = password;
        }

        _log?.Invoke($"已设置默认访问密码：{password}（建议在设置页改成自己的密码）");
    }

    public void Save()
    {
        lock (_sync)
        {
            AtomicJsonFile.Save(_path, _settings, _log);
        }
    }

    /// <summary>把越界的「软」配置收敛到合法范围（端口等硬约束由 <see cref="Validate"/> 负责报错）。</summary>
    private static void Normalize(PluginSettings s)
    {
        s.LanPort = s.LanPort is >= 1 and <= 65535 ? s.LanPort : CoreDefaults.LanPort;
        s.AdminPort = s.AdminPort is >= 1 and <= 65535 ? s.AdminPort : CoreDefaults.AdminPort;

        // 负数一律视为「不限制」；0 视为没填，收敛到默认值
        s.SessionDays = s.SessionDays < 0
            ? CoreDefaults.Unlimited
            : s.SessionDays == 0 ? CoreDefaults.SessionDays : Math.Clamp(s.SessionDays, 1, 365);
        s.MessageRetention = s.MessageRetention < 0
            ? CoreDefaults.Unlimited
            : s.MessageRetention == 0 ? CoreDefaults.MessageRetention : Math.Clamp(s.MessageRetention, 50, 10000);

        s.NotificationMaskSeconds = Math.Clamp(s.NotificationMaskSeconds, 1, 60);
        s.NotificationOverlaySeconds = Math.Clamp(s.NotificationOverlaySeconds, 1, 180);
        if (s.PasswordIterations < 10_000)
        {
            s.PasswordIterations = CoreDefaults.PasswordIterations;
        }

        s.GeneratedPassword ??= "";
        s.PasswordHash ??= "";
        s.PasswordSalt ??= "";
    }

    private static string? Validate(PluginSettings s)
    {
        if (s.LanPort is < 1 or > 65535)
        {
            return "局域网端口必须在 1~65535 之间。";
        }

        if (s.AdminPort is < 1 or > 65535)
        {
            return "本机管理端口必须在 1~65535 之间。";
        }

        if (s.LanPort == s.AdminPort)
        {
            return "局域网端口与本机管理端口不能相同。";
        }

        return null;
    }
}
