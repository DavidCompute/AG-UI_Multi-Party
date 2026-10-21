using Microsoft.Win32;

namespace AguiGroupChat.Desktop;

/// <summary>
/// 开机自启动：写 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>（per-user，免管理员）。
/// 命令带 <c>--autostart</c>，登录后静默进托盘，不主动弹窗。
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AguiGroupChat";

    /// <summary>是否已登记开机启动（不校验目标文件是否仍存在）。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启用 / 停用开机启动；失败时通过 <paramref name="error"/> 返回原因。</summary>
    public static bool SetEnabled(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "registry key unavailable";
                return false;
            }
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    error = "process path unavailable";
                    return false;
                }
                key.SetValue(ValueName, $"\"{exe}\" --autostart", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
