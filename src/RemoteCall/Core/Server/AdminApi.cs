using System.Net;
using RemoteCall.Core.Config;
using RemoteCall.Core.Data;
using RemoteCall.Core.Http;

namespace RemoteCall.Core.Server;

/// <summary>
/// 本机管理页（仅 127.0.0.1）的 API。不需要密码，但拒绝非本机来源。
/// </summary>
public sealed class AdminApi
{
    private readonly WebContext _ctx;

    public AdminApi(WebContext ctx)
    {
        _ctx = ctx;
    }

    public HttpResponse Handle(HttpRequest request)
    {
        if (!IsLoopback(request.RemoteIp))
        {
            throw ApiException.Forbidden("本机管理页只接受来自本机的访问。");
        }

        var path = request.Path;
        // HEAD 按 GET 处理（响应体由 HTTP 层剥掉）
        var method = request.Method == "HEAD" ? "GET" : request.Method;
        var segments = LanApi.SplitSegments(path);

        if (TryStatic(path, request, out var staticResponse))
        {
            return staticResponse;
        }

        if (method == "GET" && path is "/" or "/index.html")
        {
            return Assets.Serve("admin/index.html", request);
        }

        if (path == "/favicon.ico")
        {
            return HttpResponse.NoContent();
        }

        if (path == "/api/health")
        {
            return method == "GET"
                ? HttpResponse.Json(new { ok = true, app = "remote.call.admin", version = _ctx.PluginVersion })
                : throw MethodNotAllowed();
        }

        if (path == "/api/state")
        {
            return method == "GET" ? State() : throw MethodNotAllowed();
        }

        if (path == "/api/settings")
        {
            return method == "POST" ? UpdateSettings(request) : throw MethodNotAllowed();
        }

        if (path == "/api/password")
        {
            return method == "POST" ? SetPassword(request) : throw MethodNotAllowed();
        }

        if (path == "/api/password/generate")
        {
            return method == "POST" ? GeneratePassword() : throw MethodNotAllowed();
        }

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "messages")
        {
            var id = segments[2];
            switch (segments[3])
            {
                case "complete" when method == "POST":
                    return CompleteMessage(request, id);
                case "delete" when method == "POST":
                    return DeleteMessage(id);
            }

            throw MethodNotAllowed();
        }

        if (path == "/api/messages/clear")
        {
            return method == "POST" ? ClearMessages(request) : throw MethodNotAllowed();
        }

        if (segments.Length == 3 && segments[0] == "api" && segments[1] == "devices")
        {
            return method switch
            {
                "PUT" => RenameDevice(request, segments[2]),
                _ => throw MethodNotAllowed(),
            };
        }

        if (path == "/api/servers/restart")
        {
            if (method != "POST")
            {
                throw MethodNotAllowed();
            }

            ScheduleRestart();
            return HttpResponse.Json(new { ok = true, message = "正在重启监听…" });
        }

        if (path == "/api/selftest")
        {
            if (method != "POST")
            {
                throw MethodNotAllowed();
            }

            var view = _ctx.Store.Add("这是一条测试消息（来自本机管理页）", "selftest", "本机测试", "127.0.0.1");
            _ctx.Log($"已插入测试消息 #{view.Id}");
            return HttpResponse.Json(new { ok = true, message = view });
        }

