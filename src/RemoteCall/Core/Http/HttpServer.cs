using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RemoteCall.Core.Http;

/// <summary>
/// 极简 HTTP/1.1 服务器：基于 <see cref="TcpListener"/> 手写实现。
/// <para>
/// 之所以不用 <see cref="HttpListener"/>：在 Windows 上监听 localhost 以外的前缀需要管理员权限或
/// <c>netsh http add urlacl</c>；而 ASP.NET Core 的共享框架又不在 ClassIsland 的运行时配置里。
/// 自己开 TCP 端口普通权限即可，且能精确绑到 0.0.0.0 / 127.0.0.1。
/// </para>
/// 支持：GET/POST/PUT/DELETE/HEAD、Content-Length 请求体、Cookie、keep-alive、ETag 304。
/// 不支持：chunked、HTTP/2、TLS、压缩。
/// </summary>
public sealed class HttpServer : IDisposable
{
    private const int MaxRequestLineBytes = 8 * 1024;
    private const int MaxHeaderLineBytes = 8 * 1024;
    private const int MaxHeaderCount = 100;
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxRequestsPerConnection = 100;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(15);

    private readonly IPAddress _address;
    private readonly int _port;
    private readonly Func<HttpRequest, HttpResponse> _handler;
    private readonly Action<string> _log;
    private readonly object _sync = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private int _activeConnections;
    private long _requestCount;
    private int _boundPort;

    public HttpServer(IPAddress address, int port, string name, Func<HttpRequest, HttpResponse> handler, Action<string>? log = null)
    {
        _address = address;
        _port = port;
        Name = name;
        _handler = handler;
        _log = log ?? (_ => { });
    }

    public string Name { get; }

    public int Port => _port;

    /// <summary>
    /// 实际监听的端口（<see cref="Port"/> 为 0 时由系统分配）。
    /// </summary>
    public int BoundPort => _boundPort > 0 ? _boundPort : _port;

    public bool IsRunning => _listener != null;

    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    public long RequestCount => Interlocked.Read(ref _requestCount);

