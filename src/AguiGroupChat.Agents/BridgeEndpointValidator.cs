using System.Net;
using System.Net.Sockets;

namespace AguiGroupChat.Agents;

/// <summary>外部 AG-UI 桥接端点校验（SSRF 防护）：仅允许 http/https/ws/wss，
/// 拒绝明显危险 scheme 与链路本地地址（云元数据 169.254.x.x 等）。
/// <see cref="GetError"/> 为静态规则（兼容本机 / 内网 AG-UI 部署）；
/// <see cref="ValidateResolved"/> 为解析级规则：域名 DNS 解析后逐 IP 拦截环回 / 私网 /
/// 链路本地（AguiBridge:AllowPrivateEndpoints=false 时启用，公网部署收紧 SSRF）。</summary>
public static class BridgeEndpointValidator
{
    /// <returns>校验通过返回 null；否则返回错误说明（中文，供日志 / 400 响应）。</returns>
    public static string? GetError(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return "桥接端点不能为空";

        var trimmed = endpoint.Trim();
        var lower = trimmed.ToLowerInvariant();
        if (!(lower.StartsWith("http://") || lower.StartsWith("https://")
              || lower.StartsWith("ws://") || lower.StartsWith("wss://")))
            return "仅支持 http/https/ws/wss 协议";

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return "桥接端点不是合法 URL";

        // 域名（含 localhost）放行：解析后的最终 IP 校验见 ValidateResolved（由网关在调用前按配置执行）
        if (!IPAddress.TryParse(uri.Host, out var ip))
            return null;

        try
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                if (ip.Equals(IPAddress.Any))
                    return "桥接端点不能指向未指定地址（0.0.0.0）";
                var bytes = ip.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                    return "桥接端点不能指向链路本地地址（云元数据 169.254.x.x 等）";
                return null; // 环回 127.x 与公网地址放行
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.Equals(IPAddress.IPv6Any))
                    return "桥接端点不能指向未指定地址（::）";
                var bytes = ip.GetAddressBytes();
                if ((bytes[0] & 0xFF) == 0xFE && (bytes[1] & 0xC0) == 0x80)
                    return "桥接端点不能指向链路本地地址（fe80::/10）";
                return null; // 环回 ::1 与公网地址放行
            }
            return null;
        }
        catch
        {
            return "桥接端点地址解析失败"; // 任何解析异常一律拒绝
        }
    }

    /// <summary>
    /// 解析级校验（网关调用前执行）：对 host 做 DNS 解析（IP 字面量直接取用），
    /// 任一解析 IP 命中环回 / 私网 / 链路本地 / 未指定地址即拒绝——覆盖
    /// <c>localhost</c>、内网域名与 DNS rebinding 场景。解析失败一律拒绝。
    /// </summary>
    public static string? ValidateResolved(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return "桥接端点不能为空";
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return "桥接端点不是合法 URL";

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host, out var parsed)
                ? [parsed]
                : Dns.GetHostAddresses(uri.Host);
        }
        catch
        {
            return "桥接端点域名解析失败";
        }
        if (addresses.Length == 0)
            return "桥接端点域名无解析结果";

        foreach (var ip in addresses)
        {
            if (IPAddress.IsLoopback(ip))
                return "桥接端点不能指向环回地址（localhost / 127.x / ::1）";
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (ip.Equals(IPAddress.Any))
                    return "桥接端点不能指向未指定地址（0.0.0.0）";
                if (b[0] == 169 && b[1] == 254)
                    return "桥接端点不能指向链路本地地址（云元数据 169.254.x.x 等）";
                if (b[0] == 10)
                    return "桥接端点不能指向私网地址（10.x.x.x）";
                if (b[0] == 172 && b[1] is >= 16 and <= 31)
                    return "桥接端点不能指向私网地址（172.16-31.x.x）";
                if (b[0] == 192 && b[1] == 168)
                    return "桥接端点不能指向私网地址（192.168.x.x）";
                if (b[0] == 100 && b[1] is >= 64 and <= 127)
                    return "桥接端点不能指向运营商级 NAT 保留地址（100.64-127.x.x）";
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // IPv4-mapped IPv6（::ffff:a.b.c.d，如 ::ffff:10.0.0.1）能绕过纯 IPv6 分支的检查：
                // 先映射回 IPv4 再走 IPv4 私网判定（防 SSRF 绕过），注意 var b 在分支内重新取
                if (ip.IsIPv4MappedToIPv6)
                {
                    var v4 = ip.MapToIPv4();
                    var b = v4.GetAddressBytes();
                    if (v4.Equals(IPAddress.Any))
                        return "桥接端点不能指向未指定地址（0.0.0.0）";
                    if (b[0] == 169 && b[1] == 254)
                        return "桥接端点不能指向链路本地地址（云元数据 169.254.x.x 等）";
                    if (b[0] == 10)
                        return "桥接端点不能指向私网地址（10.x.x.x）";
                    if (b[0] == 172 && b[1] is >= 16 and <= 31)
                        return "桥接端点不能指向私网地址（172.16-31.x.x）";
                    if (b[0] == 192 && b[1] == 168)
                        return "桥接端点不能指向私网地址（192.168.x.x）";
                    if (b[0] == 100 && b[1] is >= 64 and <= 127)
                        return "桥接端点不能指向运营商级 NAT 保留地址（100.64-127.x.x）";
                    continue; // 映射后的公网 IPv4：继续检查其余解析结果
                }
                var b6 = ip.GetAddressBytes();
                if (ip.Equals(IPAddress.IPv6Any))
                    return "桥接端点不能指向未指定地址（::）";
                if ((b6[0] & 0xFF) == 0xFE && (b6[1] & 0xC0) == 0x80)
                    return "桥接端点不能指向链路本地地址（fe80::/10）";
                if ((b6[0] & 0xFE) == 0xFC)
                    return "桥接端点不能指向唯一本地地址（fc00::/7）";
            }
        }
        return null;
    }
}
