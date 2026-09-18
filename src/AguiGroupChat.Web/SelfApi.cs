using System.Security.Cryptography;
using System.Text;

namespace AguiGroupChat.Web;

/// <summary>
/// 平台自调用端点（技能 → 平台）。
///
/// <para>
/// 内置 dotnet 技能是<b>运行时用 Roslyn 编译的独立程序集</b>（只引用 TPA 里的框架程序集），
/// 拿不到平台的 DI 容器，所以它要用平台能力（典型：图库语义检索拿本地图片路径）时，
/// 只能通过 HTTP 回到本进程。
/// </para>
///
/// <para>
/// 这里提供两样东西：<c>AGUI_SELF_BASE</c>（回环基址）与 <c>AGUI_SELF_TOKEN</c>（进程内随机令牌），
/// 服务启动完成后写进**进程环境变量** —— 技能与平台同进程，读环境变量即可拿到。
/// 令牌不是可选的：那个内部接口会返回<b>服务器上的文件路径</b>，没有令牌的话同机任意进程都能打。
/// </para>
/// </summary>
public static class SelfApi
{
    /// <summary>进程内随机令牌（每次启动都不同，重启即失效）。</summary>
    public static string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public const string TokenEnvVar = "AGUI_SELF_TOKEN";
    public const string BaseEnvVar = "AGUI_SELF_BASE";

    /// <summary>启动完成后发布回环基址与令牌（须在技能执行之前完成，故挂在 ApplicationStarted 上）。</summary>
    public static void Publish(WebApplication app)
    {
        try
        {
            var raw = app.Urls.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(raw))
            {
                app.Logger.LogWarning("未能取得服务监听地址，技能将无法回调图库检索");
                return;
            }
            // 通配主机（+ / 0.0.0.0 / [::]）换成回环：技能与平台同进程/同容器，走回环最稳
            var normalized = raw
                .Replace("//+", "//127.0.0.1", StringComparison.Ordinal)
                .Replace("//0.0.0.0", "//127.0.0.1", StringComparison.Ordinal)
                .Replace("//[::]", "//127.0.0.1", StringComparison.Ordinal);
            var uri = new Uri(normalized);
            var baseUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
            Environment.SetEnvironmentVariable(BaseEnvVar, baseUrl);
            Environment.SetEnvironmentVariable(TokenEnvVar, Token);
            app.Logger.LogInformation("平台自调用端点已发布：{Base}（技能经此回调图库等内部能力）", baseUrl);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "发布平台自调用端点失败（技能将无法回调图库检索）");
        }
    }

    /// <summary>请求是否携带有效的自令牌（定长比较，避免时序侧信道）。</summary>
    public static bool IsSelf(HttpContext ctx)
    {
        var provided = ctx.Request.Headers[TokenEnvVar].ToString();
        if (provided.Length == 0) return false;
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(Token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