    /// <summary>
    /// 开始监听。绑定失败会抛出 <see cref="SocketException"/>（例如端口被占用）。
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_listener != null)
            {
                return;
            }

            var listener = new TcpListener(_address, _port);
            listener.Start(128);
            _boundPort = (listener.LocalEndpoint as IPEndPoint)?.Port ?? _port;
            _listener = listener;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _acceptTask = Task.Run(() => AcceptLoopAsync(listener, token), CancellationToken.None);
            _log($"[{Name}] 已开始监听 {_address}:{_port}");
        }
    }

    /// <summary>
    /// 停止监听并关闭所有连接。
    /// </summary>
    public void Stop()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        Task? acceptTask;
        lock (_sync)
        {
            listener = _listener;
            cts = _cts;
            acceptTask = _acceptTask;
            _listener = null;
            _cts = null;
            _acceptTask = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // 忽略
        }

        try
        {
            listener?.Stop();
        }
        catch
        {
            // 忽略
        }

        try
        {
            acceptTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 忽略
        }

        cts?.Dispose();
        if (listener != null)
        {
            _log($"[{Name}] 已停止监听 {_address}:{_port}");
        }
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                _log($"[{Name}] 接受连接失败：{ex.Message}");
                try
                {
                    await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // 忽略
                }

                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        Interlocked.Increment(ref _activeConnections);
        var remoteIp = IpAddressUtil.Normalize((client.Client.RemoteEndPoint as IPEndPoint)?.Address);
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            var reader = new BufferedStreamReader(stream);

            for (var index = 0; index < MaxRequestsPerConnection; index++)
            {
                if (serverToken.IsCancellationRequested)
                {
                    break;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                timeout.CancelAfter(ReadTimeout);

                HttpRequest request;
                try
                {
                    var parsed = await ReadRequestAsync(reader, remoteIp, timeout.Token).ConfigureAwait(false);
                    if (parsed == null)
                    {
                        break;
                    }

                    request = parsed;
                }
                catch (HttpProtocolException ex)
                {
                    await SafeWriteAsync(stream, HttpResponse.Error(ex.StatusCode, "protocol_error", ex.Message), false, "GET").ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                Interlocked.Increment(ref _requestCount);

                HttpResponse response;
                try
                {
                    response = _handler(request);
                }
                catch (ApiException ex)
                {
                    response = HttpResponse.Error(ex.StatusCode, ex.Code, ex.Message);
                }
                catch (Exception ex)
                {
                    _log($"[{Name}] 处理 {request.Method} {request.Path} 时出错：{ex}");
                    response = HttpResponse.Error(500, "internal_error", "服务器内部错误。");
                }

                var keepAlive = request.KeepAlive && index + 1 < MaxRequestsPerConnection;
                var written = await SafeWriteAsync(stream, response, keepAlive, request.Method).ConfigureAwait(false);
                if (!written || !keepAlive)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _log($"[{Name}] 连接处理异常：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
            try
            {
                client.Close();
            }
            catch
            {
                // 忽略
            }
        }
    }

    private async Task<bool> SafeWriteAsync(Stream stream, HttpResponse response, bool keepAlive, string method, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WriteTimeout);
        try
        {
            await WriteResponseAsync(stream, response, keepAlive, method, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static async Task WriteResponseAsync(Stream stream, HttpResponse response, bool keepAlive, string method, CancellationToken ct)
    {
        var isHead = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
        var head = new StringBuilder(256);
        head.Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(ReasonPhrase(response.StatusCode)).Append("\r\n");
        head.Append("Date: ").Append(DateTime.UtcNow.ToString("R")).Append("\r\n");
        head.Append("Server: RemoteCall/1.0\r\n");
        if (response.StatusCode is not (204 or 304))
        {
            head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
            head.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
        }

        head.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
        head.Append("X-Content-Type-Options: nosniff\r\n");
        foreach (var header in response.Headers)
        {
            head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }

        head.Append("\r\n");
        var headBytes = Encoding.UTF8.GetBytes(head.ToString());
        await stream.WriteAsync(headBytes, ct).ConfigureAwait(false);
        if (!isHead && response.Body.Length > 0)
        {
            await stream.WriteAsync(response.Body, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<HttpRequest?> ReadRequestAsync(BufferedStreamReader reader, string remoteIp, CancellationToken ct)
    {
        byte[]? lineBytes;
        do
        {
            lineBytes = await reader.ReadLineBytesAsync(MaxRequestLineBytes, ct).ConfigureAwait(false);
            if (lineBytes == null)
            {
                return null;
            }
        }
        while (lineBytes.Length == 0);

        var requestLine = Encoding.Latin1.GetString(lineBytes);
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new HttpProtocolException(400, "请求行格式错误。");
        }

        var method = parts[0].ToUpperInvariant();
        if (method.Length == 0 || !method.All(c => c is >= 'A' and <= 'Z'))
        {
            throw new HttpProtocolException(400, "请求方法非法。");
        }

        var target = parts[1];
        var version = parts.Length > 2 ? parts[2] : "HTTP/1.1";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headerBytes = 0;
        for (var count = 0; ; count++)
        {
            lineBytes = await reader.ReadLineBytesAsync(MaxHeaderLineBytes, ct).ConfigureAwait(false);
            if (lineBytes == null)
            {
                throw new HttpProtocolException(400, "请求头不完整。");
            }

            if (lineBytes.Length == 0)
            {
                break;
            }

            if (count >= MaxHeaderCount)
            {
                throw new HttpProtocolException(431, "请求头过多。");
            }

            headerBytes += lineBytes.Length;
            if (headerBytes > MaxHeaderBytes)
            {
                throw new HttpProtocolException(431, "请求头过大。");
            }

            var text = Encoding.UTF8.GetString(lineBytes);
            var colon = text.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = text[..colon].Trim();
            var value = text[(colon + 1)..].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (headers.TryGetValue(name, out var existing))
            {
                headers[name] = string.Equals(name, "cookie", StringComparison.OrdinalIgnoreCase)
                    ? existing + "; " + value
                    : existing + "," + value;
            }
            else
            {
                headers[name] = value;
            }
        }

        if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
            transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpProtocolException(501, "不支持分块传输编码。");
        }

        var body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var contentLengthRaw))
        {
            if (!int.TryParse(contentLengthRaw, out var contentLength) || contentLength < 0)
            {
                throw new HttpProtocolException(400, "Content-Length 非法。");
            }

            if (contentLength > Config.CoreDefaults.RequestBodyLimit)
            {
                throw new HttpProtocolException(413, "请求体过大。");
            }

            if (contentLength > 0)
            {
                body = await reader.ReadExactAsync(contentLength, ct).ConfigureAwait(false);
            }
        }

        var keepAlive = version.Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase)
            ? !(headers.TryGetValue("Connection", out var connection) && connection.Contains("close", StringComparison.OrdinalIgnoreCase))
            : headers.TryGetValue("Connection", out var connection10) && connection10.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);

        ParseTarget(target, out var path, out var query);

        return new HttpRequest
        {
            Method = method,
            Target = target,
            Path = path,
            Query = query,
            Headers = headers,
            Body = body,
            RemoteIp = remoteIp,
            KeepAlive = keepAlive,
        };
    }

    private static void ParseTarget(string target, out string path, out Dictionary<string, string> query)
    {
        var questionMark = target.IndexOf('?');
        var rawPath = questionMark >= 0 ? target[..questionMark] : target;
        var rawQuery = questionMark >= 0 ? target[(questionMark + 1)..] : "";

        if (rawPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            rawPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // 兼容代理形式（罕见），只取路径部分
            var schemeEnd = rawPath.IndexOf("://", StringComparison.Ordinal) + 3;
            var slash = rawPath.IndexOf('/', schemeEnd);
            rawPath = slash < 0 ? "/" : rawPath[slash..];
        }

        if (!rawPath.StartsWith('/'))
        {
            rawPath = "/" + rawPath;
        }

        var segments = rawPath.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = PercentDecode(segments[i], plusAsSpace: false);
        }

        path = string.Join('/', segments);

        query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rawQuery.Length == 0)
        {
            return;
        }

        foreach (var pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = equals >= 0 ? pair[..equals] : pair;
            var value = equals >= 0 ? pair[(equals + 1)..] : "";
            var decodedKey = PercentDecode(key, plusAsSpace: true);
            if (decodedKey.Length == 0)
            {
                continue;
            }

            query[decodedKey] = PercentDecode(value, plusAsSpace: true);
        }
    }

    private static string PercentDecode(string value, bool plusAsSpace)
    {
        try
        {
            return plusAsSpace
                ? Uri.UnescapeDataString(value.Replace('+', ' '))
                : Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            throw new HttpProtocolException(400, "URL 编码非法。");
        }
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        411 => "Length Required",
        413 => "Payload Too Large",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        _ => "OK",
    };
}