        throw ApiException.NotFound("页面不存在。");
    }

    private HttpResponse State()
    {
        var settings = _ctx.Settings.Current;
        return HttpResponse.Json(new
        {
            ok = true,
            version = _ctx.Store.Version,
            serverTime = DateTimeOffset.UtcNow,
            status = _ctx.Status.Snapshot(),
            settings = PublicSettings(settings),
            messages = _ctx.Store.Query(MessageFilter.All, 500),
            devices = _ctx.Store.Devices(),
            logs = _ctx.Logs.Snapshot().TakeLast(50).ToList(),
        });
    }

    private HttpResponse UpdateSettings(HttpRequest request)
    {
        var body = request.BodyJson<AdminSettingsRequest>() ?? throw ApiException.BadRequest("缺少请求体。");
        var ok = _ctx.Settings.Update(settings =>
        {
            if (body.LanEnabled.HasValue)
            {
                settings.LanEnabled = body.LanEnabled.Value;
            }

            if (body.LanPort.HasValue)
            {
                settings.LanPort = body.LanPort.Value;
            }

            if (body.AdminPort.HasValue)
            {
                settings.AdminPort = body.AdminPort.Value;
            }

            if (body.SessionDays.HasValue)
            {
                settings.SessionDays = body.SessionDays.Value;
            }

            if (body.NotificationEnabled.HasValue)
            {
                settings.NotificationEnabled = body.NotificationEnabled.Value;
            }

            if (body.NotificationMaskSeconds.HasValue)
            {
                settings.NotificationMaskSeconds = body.NotificationMaskSeconds.Value;
            }

            if (body.NotificationOverlaySeconds.HasValue)
            {
                settings.NotificationOverlaySeconds = body.NotificationOverlaySeconds.Value;
            }

            if (body.NotificationSpeech.HasValue)
            {
                settings.NotificationSpeech = body.NotificationSpeech.Value;
            }

            if (body.MessageRetention.HasValue)
            {
                settings.MessageRetention = body.MessageRetention.Value;
            }
        }, out var error);

        if (!ok)
        {
            throw ApiException.BadRequest(error ?? "设置不合法。", "invalid_settings");
        }

        _ctx.Log("设置已更新");
        return HttpResponse.Json(new
        {
            ok = true,
            settings = PublicSettings(_ctx.Settings.Current),
            status = _ctx.Status.Snapshot(),
        });
    }

    private HttpResponse SetPassword(HttpRequest request)
    {
        var body = request.BodyJson<PasswordRequest>() ?? throw ApiException.BadRequest("缺少请求体。");
        if (!_ctx.Settings.SetPassword(body.Password ?? "", out var error))
        {
            throw ApiException.BadRequest(error ?? "密码不合法。", "invalid_password");
        }

        _ctx.Sessions.Clear();
        _ctx.Log("访问密码已更新，所有设备需要重新登录");
        return HttpResponse.Json(new { ok = true });
    }

    private HttpResponse GeneratePassword()
    {
        var password = _ctx.Settings.GenerateAndSetPassword(out var error);
        if (error != null)
        {
            throw ApiException.BadRequest(error, "invalid_password");
        }

        _ctx.Sessions.Clear();
        _ctx.Log("已生成新的访问密码，所有设备需要重新登录");
        return HttpResponse.Json(new { ok = true, password });
    }

    private HttpResponse CompleteMessage(HttpRequest request, string idSegment)
    {
        var id = LanApi.ParseLong(idSegment);
        var body = request.BodyJson<CompleteRequest>() ?? new CompleteRequest { Completed = true };
        var view = _ctx.Store.SetCompleted(id, body.Completed) ?? throw ApiException.NotFound("消息不存在。");
        return HttpResponse.Json(new { ok = true, message = view });
    }

    private HttpResponse DeleteMessage(string idSegment)
    {
        var id = LanApi.ParseLong(idSegment);
        if (!_ctx.Store.Delete(id))
        {
            throw ApiException.NotFound("消息不存在。");
        }

        _ctx.Log($"已删除消息 #{id}");
        return HttpResponse.Json(new { ok = true });
    }

    private HttpResponse ClearMessages(HttpRequest request)
    {
        var body = request.BodyJson<ClearRequest>() ?? new ClearRequest { Scope = "all" };
        var onlyCompleted = body.Scope?.ToLowerInvariant() is "done" or "completed";
        var removed = _ctx.Store.Clear(onlyCompleted);
        _ctx.Log(onlyCompleted ? $"已清空 {removed} 条已完成消息" : $"已清空全部 {removed} 条消息");
        return HttpResponse.Json(new { ok = true, removed });
    }

    private HttpResponse RenameDevice(HttpRequest request, string deviceId)
    {
        var body = request.BodyJson<RenameDeviceRequest>() ?? throw ApiException.BadRequest("缺少请求体。");
        var name = (body.Name ?? "").Trim();
        if (name.Length == 0)
        {
            throw ApiException.BadRequest("设备名不能为空。", "empty_name");
        }

        if (!_ctx.Store.RenameDevice(deviceId, name))
        {
            throw ApiException.NotFound("设备不存在。");
        }

        var device = _ctx.Store.Devices().FirstOrDefault(d => d.Id == deviceId);
        _ctx.Log($"设备 {deviceId} 已改名为「{device?.Name}」");
        return HttpResponse.Json(new { ok = true, device });
    }

    private void ScheduleRestart()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(200).ConfigureAwait(false);
            try
            {
                _ctx.RestartServers?.Invoke();
            }
            catch (Exception ex)
            {
                _ctx.Log($"重启监听失败：{ex.Message}");
            }
        });
    }

    private static object PublicSettings(PluginSettings settings)
    {
        return new
        {
            lanEnabled = settings.LanEnabled,
            lanPort = settings.LanPort,
            adminPort = settings.AdminPort,
            sessionDays = settings.SessionDays,
            passwordSet = !string.IsNullOrEmpty(settings.PasswordHash),
            generatedPassword = string.IsNullOrEmpty(settings.GeneratedPassword) ? null : settings.GeneratedPassword,
            notificationEnabled = settings.NotificationEnabled,
            notificationMaskSeconds = settings.NotificationMaskSeconds,
            notificationOverlaySeconds = settings.NotificationOverlaySeconds,
            notificationSpeech = settings.NotificationSpeech,
            messageRetention = settings.MessageRetention,
        };
    }

    private static bool IsLoopback(string remoteIp)
    {
        return IPAddress.TryParse(remoteIp, out var address) && LanAddresses.IsLoopback(address);
    }

    private static bool TryStatic(string path, HttpRequest request, out HttpResponse response)
    {
        response = HttpResponse.NoContent();
        string? logical = path switch
        {
            "/app.js" => "admin/app.js",
            "/style.css" => "admin/style.css",
            _ => null,
        };

        if (logical == null)
        {
            return false;
        }

        response = Assets.Serve(logical, request);
        return true;
    }

    private static ApiException MethodNotAllowed()
    {
        return new ApiException(405, "method_not_allowed", "不支持的请求方法。");
    }
}
