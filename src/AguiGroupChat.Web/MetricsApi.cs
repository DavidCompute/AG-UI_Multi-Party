using System.Text;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 可观测性配置（appsettings 的 <c>Observability</c> 节）。
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>是否暴露 Prometheus 抓取端点 <c>GET /ag-ui/metrics</c>（默认开）。</summary>
    public bool PrometheusEnabled { get; set; } = true;

    /// <summary>
    /// 端点是否要求鉴权（默认<b>开</b>）：指标会泄露数字员工标识与调用量，公网 / 多租场景不应匿名可读。
    /// 置 false 仅建议在「仅内网可达」的部署里使用。
    /// </summary>
    public bool PrometheusRequireAuth { get; set; } = true;

    /// <summary>
    /// 专用抓取令牌（可选）：设置后，抓取方可用 <c>Authorization: Bearer &lt;token&gt;</c> 或 <c>?token=&lt;token&gt;</c>
    /// 读取指标，无需管理员/会话账号——便于把 Prometheus 抓取凭据与用户体系解耦。留空则只接受登录会话或托管 API 密钥。
    /// </summary>
    public string PrometheusScrapeToken { get; set; } = "";
}

/// <summary>
/// 运行指标导出（6.1 可观测性 · OpenTelemetry 对接）：
///   GET /ag-ui/metrics —— <b>Prometheus 文本格式</b>（<c>text/plain; version=0.0.4</c>）。
/// Prometheus 可直接抓取；OpenTelemetry Collector 也可用 <c>prometheus</c> receiver 采集后转发到任意后端
/// （OTLP / Tempo / 云监控…），因此无需在本进程内嵌 OTel SDK 即可完成对接。
/// 鉴权见 <see cref="ObservabilityOptions"/>：默认要求（专用抓取令牌 / 登录会话 / 托管 API 密钥）。
/// </summary>
public static class MetricsApi
{
    public static void MapMetricsApi(this WebApplication app)
    {
        app.MapGet("/ag-ui/metrics", (HttpContext ctx, MetricsService metrics, ObservabilityOptions options,
            AuthService auth) =>
        {
            if (!options.PrometheusEnabled)
                return Results.NotFound();

            if (options.PrometheusRequireAuth && !IsAuthorized(ctx, options, auth))
                return Results.Json(new { error = "未授权：指标端点需要抓取令牌或登录身份" },
                    statusCode: StatusCodes.Status401Unauthorized);

            var text = metrics.RenderPrometheusText();
            return Results.Text(text, "text/plain; version=0.0.4; charset=utf-8");
        });
    }

    /// <summary>鉴权：先看专用抓取令牌（恒定时间比较），再回退到登录会话 / 托管 API 密钥。</summary>
    private static bool IsAuthorized(HttpContext ctx, ObservabilityOptions options, AuthService auth)
    {
        var token = WebIdentity.ResolveToken(ctx.Request);
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (!string.IsNullOrWhiteSpace(options.PrometheusScrapeToken)
            && FixedTimeEquals(options.PrometheusScrapeToken.Trim(), token))
            return true;
        return auth.ValidateToken(token) is not null || auth.ResolveApiKey(token) is not null;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length) return false;
        var d = 0;
        for (var i = 0; i < ba.Length; i++) d |= ba[i] ^ bb[i];
        return d == 0;
    }
}
