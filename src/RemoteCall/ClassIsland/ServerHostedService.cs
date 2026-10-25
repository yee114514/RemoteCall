using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteCall.Core.Server;

namespace RemoteCall.ClassIsland;

/// <summary>
/// 把 Core 里的 <see cref="ServerHost"/> 挂到 ClassIsland 的主机生命周期上。
/// </summary>
internal sealed class ServerHostedService : IHostedService
{
    private readonly ServerHost _serverHost;
    private readonly ILogger<ServerHostedService> _logger;

    public ServerHostedService(ServerHost serverHost, ILogger<ServerHostedService> logger)
    {
        _serverHost = serverHost;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _serverHost.Start();
        }
        catch (Exception ex)
        {
            // 服务起不来不应该影响 ClassIsland 本身
            _logger.LogError(ex, "远程消息插件启动 HTTP 服务失败");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _serverHost.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "远程消息插件停止 HTTP 服务时出错");
        }

        return Task.CompletedTask;
    }
}
