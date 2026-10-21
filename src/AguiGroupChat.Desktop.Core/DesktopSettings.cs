using System.Text.Json;

namespace AguiGroupChat.Desktop;

/// <summary>
/// 桌面壳本机偏好（与业务数据无关）：落 <c>%LocalAppData%\AguiGroupChat\desktop-settings.json</c>。
/// 读写都容错——文件损坏 / 目录不可写时退回默认值，不影响启动。
/// </summary>
public sealed class DesktopSettings
{
    private static readonly string FilePath = Path.Combine(DesktopPaths.DataDir, "desktop-settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>关闭主窗口时最小化到托盘（默认关闭：关窗即退出）。</summary>
    public bool CloseToTray { get; set; }

    // 窗口位置 / 尺寸（1.0.308）：只保存正常态边界；最大化状态单独记。
    public bool HasWindowPlacement { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public bool WindowMaximized { get; set; }

    public static DesktopSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new DesktopSettings();
            return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath), JsonOptions)
                   ?? new DesktopSettings();
        }
        catch
        {
            return new DesktopSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DesktopPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { /* 偏好写不进去不影响运行 */ }
    }
}
