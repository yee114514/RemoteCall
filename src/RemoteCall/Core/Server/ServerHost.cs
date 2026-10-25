using System.Net;
using RemoteCall.Core.Auth;
using RemoteCall.Core.Config;
using RemoteCall.Core.Data;
using RemoteCall.Core.Http;

namespace RemoteCall.Core.Server;

/// <summary>
/// 管理两个 HTTP 监听的启停与热重配。
/// </summary>
public sealed class ServerHost
{
    private readonly object _sync = new();
    private readonly PluginSettingsService _settings;
    private readonly MessageStore _store;
    private readonly ServerStatus _status;
    private readonly Action<string>? _log;
    private readonly LanApi _lanApi;
    private readonly AdminApi _adminApi;

    private HttpServer? _lan;
    private HttpServer? _admin;
    private string? _lanError;
    private string? _adminError;
    private bool _running;
    private int _applyGeneration;

    public ServerHost(
        PluginSettingsService settings,
        MessageStore store,
        SessionStore sessions,
        ServerStatus status,
        LogBuffer logs,
        Action<string>? log = null)
    {
        _settings = settings;
        _store = store;
        _status = status;
        _log = log;
        Context = new WebContext(settings, store, sessions, status, logs, log)
        {
            RestartServers = Restart,
        };
        _lanApi = new LanApi(Context);
        _adminApi = new AdminApi(Context);
    }

    public WebContext Context { get; }

    public ServerStatus Status => _status;

    public void Start()
    {
        _store.SetRetention(_settings.Current.MessageRetention);
        _running = true;
        _settings.Changed += OnSettingsChanged;
        ApplyListeners();

        // 定期清理过期会话
        _ = Task.Run(async () =>
        {
            while (_running)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
                    if (!_running)
                    {
                        break;
                    }

                    Context.Sessions.Sweep();
                }
                catch
                {
                    // 忽略
                }
            }
        });
    }

    public void Stop()
    {
        _running = false;
        _settings.Changed -= OnSettingsChanged;
        lock (_sync)
        {
            _lan?.Stop();
            _lan?.Dispose();
            _lan = null;
            _admin?.Stop();
            _admin?.Dispose();
            _admin = null;
        }

        RefreshStatus();
    }

    /// <summary>
    /// 立即重启两个监听。
    /// </summary>
    public void Restart()
    {
        try
        {
            lock (_sync)
            {
                _lan?.Stop();
                _lan?.Dispose();
                _lan = null;
                _admin?.Stop();
                _admin?.Dispose();
                _admin = null;
            }

            ApplyListeners();
        }
        catch (Exception ex)
        {
            Context.Log($"重启监听时出错：{ex.Message}");
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (!_running)
        {
            return;
        }

        // 稍等一下再应用：让触发修改的那个 HTTP 响应先写回去
        var generation = Interlocked.Increment(ref _applyGeneration);
        _ = Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);
            if (!_running || Volatile.Read(ref _applyGeneration) != generation)
            {
                return;
            }

            ApplyListeners();
        });
    }

    private void ApplyListeners()
    {
        var settings = _settings.Current;
        _store.SetRetention(settings.MessageRetention);

        lock (_sync)
        {
            // ---- 局域网网页 ----
            if (!settings.LanEnabled)
            {
                if (_lan != null)
                {
                    _lan.Stop();
                    _lan.Dispose();
                    _lan = null;
                    Context.Log("局域网网页服务已关闭");
                }
            }
            else if (_lan == null || _lan.Port != settings.LanPort)
            {
                if (_lan != null)
                {
                    _lan.Stop();
                    _lan.Dispose();
                    _lan = null;
                }

                var server = new HttpServer(IPAddress.Any, settings.LanPort, "局域网网页", _lanApi.Handle, Context.Log);
                try
                {
                    server.Start();
                    _lan = server;
                    _lanError = null;
                    Context.Log($"局域网网页：http://<本机IP>:{settings.LanPort}/");
                }
                catch (Exception ex)
                {
                    server.Dispose();
                    _lanError = DescribeBindError(ex, settings.LanPort);
                    Context.Log($"局域网网页启动失败：{_lanError}");
                }
            }

            // ---- 本机管理页 ----
            if (_admin == null || _admin.Port != settings.AdminPort)
            {
                if (_admin != null)
                {
                    _admin.Stop();
                    _admin.Dispose();
                    _admin = null;
                }

                var server = new HttpServer(IPAddress.Loopback, settings.AdminPort, "本机管理页", _adminApi.Handle, Context.Log);
                try
                {
                    server.Start();
                    _admin = server;
                    _adminError = null;
                    Context.Log($"本机管理页：http://127.0.0.1:{settings.AdminPort}/");
                }
                catch (Exception ex)
                {
                    server.Dispose();
                    _adminError = DescribeBindError(ex, settings.AdminPort);
                    Context.Log($"本机管理页启动失败：{_adminError}");
                }
            }
        }

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var settings = _settings.Current;
        HttpServer? lan;
        HttpServer? admin;
        lock (_sync)
        {
            lan = _lan;
            admin = _admin;
        }

        _status.Update(s =>
        {
            s.LanEnabled = settings.LanEnabled;
            s.LanPort = settings.LanPort;
            s.AdminPort = settings.AdminPort;
            s.LanRunning = lan?.IsRunning == true;
            s.AdminRunning = admin?.IsRunning == true;
            s.LanError = _lanError;
            s.AdminError = _adminError;
            s.LanUrls = settings.LanEnabled && s.LanRunning
                ? LanAddresses.GetUrls(settings.LanPort)
                : Array.Empty<string>();
            s.RequestCount = (lan?.RequestCount ?? 0) + (admin?.RequestCount ?? 0);
            s.ActiveConnections = (lan?.ActiveConnections ?? 0) + (admin?.ActiveConnections ?? 0);
            s.LastError = _lanError ?? _adminError;
            s.StartedAt ??= DateTimeOffset.UtcNow;
        });
    }

    private static string DescribeBindError(Exception ex, int port)
    {
        return ex switch
        {
            System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse } =>
                $"端口 {port} 已被其它程序占用，请在设置里换一个端口。",
            System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AccessDenied } =>
                $"没有权限监听端口 {port}（可能被系统保留），请换一个端口。",
            System.Net.Sockets.SocketException socketEx =>
                $"无法监听端口 {port}：{socketEx.Message}",
            _ => $"无法监听端口 {port}：{ex.Message}",
        };
    }
}
