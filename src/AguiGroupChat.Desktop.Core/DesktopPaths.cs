namespace AguiGroupChat.Desktop;

/// <summary>
/// 桌面版跨进程共享目录：<c>%LocalAppData%\AguiGroupChat</c>。
/// 实例计数 / 后端 secret / 一次性提示标记 / 模型回退目录都落在这里——集中一处，
/// 避免各文件各写一份 <c>Path.Combine(LocalApplicationData, "AguiGroupChat")</c> 将来改一处漏一处。
/// </summary>
public static class DesktopPaths
{
    /// <summary>共享数据目录（不存在时调用方自行创建）。</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AguiGroupChat");
}
