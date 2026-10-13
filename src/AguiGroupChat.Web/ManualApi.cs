using System.Text;

namespace AguiGroupChat.Web;

/// <summary>
/// 内置用户手册：仓库 <c>docs/user-guide.md</c> / <c>docs/user-guide.en.md</c> 以<b>嵌入资源</b>随程序集分发，
/// 供前端「📖 完整手册」弹窗按当前界面语言拉取并渲染。
///
/// <para>
/// 用嵌入资源而不是运行时读磁盘：开发 / Docker / Windows 桌面版三处的“文档目录”各不相同，
/// 嵌入后不存在路径差异（<c>Dockerfile</c> 已在发布前 <c>COPY docs/ docs/</c>）。
/// </para>
/// </summary>
public static class ManualApi
{
    /// <summary>嵌入资源逻辑名（与 <c>AguiGroupChat.Web.csproj</c> 的 <c>LogicalName</c> 一致）。</summary>
    private const string ZhResource = "AguiGroupChat.Manual.zh.md";
    private const string EnResource = "AguiGroupChat.Manual.en.md";

    public static void MapManualApi(this WebApplication app)
    {
        // 仅登录用户可读：手册含接口一览 / 配置键等内部信息（前端全局 fetch 会自动带上 Bearer 令牌）
        app.MapGet("/ag-ui/manual", (string? lang) =>
        {
            var zh = string.Equals((lang ?? "").Trim(), "zh", StringComparison.OrdinalIgnoreCase);
            var text = Read(zh ? ZhResource : EnResource) ?? Read(EnResource) ?? "";
            return Results.Text(text, "text/markdown; charset=utf-8");
        })
        .AddEndpointFilter(new WebIdentity.RequireIdentityFilter());
    }

    private static string? Read(string logicalName)
    {
        using var stream = typeof(ManualApi).Assembly.GetManifestResourceStream(logicalName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
