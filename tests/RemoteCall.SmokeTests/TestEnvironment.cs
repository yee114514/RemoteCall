using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using RemoteCall.Core.Auth;
using RemoteCall.Core.Config;
using RemoteCall.Core.Data;
using RemoteCall.Core.Http;
using RemoteCall.Core.Server;

namespace RemoteCall.SmokeTests;

/// <summary>
/// 每个用例一份独立环境：临时目录 + 两个随机端口的 HTTP 服务 + 已登录 Cookie。
/// </summary>
internal sealed class TestEnvironment : IAsyncDisposable
{
    public const string Password = "test1234";

    private TestEnvironment()
    {
    }

    public string Folder { get; private set; } = "";
    public PluginSettingsService Settings { get; private set; } = null!;
    public MessageStore Store { get; private set; } = null!;
    public SessionStore Sessions { get; private set; } = null!;
    public ServerStatus Status { get; } = new();
    public LogBuffer Logs { get; } = new();
    public WebContext Context { get; private set; } = null!;
    public LanApi LanApi { get; private set; } = null!;
    public AdminApi AdminApi { get; private set; } = null!;
    public HttpServer LanServer { get; private set; } = null!;
    public HttpServer AdminServer { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public string Cookie { get; private set; } = "";
    public string DeviceId { get; } = "test-device-1";

    public static async Task<TestEnvironment> CreateAsync()
    {
        var env = new TestEnvironment
        {
            Folder = Path.Combine(Path.GetTempPath(), "remotecall-tests-" + Guid.NewGuid().ToString("N")),
        };
        Directory.CreateDirectory(env.Folder);

        env.Settings = new PluginSettingsService(env.Folder, message => env.Logs.Add(message));
        env.Settings.SetPassword(Password, out _);
        env.Store = new MessageStore(env.Folder, message => env.Logs.Add(message));
        env.Sessions = new SessionStore(Path.Combine(env.Folder, "sessions.json"), message => env.Logs.Add(message));
        env.Context = new WebContext(env.Settings, env.Store, env.Sessions, env.Status, env.Logs, message => env.Logs.Add(message));
        env.LanApi = new LanApi(env.Context);
        env.AdminApi = new AdminApi(env.Context);

        env.LanServer = new HttpServer(IPAddress.Loopback, 0, "test-lan", env.LanApi.Handle, message => env.Logs.Add(message));
        env.LanServer.Start();
        env.AdminServer = new HttpServer(IPAddress.Loopback, 0, "test-admin", env.AdminApi.Handle, message => env.Logs.Add(message));
        env.AdminServer.Start();

        env.Client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        env.Cookie = await env.LoginAsync(Password, remember: true);
        return env;
    }

    public async Task<string> LoginAsync(string password, bool remember)
    {
        var (status, body, response) = await RequestAsync(
            LanServer.BoundPort,
            "POST",
            "/api/login",
            System.Text.Json.JsonSerializer.Serialize(new { password, remember }),
            cookie: null);

        if (status != 200)
        {
            throw new InvalidOperationException($"登录失败：{status} {body}");
        }

        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault() ?? ""
            : "";
        var cookie = setCookie.Split(';')[0];
        Context.LoginLimiter.Reset("127.0.0.1");
        return cookie;
    }

    public Task<(int Status, string Body, HttpResponseMessage Response)> LanAsync(string method, string path, string? json = null, string? cookie = null)
    {
        return RequestAsync(LanServer.BoundPort, method, path, json, cookie ?? Cookie);
    }

    public Task<(int Status, string Body, HttpResponseMessage Response)> LanAnonymousAsync(string method, string path, string? json = null)
    {
        return RequestAsync(LanServer.BoundPort, method, path, json, cookie: null);
    }

    public Task<(int Status, string Body, HttpResponseMessage Response)> AdminAsync(string method, string path, string? json = null)
    {
        return RequestAsync(AdminServer.BoundPort, method, path, json, cookie: null);
    }

    public async Task<(int Status, string Body, HttpResponseMessage Response)> RequestAsync(int port, string method, string path, string? json, string? cookie)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{port}{path}");
        if (json != null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (!string.IsNullOrEmpty(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        var response = await Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, body, response);
    }

    public async Task<long> SendMessageAsync(string text, string? deviceName = "测试机", string deviceId = "test-device-1")
    {
        var (status, body, _) = await LanAsync("POST", "/api/messages", System.Text.Json.JsonSerializer.Serialize(new
        {
            text,
            deviceId,
            deviceName,
        }));

        if (status != 201)
        {
            throw new InvalidOperationException($"发送消息失败：{status} {body}");
        }

        using var document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.GetProperty("message").GetProperty("id").GetInt64();
    }

    /// <summary>发原始 TCP 请求，用于验证协议细节。返回服务端写回的全部文本。</summary>
    public async Task<string> RawAsync(int port, string rawRequest, int readMilliseconds = 1200)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        var payload = Encoding.ASCII.GetBytes(rawRequest);
        await stream.WriteAsync(payload);

        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        var deadline = DateTime.UtcNow.AddMilliseconds(readMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            var remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
            using var cts = new CancellationTokenSource(remaining);
            try
            {
                var read = await stream.ReadAsync(buffer, cts.Token);
                if (read <= 0)
                {
                    break;
                }

                memory.Write(buffer, 0, read);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            LanServer.Stop();
            LanServer.Dispose();
            AdminServer.Stop();
            AdminServer.Dispose();
        }
        catch
        {
            // 忽略
        }

        Client.Dispose();

        try
        {
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }
        }
        catch
        {
            // 忽略（临时目录清理失败不影响测试结果）
        }

        await Task.CompletedTask;
    }
}
