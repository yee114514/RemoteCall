using System.Globalization;
using System.Text;
using RemoteCall.Core.Auth;
using RemoteCall.Core.Config;
using RemoteCall.Core.Data;
using RemoteCall.Core.Http;

namespace RemoteCall.Core.Server;

/// <summary>
/// 局域网网页服务（0.0.0.0:LAN 端口）的 API。
/// </summary>
public sealed class LanApi
{
    private readonly WebContext _ctx;

    public LanApi(WebContext ctx)
    {
        _ctx = ctx;
    }

    public HttpResponse Handle(HttpRequest request)
    {
        var path = request.Path;
        // HEAD 按 GET 处理（响应体由 HTTP 层剥掉）
        var method = request.Method == "HEAD" ? "GET" : request.Method;
        var segments = SplitSegments(path);

        // ---- 静态资源 ----
        if (TryStatic(path, request, out var staticResponse))
        {
            return staticResponse;
        }

        // ---- 页面 ----
        if (method == "GET" && (path is "/" or "/index.html"))
        {
            return IsAuthenticated(request)
                ? Assets.Serve("lan/index.html", request)
                : HttpResponse.Redirect("/login");
        }

        if (method == "GET" && path is "/login" or "/login.html")
        {
            return Assets.Serve("lan/login.html", request);
        }

        if (path == "/favicon.ico")
        {
            return HttpResponse.NoContent();
        }

        // ---- 公共 ----
        if (path == "/api/health")
        {
            if (method != "GET")
            {
                throw MethodNotAllowed();
            }

            return HttpResponse.Json(new { ok = true, app = "remote.call", version = _ctx.PluginVersion });
        }

        if (path == "/api/login")
        {
            return method == "POST" ? Login(request) : throw MethodNotAllowed();
        }

        if (path == "/api/logout")
        {
            return method == "POST" ? Logout(request) : throw MethodNotAllowed();
        }

        // ---- 需要登录 ----
        if (path == "/api/session")
        {
            return method == "GET" ? Session(request) : throw MethodNotAllowed();
        }

        if (path == "/api/messages")
        {
            return method switch
            {
                "GET" => ListMessages(request),
                "POST" => CreateMessage(request),
                _ => throw MethodNotAllowed(),
            };
        }

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "messages" && segments[3] == "complete")
        {
            return method == "POST" ? CompleteMessage(request, segments[2]) : throw MethodNotAllowed();
        }

        if (path == "/api/devices")
        {
            return method == "GET" ? ListDevices(request) : throw MethodNotAllowed();
        }

        if (segments.Length == 3 && segments[0] == "api" && segments[1] == "devices")
        {
            return method switch
            {
                "PUT" => RenameDevice(request, segments[2]),
                _ => throw MethodNotAllowed(),
            };
        }

