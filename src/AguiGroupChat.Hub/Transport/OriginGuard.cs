namespace AguiGroupChat.Hub.Transport;

/// <summary>
/// WS / SSE 实时连接的跨站来源校验（防 CSWSH：恶意网页经浏览器发起跨站 WebSocket / EventSource
/// 冒充已登录用户建连）。规则：无 Origin 头（curl / 非浏览器客户端）放行；同源放行；
/// 其余须命中 AuthOptions.AllowedOrigins 白名单，否则拒绝。
/// </summary>
internal static class OriginGuard
{
    public static bool IsAllowed(Microsoft.AspNetCore.Http.HttpRequest request, Options.AuthOptions options)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin)) return true;

        // 同源：scheme + host + port 一致（省略端口时按 scheme 默认端口）
        if (Uri.TryCreate(origin, UriKind.Absolute, out var o))
        {
            var requestPort = request.Host.Port
                ?? (request.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
            if (string.Equals(o.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
                && o.Port == requestPort)
                return true;
        }

        var allowed = (options.AllowedOrigins ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var a in allowed)
        {
            if (string.Equals(a.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