/// <summary>
/// 带缓冲的请求读取器，支持读一行（CRLF）与读固定长度（请求体）。
/// </summary>
internal sealed class BufferedStreamReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private int _start;
    private int _end;

    public BufferedStreamReader(Stream stream, int bufferSize = 8192)
    {
        _stream = stream;
        _buffer = new byte[bufferSize];
    }

    private async ValueTask<bool> FillAsync(CancellationToken ct)
    {
        _start = 0;
        _end = 0;
        var read = await _stream.ReadAsync(_buffer.AsMemory(0, _buffer.Length), ct).ConfigureAwait(false);
        if (read <= 0)
        {
            return false;
        }

        _end = read;
        return true;
    }

    /// <summary>
    /// 读取一行（不含 CRLF）。流结束且没有内容时返回 null。
    /// </summary>
    public async ValueTask<byte[]?> ReadLineBytesAsync(int maxLength, CancellationToken ct)
    {
        var accumulator = new List<byte>(128);
        while (true)
        {
            for (var i = _start; i < _end; i++)
            {
                if (_buffer[i] != (byte)'\n')
                {
                    continue;
                }

                accumulator.AddRange(new ArraySegment<byte>(_buffer, _start, i - _start));
                _start = i + 1;
                if (accumulator.Count > 0 && accumulator[^1] == (byte)'\r')
                {
                    accumulator.RemoveAt(accumulator.Count - 1);
                }

                return accumulator.ToArray();
            }

            if (_end > _start)
            {
                accumulator.AddRange(new ArraySegment<byte>(_buffer, _start, _end - _start));
            }

            if (accumulator.Count > maxLength)
            {
                throw new HttpProtocolException(431, "请求行/请求头过长。");
            }

            if (!await FillAsync(ct).ConfigureAwait(false))
            {
                return accumulator.Count > 0 ? accumulator.ToArray() : null;
            }
        }
    }

    /// <summary>
    /// 读取固定长度字节（请求体）。
    /// </summary>
    public async ValueTask<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var result = new byte[count];
        var filled = 0;
        if (_end > _start)
        {
            var take = Math.Min(count, _end - _start);
            Buffer.BlockCopy(_buffer, _start, result, 0, take);
            _start += take;
            filled = take;
        }

        while (filled < count)
        {
            var read = await _stream.ReadAsync(result.AsMemory(filled, count - filled), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new HttpProtocolException(400, "请求体不完整。");
            }

            filled += read;
        }

        return result;
    }
}
