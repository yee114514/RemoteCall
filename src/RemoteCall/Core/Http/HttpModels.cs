using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RemoteCall.Core.Http;

/// <summary>
/// 统一的 JSON 序列化选项（camelCase，中文不转义）。
/// </summary>
public static class WebJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>
/// 业务层可抛出的 API 错误：会被转换成统一 JSON 错误响应。
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string Code { get; }

    public static ApiException BadRequest(string message, string code = "bad_request") => new(400, code, message);
    public static ApiException Unauthorized(string message = "请先登录。") => new(401, "unauthorized", message);
    public static ApiException Forbidden(string message = "没有权限。") => new(403, "forbidden", message);
    public static ApiException NotFound(string message = "资源不存在。") => new(404, "not_found", message);
    public static ApiException TooManyRequests(string message) => new(429, "too_many_requests", message);
}

/// <summary>
/// HTTP 协议级错误（解析失败等），会直接返回状态码并关闭连接。
/// </summary>
public sealed class HttpProtocolException : Exception
{
    public HttpProtocolException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

/// <summary>
/// 解析后的请求。
/// </summary>
public sealed class HttpRequest
{
    public string Method { get; init; } = "GET";
    public string Target { get; init; } = "/";
    public string Path { get; init; } = "/";
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public string RemoteIp { get; init; } = "";
    public bool KeepAlive { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public string? QueryValue(string name) => Query.TryGetValue(name, out var value) ? value : null;

    public string BodyText => Encoding.UTF8.GetString(Body);

    public T? BodyJson<T>() where T : class
    {
        if (Body.Length == 0)
        {
            return null;
        }

        try
        {
            return WebJson.Deserialize<T>(BodyText);
        }
        catch (JsonException)
        {
            throw ApiException.BadRequest("请求体不是合法的 JSON。", "invalid_json");
        }
    }

    public string? Cookie(string name)
    {
        var raw = Header("cookie");
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        foreach (var part in raw.Split(';'))
        {
            var trimmed = part.Trim();
            var index = trimmed.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            if (string.Equals(trimmed[..index].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(index + 1)..].Trim();
            }
        }

        return null;
    }
}

/// <summary>
/// 要返回的响应。
/// </summary>
public sealed class HttpResponse
{
    public int StatusCode { get; set; } = 200;
    public string ContentType { get; set; } = "application/json; charset=utf-8";
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HttpResponse WithHeader(string name, string value)
    {
        Headers[name] = value;
        return this;
    }

    public static HttpResponse Json<T>(T value, int statusCode = 200)
    {
        return new HttpResponse
        {
            StatusCode = statusCode,
            ContentType = "application/json; charset=utf-8",
            Body = Encoding.UTF8.GetBytes(WebJson.Serialize(value)),
        };
    }

    public static HttpResponse Ok(object? extra = null)
    {
        return extra == null
            ? Json(new { ok = true })
            : Json(new { ok = true, data = extra });
    }

    public static HttpResponse Error(int statusCode, string code, string message)
    {
        return Json(new { ok = false, error = code, message }, statusCode);
    }

    public static HttpResponse Html(string html, int statusCode = 200)
    {
        return new HttpResponse
        {
            StatusCode = statusCode,
            ContentType = "text/html; charset=utf-8",
            Body = Encoding.UTF8.GetBytes(html),
        };
    }

    public static HttpResponse Text(string text, int statusCode = 200)
    {
        return new HttpResponse
        {
            StatusCode = statusCode,
            ContentType = "text/plain; charset=utf-8",
            Body = Encoding.UTF8.GetBytes(text),
        };
    }

    public static HttpResponse Redirect(string location, int statusCode = 302)
    {
        var response = new HttpResponse
        {
            StatusCode = statusCode,
            ContentType = "text/plain; charset=utf-8",
            Body = Encoding.UTF8.GetBytes("正在跳转…"),
        };
        response.Headers["Location"] = location;
        return response;
    }

    public static HttpResponse NoContent()
    {
        return new HttpResponse
        {
            StatusCode = 204,
            ContentType = "text/plain; charset=utf-8",
            Body = Array.Empty<byte>(),
        };
    }

    public static HttpResponse Bytes(byte[] body, string contentType, int statusCode = 200)
    {
        return new HttpResponse
        {
            StatusCode = statusCode,
            ContentType = contentType,
            Body = body,
        };
    }
}

/// <summary>
/// 远端 IP 归一化（IPv4-mapped IPv6 → IPv4）。
/// </summary>
public static class IpAddressUtil
{
    public static string Normalize(System.Net.IPAddress? address)
    {
        if (address == null)
        {
            return "未知";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var text = address.ToString();
        // 去掉 IPv6 的 scope id
        var percent = text.IndexOf('%');
        return percent > 0 ? text[..percent] : text;
    }
}
