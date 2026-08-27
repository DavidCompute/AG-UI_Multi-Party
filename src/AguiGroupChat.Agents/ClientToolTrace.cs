using System.Text;

namespace AguiGroupChat.Agents;

/// <summary>
/// 客户端执行技能（Client-side tools）调试追踪：把「前端是否回传 toolResult → 网关是否写入共享存储 → 占位函数是否消费」
/// 落盘到 <c>data/clienttool-trace.log</c>（内容根/data，与技能沙箱同目录），桌面版无控制台时据此定位断点。
/// 仅诊断用途，多余日志不影响运行。
/// </summary>
public static class ClientToolTrace
{
    private static readonly object _gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (_gate)
            {
                var dir = Path.Combine(Directory.GetCurrentDirectory(), "data");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "clienttool-trace.log"),
                    $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 诊断失败不影响主流程
        }
    }
}