        throw ApiException.NotFound("页面不存在。");
    }

    // ---------------- 具体接口 ----------------

    private HttpResponse Login(HttpRequest request)
    {
        if (!_ctx.LoginLimiter.TryAcquire(request.RemoteIp, out var retryAfter))
        {
            throw new ApiException(429, "too_many_attempts", $"登录尝试过于频繁，请 {retryAfter} 秒后再试。");
        }

        var body = request.BodyJson<LoginRequest>() ?? throw ApiException.BadRequest("缺少请求体。");
        if (!_ctx.Settings.VerifyPassword(body.Password))
        {
            _ctx.Log($"来自 {request.RemoteIp} 的登录失败：密码错误");
            throw new ApiException(401, "invalid_password", "密码错误。");
        }

        _ctx.LoginLimiter.Reset(request.RemoteIp);

        var settings = _ctx.Settings.Current;
        // SessionDays 为负数（-1）表示不限制：会话不因时间过期
        var lifetime = body.Remember
            ? settings.SessionDays < 0
                ? TimeSpan.MaxValue
                : TimeSpan.FromDays(Math.Clamp(settings.SessionDays, 1, 365))
            : TimeSpan.FromHours(CoreDefaults.SessionHoursNotRemembered);
        var (token, expiresAt) = _ctx.Sessions.Create(lifetime, request.RemoteIp);
        _ctx.Log($"来自 {request.RemoteIp} 的设备已登录{(body.Remember ? "（记住登录）" : "")}");

        var response = HttpResponse.Json(new { ok = true, expiresAt, remember = body.Remember });
        response.Headers["Set-Cookie"] = BuildCookie(token, body.Remember ? expiresAt : null);
        return response;
    }

    private HttpResponse Logout(HttpRequest request)
    {
        var token = request.Cookie(CoreDefaults.CookieName);
        _ctx.Sessions.Remove(token);
        var response = HttpResponse.NoContent();
        response.Headers["Set-Cookie"] = BuildClearCookie();
        return response;
    }

    private HttpResponse Session(HttpRequest request)
    {
        var session = Authenticate(request);
        var deviceId = request.QueryValue("deviceId");
        string? deviceName = null;
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var device = _ctx.Store.Devices().FirstOrDefault(d => d.Id == deviceId);
            deviceName = device?.Name;
        }

        return HttpResponse.Json(new
        {
            ok = true,
            authenticated = true,
            yourIp = request.RemoteIp,
            serverTime = DateTimeOffset.UtcNow,
            pluginVersion = _ctx.PluginVersion,
            expiresAt = session.ExpiresAt,
            deviceName,
            messageCount = _ctx.Store.Count,
            pendingCount = _ctx.Store.PendingCount,
            version = _ctx.Store.Version,
        });
    }

    private HttpResponse ListMessages(HttpRequest request)
    {
        Authenticate(request);
        var filter = ParseFilter(request.QueryValue("filter"));
        var limit = ParseInt(request.QueryValue("limit"), 200, 1, 500);
        return HttpResponse.Json(new
        {
            ok = true,
            version = _ctx.Store.Version,
            serverTime = DateTimeOffset.UtcNow,
            total = _ctx.Store.Count,
            pending = _ctx.Store.PendingCount,
            messages = _ctx.Store.Query(filter, limit),
        });
    }

    private HttpResponse CreateMessage(HttpRequest request)
    {
        Authenticate(request);
        var body = request.BodyJson<SendMessageRequest>() ?? throw ApiException.BadRequest("缺少请求体。");

        var text = (body.Text ?? "").Trim();
        if (text.Length == 0)
        {
            throw ApiException.BadRequest("消息内容不能为空。", "empty_text");
        }

        if (text.Length > CoreDefaults.MaxMessageLength)
        {
            throw ApiException.BadRequest($"消息内容不能超过 {CoreDefaults.MaxMessageLength} 个字符。", "text_too_long");
        }

        var deviceId = NormalizeDeviceId(body.DeviceId);

        if (!_ctx.MessageLimiter.TryAcquire(request.RemoteIp, out var retryAfter))
        {
            throw new ApiException(429, "too_many_messages", $"发送过于频繁，请 {retryAfter} 秒后再试。");
        }

        var view = _ctx.Store.Add(text, deviceId, body.DeviceName, request.RemoteIp);
        _ctx.Log($"收到来自 {view.DeviceName}（{view.Ip}）的消息 #{view.Id}");
        return HttpResponse.Json(new { ok = true, message = view }, 201);
    }

    private HttpResponse CompleteMessage(HttpRequest request, string idSegment)
    {
        Authenticate(request);
        var id = ParseLong(idSegment);
        var body = request.BodyJson<CompleteRequest>() ?? new CompleteRequest { Completed = true };
        var view = _ctx.Store.SetCompleted(id, body.Completed) ?? throw ApiException.NotFound("消息不存在。");
        _ctx.Log($"消息 #{id} 已标记为{(view.Completed ? "已完成展示" : "未完成展示")}");
        return HttpResponse.Json(new { ok = true, message = view });
    }

    private HttpResponse ListDevices(HttpRequest request)
    {
        Authenticate(request);
        return HttpResponse.Json(new { ok = true, devices = _ctx.Store.Devices() });
    }

    private HttpResponse RenameDevice(HttpRequest request, string deviceId)
    {
        Authenticate(request);
        var body = request.BodyJson<RenameDeviceRequest>() ?? throw ApiException.BadRequest("缺少请求体。");
        if (!string.IsNullOrWhiteSpace(body.RequesterId) &&
            !string.Equals(body.RequesterId, deviceId, StringComparison.Ordinal))
        {
            throw ApiException.Forbidden("只能修改本机的设备名。");
        }

        var name = (body.Name ?? "").Trim();
        if (name.Length == 0)
        {
            throw ApiException.BadRequest("设备名不能为空。", "empty_name");
        }

        if (!_ctx.Store.RenameDevice(deviceId, name))
        {
            // 设备还没发过消息时，允许先建立一条设备记录
            _ctx.Store.EnsureDevice(deviceId, name, request.RemoteIp);
        }

        var device = _ctx.Store.Devices().FirstOrDefault(d => d.Id == deviceId);
        _ctx.Log($"设备已改名为「{device?.Name}」");
        return HttpResponse.Json(new { ok = true, device });
    }

    // ---------------- 工具 ----------------

    private SessionRecord Authenticate(HttpRequest request)
    {
        var token = request.Cookie(CoreDefaults.CookieName);
        if (!_ctx.Sessions.TryValidate(token, out var record))
        {
            throw ApiException.Unauthorized();
        }

        return record!;
    }

    private bool IsAuthenticated(HttpRequest request)
    {
        return _ctx.Sessions.TryValidate(request.Cookie(CoreDefaults.CookieName), out _);
    }

    private static bool TryStatic(string path, HttpRequest request, out HttpResponse response)
    {
        response = HttpResponse.NoContent();
        string? logical = path switch
        {
            "/app.js" => "lan/app.js",
            "/style.css" => "lan/style.css",
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

    /// <summary>
    /// Chromium 系浏览器对 Cookie 有效期有约 400 天的上限，超过会被截断，这里主动按上限写。
    /// </summary>
    private const int MaxCookieAgeSeconds = 400 * 24 * 60 * 60;

    private static string BuildCookie(string token, DateTimeOffset? expiresAt)
    {
        var builder = new StringBuilder();
        builder.Append(CoreDefaults.CookieName).Append('=').Append(token)
            .Append("; Path=/; HttpOnly; SameSite=Lax");
        if (expiresAt is { } expires)
        {
            var remaining = expires == DateTimeOffset.MaxValue
                ? double.MaxValue
                : (expires - DateTimeOffset.UtcNow).TotalSeconds;
            var seconds = (int)Math.Clamp(remaining, 0, MaxCookieAgeSeconds);
            builder.Append("; Max-Age=").Append(seconds.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string BuildClearCookie()
    {
        return CoreDefaults.CookieName + "=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0";
    }

    internal static string NormalizeDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw ApiException.BadRequest("缺少设备标识。", "missing_device_id");
        }

        var trimmed = deviceId.Trim();
        if (trimmed.Length > 64 || !trimmed.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
        {
            throw ApiException.BadRequest("设备标识格式不正确。", "invalid_device_id");
        }

        return trimmed;
    }

    internal static string[] SplitSegments(string path)
    {
        return path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    internal static MessageFilter ParseFilter(string? filter)
    {
        return filter?.ToLowerInvariant() switch
        {
            "pending" or "uncompleted" or "todo" => MessageFilter.Pending,
            "done" or "completed" => MessageFilter.Completed,
            _ => MessageFilter.All,
        };
    }

    internal static int ParseInt(string? text, int fallback, int min, int max)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }

    internal static long ParseLong(string text)
    {
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw ApiException.BadRequest("标识格式不正确。", "invalid_id");
        }

        return value;
    }
}
