namespace AguiGroupChat.SkillHosting;

/// <summary>技能宿主（服务端 Roslyn / 本机桥）默认路径约定。</summary>
public static class SkillHostingDefaults
{
    /// <summary>NuGet 引用还原缓存根：可经环境变量 AGUI_DOTNET_PKG_CACHE 覆盖；
    /// 缺省放系统本地应用数据目录（容器内无则用临时目录）。</summary>
    public static string NuGetCacheRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("AGUI_DOTNET_PKG_CACHE");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(local)
            ? Path.Combine(Path.GetTempPath(), "AguiGroupChat")
            : Path.Combine(local, "AguiGroupChat");
        return Path.Combine(root, "dotnetpkgs");
    }
}
