namespace AguiGroupChat.Desktop;

/// <summary>
/// 桌面版「首次启动一次性提示」：本机未装 LibreOffice（办公文档在线查看 / PPT 播放的**唯一外部依赖**）时，
/// 首次启动提示一次，之后不再打扰（落一个标记文件）。
///
/// <para>
/// 这里只做「本次是否赢得提示权 / 落标记」的**纯逻辑**（便于单测）：真正的弹窗与文案由 UI 壳负责。
/// 探测复用后端同一套逻辑（<c>ProcessSofficeRunner.DetectSoffice</c>：只读环境变量 + 常见安装路径，无副作用），
/// UI 与后端同机，结论一致。
/// </para>
///
/// <para>
/// <b>为什么是「认领」而不是「先查再写」</b>：两个 UI 实例同时启动时，先查再写会双双查到「未提示过」而各弹一次。
/// <see cref="TryClaimOnce"/> 在跨进程独占文件锁内完成「查标记 → 写标记」，只有一个进程能赢。
/// </para>
/// </summary>
public static class DocPreviewStartupHint
{
    /// <summary>标记文件所在目录：与实例计数 / 后端 secret 同处（%LocalAppData%\AguiGroupChat）。</summary>
    public static string DefaultBaseDir => DesktopPaths.DataDir;

    /// <summary>标记文件路径：存在即表示已提示过。</summary>
    public static string MarkerPath(string? baseDir = null)
        => Path.Combine(baseDir ?? DefaultBaseDir, "docpreview-hint-shown");

    /// <summary>独占锁文件名（跨进程，保证「查 + 写」原子）。</summary>
    private static string LockPath(string baseDir) => Path.Combine(baseDir, "docpreview-hint.lock");

    /// <summary>
    /// 尝试认领「本次启动由我弹提示」：转换器已装 → 不弹（<c>false</c>）；否则在独占锁内检查标记，
    /// 未提示过则写标记并返回 <c>true</c>（由调用方弹窗），已提示过或出错返回 <c>false</c>。
    /// </summary>
    public static bool TryClaimOnce(bool converterAvailable, string? baseDir = null)
    {
        if (converterAvailable) return false; // 已装 LibreOffice：无需提示
        var dir = baseDir ?? DefaultBaseDir;
        try
        {
            Directory.CreateDirectory(dir);
            using var lk = File.Open(LockPath(dir), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(MarkerPath(baseDir))) return false;
            File.WriteAllText(MarkerPath(baseDir), DateTime.UtcNow.ToString("o"));
            return true;
        }
        catch
        {
            return false; // 拿不到锁 / 写不了标记（权限等）：宁可不弹，也不要每次启动都弹
        }
    }
}
