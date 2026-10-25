using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteCall.Core.Server;

/// <summary>
/// 枚举本机可用于局域网访问的 IPv4 地址。
/// <para>
/// 会过滤掉虚拟网卡（VMware / Hyper-V / VirtualBox / Radmin / Tailscale / WSL / Docker / VPN 等），
/// 这些网卡的地址（如 192.168.x.1、26.x.x.x）手机根本连不上。
/// </para>
/// </summary>
public static class LanAddresses
{
    /// <summary>明显是虚拟/隧道网卡的关键词（匹配网卡名称与描述，忽略大小写）。</summary>
    private static readonly string[] VirtualKeywords =
    {
        "vmware", "virtualbox", "vbox", "hyper-v", "vethernet", "virtual",
        "radmin", "tailscale", "zerotier", "wireguard", "openvpn", "tap-", " tap ",
        "tunnel", "vpn", "wsl", "docker", "hamachi", "npcap", "loopback",
        "bluetooth", "蓝牙", "sanbao", "sangfor", "easyconnect",
    };

    /// <summary>
    /// 取可用于局域网访问的 IPv4 地址，按「最可能是真实局域网」排序。
    /// 如果一个可用的都没有（例如完全离线的机器），会退回到所有候选地址。
    /// </summary>
    public static IReadOnlyList<string> GetIPv4()
    {
        var candidates = new List<(string Address, int Score, int Index)>();
        var index = 0;

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceProperties properties;
                try
                {
                    properties = nic.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                var looksVirtual = LooksVirtual(nic);
                var hasGateway = HasIPv4Gateway(properties);

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var text = unicast.Address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal) || text == "0.0.0.0")
                    {
                        continue; // APIPA 自动地址，连不通
                    }

                    var score = 0;
                    if (hasGateway)
                    {
                        score += 100; // 有默认网关 ⇒ 基本可以确定是真实网络
                    }

                    if (IsPrivate(text))
                    {
                        score += 50;
                    }

                    if (looksVirtual)
                    {
                        score -= 150;
                    }

                    if (IsCgnat(text))
                    {
                        score -= 50; // 100.64/10，多半是 Tailscale 之类
                    }

                    candidates.Add((text, score, index++));
                }
            }
        }
        catch
        {
            // 枚举失败时返回空列表，调用方会退回到“未获取到地址”的提示
        }

        if (candidates.Count == 0)
        {
            return Array.Empty<string>();
        }

        var preferred = candidates
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Index)
            .Select(c => c.Address)
            .Distinct()
            .ToList();

        if (preferred.Count > 0)
        {
            return preferred;
        }

        // 没有任何“像真实局域网”的地址时，至少把候选都列出来，让用户自己判断
        return candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Index)
            .Select(c => c.Address)
            .Distinct()
            .ToList();
    }

    public static IReadOnlyList<string> GetUrls(int port)
    {
        return GetIPv4().Select(ip => $"http://{ip}:{port}/").ToList();
    }

    public static string? GetPrimaryUrl(int port)
    {
        return GetUrls(port).FirstOrDefault();
    }

    public static bool IsLoopback(IPAddress? address)
    {
        if (address == null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address);
    }

    private static bool LooksVirtual(NetworkInterface nic)
    {
        var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
        return VirtualKeywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal));
    }

    private static bool HasIPv4Gateway(IPInterfaceProperties properties)
    {
        try
        {
            foreach (var gateway in properties.GatewayAddresses)
            {
                if (gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !gateway.Address.Equals(IPAddress.Any))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 忽略
        }

        return false;
    }

    private static bool IsPrivate(string address)
    {
        var parts = address.Split('.');
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], out var a) ||
            !int.TryParse(parts[1], out var b))
        {
            return false;
        }

        return a == 10 ||
               (a == 172 && b is >= 16 and <= 31) ||
               (a == 192 && b == 168);
    }

    private static bool IsCgnat(string address)
    {
        var parts = address.Split('.');
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], out var a) ||
            !int.TryParse(parts[1], out var b))
        {
            return false;
        }

        return a == 100 && b is >= 64 and <= 127;
    }
}
