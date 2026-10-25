using RemoteCall.Core.Http;

namespace RemoteCall.Core.Server;

/// <summary>
/// 内嵌静态资源的响应构造（带 ETag/304）。
/// </summary>
internal static class Assets
{
    public static HttpResponse Serve(string logicalPath, HttpRequest request)
    {
        if (!WebAssets.TryGet(logicalPath, out var bytes, out var contentType, out var etag))
        {
            return HttpResponse.Error(404, "not_found", $"资源不存在：{logicalPath}");
        }

        var ifNoneMatch = request.Header("If-None-Match");
        var notModified = !string.IsNullOrEmpty(ifNoneMatch) &&
                          ifNoneMatch.Contains(etag.Trim('"'), StringComparison.Ordinal);

        var response = notModified
            ? new HttpResponse { StatusCode = 304, ContentType = contentType }
            : HttpResponse.Bytes(bytes, contentType);

        response.Headers["ETag"] = etag;
        response.Headers["Cache-Control"] = "no-cache";
        return response;
    }
}
