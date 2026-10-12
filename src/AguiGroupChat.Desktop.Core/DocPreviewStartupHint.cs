namespace AguiGroupChat.Desktop;

/// <summary>
/// 桌面版「首次启动一次性提示」：本机未装 LibreOffice（办公文档在线查看 / PPT 播放的**唯一外部依赖**）时，
/// 首次启动提示一次，之后不再打扰（落一个标记文件）。
///
/// <para>
/// 这里只做「该不该弹 / 记下已弹」的**纯逻辑**（便于单测）：真正的弹窗与文案由 UI 壳负责。
/// 探测复用后端同一套逻辑（<c>ProcessSofficeRunner.DetectSoffice</c>：只读环境变量 + 常见安装路径，无副作用），
/// UI 与后端同机，结论一致。
/// </para>
/// </summary>
public static class DocPreviewStartupHint
{
    /// <summary>标记文件所在目录：与实例计数 / 后端 secret 同处（%LocalAppData%\AguiGroupChat）。</summary>
    public static string DefaultBaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AguiGroupChat");

    /// <summary>标记文件路径：存在即表示已提示过。</summary>
    public static string MarkerPath(string? baseDir = null)
        => Path.Combine(baseDir ?? DefaultBaseDir, "docpreview-hint-shown");

    /// <summary>是否应弹提示：转换器不可用 且 尚未提示过。</summary>
    public static bool ShouldShow(bool converterAvailable, string? baseDir = null)
    {
        if (converterAvailable) return false; // 已装 LibreOffice：无需提示
        try { return !File.Exists(MarkerPath(baseDir)); }
        catch { return false; } // 读不到标记（权限等）：宁可不弹，也不要每次启动都弹
    }

    /// <summary>记下「已提示过」（幂等；失败静默——大不了下次再提示一次）。</summary>
    public static void MarkShown(string? baseDir = null)
    {
        try
        {
            var path = MarkerPath(baseDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTime.UtcNow.ToString("o"));
        }
        catch { /* 忽略 */ }
    }
}
