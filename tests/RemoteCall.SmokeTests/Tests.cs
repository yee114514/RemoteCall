using System.Text.Json;
using RemoteCall.Core.Auth;
using RemoteCall.Core.Config;
using RemoteCall.Core.Data;
using RemoteCall.Core.Server;

namespace RemoteCall.SmokeTests;

internal static class Tests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ---------------- 密码与设置 ----------------

    public static async Task PasswordHashingAsync(TestEnvironment env)
    {
        var (salt, hash) = PasswordHasher.Create("abc123", 1000);
        var saltText = Convert.ToBase64String(salt);
        var hashText = Convert.ToBase64String(hash);
        Assert.True(PasswordHasher.Verify("abc123", saltText, hashText, 1000), "正确密码应校验通过");
        Assert.True(!PasswordHasher.Verify("abc124", saltText, hashText, 1000), "错误密码不应通过");

        var (salt2, _) = PasswordHasher.Create("abc123", 1000);
        Assert.True(!salt.SequenceEqual(salt2), "每次生成的盐应不同");

        Assert.True(PasswordHasher.IsAcceptablePassword("abcd"), "4 位密码应合法");
        Assert.True(!PasswordHasher.IsAcceptablePassword("abc"), "3 位密码应被拒绝");
        Assert.True(!PasswordHasher.IsAcceptablePassword(new string('x', 65)), "超长密码应被拒绝");
        await Task.CompletedTask;
    }

    public static async Task SettingsAsync(TestEnvironment env)
    {
        var ok = env.Settings.Update(settings =>
        {
            settings.LanPort = 25100;
            settings.AdminPort = 25101;
            settings.SessionDays = 7;
        }, out var error);
        Assert.True(ok, "合法设置应保存成功：" + error);

        var reloaded = new PluginSettingsService(env.Folder);
        Assert.Equal(25100, reloaded.Current.LanPort, "端口应持久化");
        Assert.Equal(7, reloaded.Current.SessionDays, "会话天数应持久化");

        ok = env.Settings.Update(settings => settings.AdminPort = settings.LanPort, out error);
        Assert.True(!ok && !string.IsNullOrEmpty(error), "两个端口相同时应被拒绝");

        ok = env.Settings.Update(settings => settings.LanPort = 70000, out error);
        Assert.True(!ok && !string.IsNullOrEmpty(error), "端口越界应被拒绝");

        // -1 表示不限制
        ok = env.Settings.Update(settings =>
        {
            settings.SessionDays = CoreDefaults.Unlimited;
            settings.MessageRetention = CoreDefaults.Unlimited;
        }, out error);
        Assert.True(ok, "-1（不限制）应被接受：" + error);
        Assert.Equal(CoreDefaults.Unlimited, env.Settings.Current.SessionDays, "会话天数应保存为 -1");
        Assert.Equal(CoreDefaults.Unlimited, env.Settings.Current.MessageRetention, "保留条数应保存为 -1");
        var reloadedUnlimited = new PluginSettingsService(env.Folder);
        Assert.Equal(CoreDefaults.Unlimited, reloadedUnlimited.Current.SessionDays, "-1 应持久化");
        Assert.Equal(CoreDefaults.Unlimited, reloadedUnlimited.Current.MessageRetention, "-1 应持久化");
        ok = env.Settings.Update(settings => settings.SessionDays = 7, out error);
        Assert.True(ok, "应能从 -1 改回具体天数");

        Assert.True(env.Settings.VerifyPassword(TestEnvironment.Password), "已设置的密码应校验通过");
        Assert.True(!env.Settings.VerifyPassword("wrong-password"), "错误密码不应通过");

        var generated = env.Settings.GenerateAndSetPassword(out var generateError);
        Assert.True(generateError == null && generated.Length == 8, "应能生成 8 位随机密码");
        Assert.True(env.Settings.VerifyPassword(generated), "生成的密码应校验通过");
        Assert.Equal(generated, env.Settings.Current.GeneratedPassword, "自动生成的密码应保存下来供查看");
        await Task.CompletedTask;
    }

    // ---------------- 页面与静态资源 ----------------

    public static async Task StaticPagesAsync(TestEnvironment env)
    {
        var (status, body, _) = await env.LanAnonymousAsync("GET", "/login");
        Assert.Equal(200, status, "登录页应返回 200");
        Assert.Contains(body, "访问密码", "登录页应包含密码输入框");

        (status, body, _) = await env.LanAnonymousAsync("GET", "/style.css");
        Assert.Equal(200, status, "样式表应返回 200");

        (status, body, _) = await env.LanAnonymousAsync("GET", "/app.js");
        Assert.Equal(200, status, "脚本应返回 200");
        Assert.Contains(body, "remotecall.deviceId", "脚本应包含设备标识逻辑");

        (status, body, _) = await env.AdminAsync("GET", "/");
        Assert.Equal(200, status, "本机管理页应返回 200");
        Assert.Contains(body, "本机管理", "管理页标题");
    }

    public static async Task RedirectAsync(TestEnvironment env)
    {
        var (status, _, response) = await env.LanAnonymousAsync("GET", "/");
        Assert.Equal(302, status, "未登录访问首页应跳转");
        Assert.Equal("/login", response.Headers.Location?.ToString(), "应跳转到 /login");

        (status, _, _) = await env.LanAsync("GET", "/");
        Assert.Equal(200, status, "已登录访问首页应返回应用页");
    }

    // ---------------- 登录与会话 ----------------

    public static async Task LoginAsync(TestEnvironment env)
    {
        var (status, body, _) = await env.LanAnonymousAsync("GET", "/api/session");
        Assert.Equal(401, status, "未登录访问 /api/session 应 401");
        Assert.Contains(body, "unauthorized", "应返回未授权错误码");

        (status, body, _) = await env.LanAsync("GET", "/api/session?deviceId=" + env.DeviceId);
        Assert.Equal(200, status, "已登录访问 /api/session 应 200");
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.GetProperty("authenticated").GetBoolean(), "应标记为已登录");
        Assert.Equal("127.0.0.1", document.RootElement.GetProperty("yourIp").GetString(), "应返回请求来源 IP");

        var wrong = JsonSerializer.Serialize(new { password = "wrong-password", remember = false });
        (status, _, _) = await env.LanAnonymousAsync("POST", "/api/login", wrong);
        Assert.Equal(401, status, "错误密码应返回 401");
    }

    public static async Task CookieAsync(TestEnvironment env)
    {
        var notRemembered = JsonSerializer.Serialize(new { password = TestEnvironment.Password, remember = false });
        var (status, _, response) = await env.LanAnonymousAsync("POST", "/api/login", notRemembered);
        Assert.Equal(200, status, "不记住登录也应登录成功");
        var cookie = response.Headers.GetValues("Set-Cookie").First();
        Assert.True(!cookie.Contains("Max-Age", StringComparison.OrdinalIgnoreCase), "不记住登录时不应带 Max-Age");
        Assert.True(cookie.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase), "会话 Cookie 应为 HttpOnly");

        var remembered = JsonSerializer.Serialize(new { password = TestEnvironment.Password, remember = true });
        (status, _, response) = await env.LanAnonymousAsync("POST", "/api/login", remembered);
        Assert.Equal(200, status, "记住登录应成功");
        cookie = response.Headers.GetValues("Set-Cookie").First();
        Assert.True(cookie.Contains("Max-Age", StringComparison.OrdinalIgnoreCase), "记住登录时应带 Max-Age");

        // 会话应持久化：换一个 SessionStore 读同一个文件也能校验
        var reloaded = new SessionStore(Path.Combine(env.Folder, "sessions.json"));
        var token = env.Cookie[(env.Cookie.IndexOf('=') + 1)..];
        Assert.True(reloaded.TryValidate(token, out _), "会话应持久化到磁盘");

        var (logoutStatus, _, logoutResponse) = await env.LanAsync("POST", "/api/logout");
        Assert.Equal(204, logoutStatus, "注销应返回 204");
        Assert.True(logoutResponse.Headers.GetValues("Set-Cookie").First().Contains("Max-Age=0"), "注销应清除 Cookie");
        (status, _, _) = await env.LanAsync("GET", "/api/session");
        Assert.Equal(401, status, "注销后旧 Cookie 应失效");
    }

    public static async Task LoginRateLimitAsync(TestEnvironment env)
    {
        env.Context.LoginLimiter.Reset("127.0.0.1");
        var payload = JsonSerializer.Serialize(new { password = "wrong-password", remember = false });
        for (var i = 0; i < CoreDefaults.LoginFailuresPerMinute; i++)
        {
            var (status, _, _) = await env.LanAnonymousAsync("POST", "/api/login", payload);
            Assert.Equal(401, status, $"第 {i + 1} 次错误密码应返回 401");
        }

        var (limited, _, _) = await env.LanAnonymousAsync("POST", "/api/login", payload);
        Assert.Equal(429, limited, "超过限流阈值应返回 429");
        env.Context.LoginLimiter.Reset("127.0.0.1");
    }

    // ---------------- 消息 ----------------

    public static async Task SendMessageAsync(TestEnvironment env)
    {
        var id = await env.SendMessageAsync("第一条消息");
        Assert.True(id > 0, "消息应获得正数 id");

        var (status, body, _) = await env.LanAsync("GET", "/api/messages?filter=pending");
        Assert.Equal(200, status, "拉取消息应 200");
        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength(), "应只有一条消息");
        var first = messages[0];
        Assert.Equal("第一条消息", first.GetProperty("text").GetString(), "消息内容");
        Assert.Equal("测试机", first.GetProperty("deviceName").GetString(), "设备名");
        Assert.Equal("127.0.0.1", first.GetProperty("ip").GetString(), "发送者 IP");
        Assert.True(!first.GetProperty("completed").GetBoolean(), "新消息应为未完成");
        Assert.Equal(1, document.RootElement.GetProperty("total").GetInt32(), "总数应为 1");

        var empty = JsonSerializer.Serialize(new { text = "   ", deviceId = env.DeviceId, deviceName = "测试机" });
        (status, _, _) = await env.LanAsync("POST", "/api/messages", empty);
        Assert.Equal(400, status, "空消息应被拒绝");

        var tooLong = JsonSerializer.Serialize(new { text = new string('x', CoreDefaults.MaxMessageLength + 1), deviceId = env.DeviceId });
        (status, _, _) = await env.LanAsync("POST", "/api/messages", tooLong);
        Assert.Equal(400, status, "超长消息应被拒绝");

        var noDevice = JsonSerializer.Serialize(new { text = "没有设备标识" });
        (status, _, _) = await env.LanAsync("POST", "/api/messages", noDevice);
        Assert.Equal(400, status, "缺少设备标识应被拒绝");

        var badJson = "this is not json";
        (status, _, _) = await env.LanAsync("POST", "/api/messages", badJson);
        Assert.Equal(400, status, "非法 JSON 应被拒绝");

        var anonymous = JsonSerializer.Serialize(new { text = "未登录", deviceId = env.DeviceId });
        (status, _, _) = await env.LanAnonymousAsync("POST", "/api/messages", anonymous);
        Assert.Equal(401, status, "未登录发送应 401");
    }

    public static async Task CompleteMessageAsync(TestEnvironment env)
    {
        var id = await env.SendMessageAsync("需要展示的消息");

        var complete = JsonSerializer.Serialize(new { completed = true });
        var (status, body, _) = await env.LanAsync("POST", $"/api/messages/{id}/complete", complete);
        Assert.Equal(200, status, "标记完成应 200");
        using (var document = JsonDocument.Parse(body))
        {
            var message = document.RootElement.GetProperty("message");
            Assert.True(message.GetProperty("completed").GetBoolean(), "应标记为已完成");
            Assert.True(message.GetProperty("completedAt").ValueKind != JsonValueKind.Null, "应记录完成时间");
        }

        (status, body, _) = await env.LanAsync("GET", "/api/messages?filter=done");
        Assert.Equal(200, status);
        Assert.Contains(body, "需要展示的消息", "已完成列表应包含该消息");

        (status, body, _) = await env.LanAsync("GET", "/api/messages?filter=pending");
        Assert.NotContains(body, "需要展示的消息", "未完成列表不应包含已完成消息");

        var undo = JsonSerializer.Serialize(new { completed = false });
        (status, _, _) = await env.LanAsync("POST", $"/api/messages/{id}/complete", undo);
        Assert.Equal(200, status, "撤销完成应 200");

        (status, _, _) = await env.LanAsync("POST", "/api/messages/999999/complete", complete);
        Assert.Equal(404, status, "不存在的消息应 404");
    }

    public static async Task RenameDeviceAsync(TestEnvironment env)
    {
        await env.SendMessageAsync("改名前发送的消息");

        var rename = JsonSerializer.Serialize(new { name = "新设备名", requesterId = env.DeviceId });
        var (status, body, _) = await env.LanAsync("PUT", $"/api/devices/{env.DeviceId}", rename);
        Assert.Equal(200, status, "改设备名应 200");
        using (var document = JsonDocument.Parse(body))
        {
            Assert.Equal("新设备名", document.RootElement.GetProperty("device").GetProperty("name").GetString(), "返回新设备名");
        }

        (status, body, _) = await env.LanAsync("GET", "/api/messages");
        Assert.Contains(body, "新设备名", "历史消息里的设备名应同步更新");

        var forbidden = JsonSerializer.Serialize(new { name = "别人的名字", requesterId = "another-device" });
        (status, _, _) = await env.LanAsync("PUT", $"/api/devices/{env.DeviceId}", forbidden);
        Assert.Equal(403, status, "改别人的设备名应 403");

        (status, _, _) = await env.LanAsync("PUT", $"/api/devices/{env.DeviceId}", JsonSerializer.Serialize(new { name = "  ", requesterId = env.DeviceId }));
        Assert.Equal(400, status, "空设备名应 400");

        // 还没发过消息的设备也能先改名（自动建立记录）
        (status, _, _) = await env.LanAsync("PUT", "/api/devices/brand-new-device", JsonSerializer.Serialize(new { name = "新来的设备", requesterId = "brand-new-device" }));
        Assert.Equal(200, status, "新设备应能直接改名");
    }

    // ---------------- 本机管理页 ----------------

    public static async Task AdminStateAsync(TestEnvironment env)
    {
        await env.SendMessageAsync("管理页可见的消息");
        var (status, body, _) = await env.AdminAsync("GET", "/api/state");
        Assert.Equal(200, status, "管理页状态应 200");
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.True(root.GetProperty("settings").GetProperty("passwordSet").GetBoolean(), "应显示密码已设置");
        Assert.Equal(1, root.GetProperty("messages").GetArrayLength(), "管理页应能看到历史消息");
        Assert.True(root.GetProperty("devices").GetArrayLength() >= 1, "管理页应能看到设备");
        Assert.True(root.GetProperty("status").TryGetProperty("adminPort", out _), "应包含服务状态");
    }

    public static async Task AdminPasswordAsync(TestEnvironment env)
    {
        var (status, _, _) = await env.AdminAsync("POST", "/api/password", JsonSerializer.Serialize(new { password = "newpass99" }));
        Assert.Equal(200, status, "改密码应 200");

        var (oldStatus, _, _) = await env.LanAsync("GET", "/api/session");
        Assert.Equal(401, oldStatus, "改密码后旧会话应失效");

        var cookie = await env.LoginAsync("newpass99", remember: true);
        var (newStatus, _, _) = await env.RequestAsync(env.LanServer.BoundPort, "GET", "/api/session", null, cookie);
        Assert.Equal(200, newStatus, "新密码登录后应可用");

        (status, _, _) = await env.AdminAsync("POST", "/api/password", JsonSerializer.Serialize(new { password = "abc" }));
        Assert.Equal(400, status, "过短的密码应被拒绝");

        (status, _, _) = await env.AdminAsync("POST", "/api/password/generate", "{}");
        Assert.Equal(200, status, "生成随机密码应 200");
        Assert.Equal(8, env.Settings.Current.GeneratedPassword.Length, "生成的密码应按预期保存");
    }

    public static async Task AdminMessagesAsync(TestEnvironment env)
    {
        var id = await env.SendMessageAsync("待删除的消息");
        var (status, _, _) = await env.AdminAsync("POST", $"/api/messages/{id}/delete");
        Assert.Equal(200, status, "删除消息应 200");

        var (_, body, _) = await env.AdminAsync("GET", "/api/state");
        Assert.NotContains(body, "待删除的消息", "删除后不应再出现");

        (status, _, _) = await env.AdminAsync("POST", $"/api/messages/{id}/delete");
        Assert.Equal(404, status, "删除不存在的消息应 404");

        var done = await env.SendMessageAsync("已完成的消息");
        var pending = await env.SendMessageAsync("未完成的消息");
        (status, _, _) = await env.AdminAsync("POST", $"/api/messages/{done}/complete", JsonSerializer.Serialize(new { completed = true }));
        Assert.Equal(200, status, "管理页标记完成应 200");

        (status, _, _) = await env.AdminAsync("POST", "/api/messages/clear", JsonSerializer.Serialize(new { scope = "done" }));
        Assert.Equal(200, status, "清空已完成应 200");
        (_, body, _) = await env.AdminAsync("GET", "/api/state");
        Assert.NotContains(body, "已完成的消息", "已完成消息应被清空");
        Assert.Contains(body, "未完成的消息", "未完成消息应保留");

        (status, _, _) = await env.AdminAsync("POST", "/api/messages/clear", JsonSerializer.Serialize(new { scope = "all" }));
        Assert.Equal(200, status, "清空全部应 200");
        Assert.Equal(0, env.Store.Count, "清空后存储应为空");
        _ = pending;
    }

    public static async Task AdminSettingsAsync(TestEnvironment env)
    {
        var payload = JsonSerializer.Serialize(new
        {
            lanEnabled = true,
            lanPort = 26100,
            adminPort = 26101,
            sessionDays = 13,
            notificationEnabled = false,
            notificationMaskSeconds = 2.5,
            notificationOverlaySeconds = 9.0,
            notificationSpeech = true,
            messageRetention = 300,
        });

        var (status, _, _) = await env.AdminAsync("POST", "/api/settings", payload);
        Assert.Equal(200, status, "保存设置应 200");

        var settings = env.Settings.Current;
        Assert.Equal(26100, settings.LanPort, "局域网端口应更新");
        Assert.Equal(26101, settings.AdminPort, "管理端口应更新");
        Assert.Equal(13, settings.SessionDays, "会话天数应更新");
        Assert.True(!settings.NotificationEnabled, "提醒开关应更新");
        Assert.Equal(2.5, settings.NotificationMaskSeconds, "遮罩时长应更新");
        Assert.Equal(300, settings.MessageRetention, "保留条数应更新");

        var invalid = JsonSerializer.Serialize(new { lanPort = 26101, adminPort = 26101 });
        (status, _, _) = await env.AdminAsync("POST", "/api/settings", invalid);
        Assert.Equal(400, status, "端口冲突应被拒绝");

        (status, _, _) = await env.AdminAsync("POST", "/api/selftest", "{}");
        Assert.Equal(200, status, "插入测试消息应 200");
        Assert.Equal(1, env.Store.Count, "测试消息应写入存储");
    }

    // ---------------- HTTP 协议细节 ----------------

    public static async Task ProtocolAsync(TestEnvironment env)
    {
        var (status, _, _) = await env.LanAsync("GET", "/api/does-not-exist");
        Assert.Equal(404, status, "未知路径应 404");

        (status, _, _) = await env.LanAsync("DELETE", "/api/messages");
        Assert.Equal(405, status, "不支持的方法应 405");

        var raw = await env.RawAsync(env.LanServer.BoundPort,
            "POST /api/login HTTP/1.1\r\nHost: localhost\r\nContent-Length: 999999\r\n\r\n");
        Assert.Contains(raw, "413", "声明超大请求体应返回 413");

        var pipelined = await env.RawAsync(env.LanServer.BoundPort,
            "GET /api/health HTTP/1.1\r\nHost: localhost\r\n\r\n" +
            "GET /api/health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        Assert.Equal(2, CountOccurrences(pipelined, "HTTP/1.1 200"), "同一连接应处理两个 keep-alive 请求");

        var head = await env.RawAsync(env.LanServer.BoundPort,
            "HEAD /api/health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        Assert.Contains(head, "200 OK", "HEAD 应返回 200");
        Assert.NotContains(head, "\"ok\"", "HEAD 不应返回响应体");

        var noContentLength = await env.RawAsync(env.LanServer.BoundPort,
            "POST /api/login HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        Assert.Contains(noContentLength, "400", "没有请求体的登录请求应返回 400");
    }

    // ---------------- 存储 ----------------

    public static async Task DefaultPasswordAsync(TestEnvironment env)
    {
        var folder = Path.Combine(env.Folder, "default-password");
        Directory.CreateDirectory(folder);
        var service = new PluginSettingsService(folder);

        Assert.Equal(CoreDefaults.DefaultPassword, service.Current.GeneratedPassword, "首次启动应使用默认密码");
        Assert.True(service.VerifyPassword("admin"), "默认密码 admin 应能登录");

        service.SetPassword("my-own-password", out var error);
        Assert.True(error == null, "设置自定义密码应成功：" + error);
        Assert.Equal("", service.Current.GeneratedPassword, "改密后应清空默认密码明文");
        Assert.True(service.VerifyPassword("my-own-password"), "自定义密码应可用");
        Assert.True(!service.VerifyPassword("admin"), "改密后默认密码应失效");
        await Task.CompletedTask;
    }

    public static async Task LanAddressesAsync(TestEnvironment env)
    {
        var addresses = LanAddresses.GetIPv4();
        foreach (var address in addresses)
        {
            Assert.True(System.Net.IPAddress.TryParse(address, out var parsed) && parsed != null, "局域网地址应可解析：" + address);
            Assert.True(parsed!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork, "应只有 IPv4：" + address);
            Assert.True(!address.StartsWith("169.254.", StringComparison.Ordinal), "不应包含 APIPA 地址：" + address);
        }

        Assert.Equal(addresses.Count, addresses.Distinct().Count(), "地址不应重复");
        await Task.CompletedTask;
    }

    public static async Task UnlimitedAsync(TestEnvironment env)
    {
        // 不限制的会话：永不过期
        var sessionPath = Path.Combine(env.Folder, "sessions-unlimited.json");
        var sessions = new SessionStore(sessionPath);
        var (token, expiresAt) = sessions.Create(TimeSpan.MaxValue, "127.0.0.1");
        Assert.Equal(DateTimeOffset.MaxValue, expiresAt, "不限制的会话应永不过期");
        Assert.True(sessions.TryValidate(token, out var record), "不限制的会话应有效");
        Assert.Equal(DateTimeOffset.MaxValue, record!.ExpiresAt, "落盘的会话也应永不过期");

        var reloaded = new SessionStore(sessionPath);
        Assert.True(reloaded.TryValidate(token, out _), "重启后不限制的会话仍有效");

        // 历史保留条数不限制：超过任何阈值都不裁剪
        var storeFolder = Path.Combine(env.Folder, "store-unlimited");
        Directory.CreateDirectory(storeFolder);
        var store = new MessageStore(storeFolder);
        store.SetRetention(CoreDefaults.Unlimited);
        for (var i = 0; i < 60; i++)
        {
            store.Add("消息 " + i, "dev-" + i, "设备" + i, "10.0.0." + (i % 20 + 1));
        }

        Assert.Equal(60, store.Count, "不限制时不应裁剪消息");
        await Task.CompletedTask;
    }

    public static async Task StoreAsync(TestEnvironment env)
    {
        var store = new MessageStore(env.Folder);
        store.SetRetention(50);
        for (var i = 0; i < 55; i++)
        {
            store.Add("消息 " + i, "device-" + i, "设备" + i, "10.0.0." + (i % 20 + 1));
        }

        Assert.Equal(50, store.Count, "应按保留条数裁剪");

        var reloaded = new MessageStore(env.Folder);
        Assert.Equal(50, reloaded.Count, "重启后消息应仍在");
        Assert.Equal(55, reloaded.Query(MessageFilter.All, 1)[0].Id, "最新消息应排在最前面");

        // 设备名联查
        var deviceId = reloaded.Query(MessageFilter.All, 1)[0].DeviceId;
        Assert.True(reloaded.RenameDevice(deviceId, "改过的名字"), "改名应成功");
        var view = reloaded.Query(MessageFilter.All, 1)[0];
        Assert.Equal("改过的名字", view.DeviceName, "历史消息应显示新设备名");
        await Task.CompletedTask;
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
