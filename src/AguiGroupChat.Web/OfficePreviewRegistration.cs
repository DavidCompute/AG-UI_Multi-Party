using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Web;

/// <summary>办公文档「在线查看」服务的注册入口（组合根与测试夹具共用，避免两处注册漂移）。</summary>
public static class OfficePreviewRegistration
{
    /// <summary>
    /// 注册转换链路：<see cref="ISofficeRunner"/>（真机为 LibreOffice 进程）+ <see cref="OfficePreviewConverter"/>。
    ///
    /// <para>
    /// 为什么抽出来：端点参数是 <see cref="IDocPreviewConverter"/>，一旦某处忘了注册，
    /// minimal API 会把它**当请求体推断**，症状是所有请求都 500 且响应体为空（排查很费时）。
    /// 注册只留一个入口就不会再出现这种漂移。
    /// </para>
    /// </summary>
    /// <param name="cacheRoot">缓存与一次性 LibreOffice profile 的落盘父目录。</param>
    public static IServiceCollection AddDocumentPreview(this IServiceCollection services, string cacheRoot)
    {
        services.AddSingleton<ISofficeRunner>(sp => new ProcessSofficeRunner(
            sofficePath: null, // 自动探测（可用环境变量 AGUI_SOFFICE 覆盖）
            profileRoot: Path.Combine(cacheRoot, "profiles"),
            sp.GetRequiredService<ILogger<ProcessSofficeRunner>>()));
        services.AddSingleton(sp => new OfficePreviewConverter(
            cacheRoot,
            sp.GetRequiredService<ISofficeRunner>(),
            sp.GetRequiredService<ILogger<OfficePreviewConverter>>()));
        // 端点按接口解析；同时保留具体类型注册，供「清空一切」直接调用 ClearAll
        services.AddSingleton<IDocPreviewConverter>(sp => sp.GetRequiredService<OfficePreviewConverter>());
        return services;
    }
}
