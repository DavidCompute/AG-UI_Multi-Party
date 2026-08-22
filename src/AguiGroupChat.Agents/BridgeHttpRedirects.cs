namespace AguiGroupChat.Agents;

/// <summary>
/// 桥接 HTTP 手动重定向跟随（SSRF 防护）：构造层已禁用 HttpClient 自动重定向，
/// 重定向改由本类逐跳处理——每个跳转目标都用 <see cref="BridgeEndpointValidator"/> 校验
/// （scheme + 私网 / 环回 / 链路本地），防止 302/307 等响应把请求导向内网地址绕过端点校验。
/// 最多跟随 5 跳，超出 / 校验失败抛异常（调用方按连接失败处理）。
/// </summary>
internal static class BridgeHttpRedirects
{
    private const int MaxHops = 5;

    /// <summary>发送请求并手动跟随重定向（≤5 跳）。初始 URL 仅做静态规则校验（网关已按
    /// <c>AllowPrivateEndpoints</c> 配置对配置端点做过解析级校验，此处不重复收紧本地部署）；
    /// 每个重定向目标一律做解析级校验（私网 / 环回 / 链路本地拒绝）。</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        var current = request.RequestUri
            ?? throw new InvalidOperationException("桥接请求缺少 URL");
        var initialError = BridgeEndpointValidator.GetError(current.AbsoluteUri);
        if (initialError is not null)
            throw new InvalidOperationException($"桥接端点非法：{initialError}");

        for (var hop = 0; ; hop++)
        {
            request.RequestUri = current;
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var location = response.Headers.Location;
            if ((int)response.StatusCode is >= 300 and < 400 && location is not null)
            {
                // 重定向响应无正文：读流前先释放，下一跳复用同一请求对象重新发送
                response.Dispose();
                if (hop >= MaxHops)
                    throw new InvalidOperationException("桥接请求重定向次数过多（超过 5 次）");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                // 每跳严格校验：scheme + 解析后逐 IP 拦截私网 / 环回（防 302 指向内网绕过 SSRF 防护）
                var error = BridgeEndpointValidator.GetError(next.AbsoluteUri)
                    ?? BridgeEndpointValidator.ValidateResolved(next.AbsoluteUri);
                if (error is not null)
                    throw new InvalidOperationException($"桥接重定向目标非法：{error}");
                current = next;
                continue;
            }
            return response;
        }
    }
}
