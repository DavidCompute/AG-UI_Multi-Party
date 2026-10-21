using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace AguiGroupChat.Desktop;

internal static class DeepLinkProtocol
{
    /// <summary>同一用户会话内唯一的深链命名管道（<see cref="PipeOptions.CurrentUserOnly"/> 隔离其他用户）。</summary>
    public const string PipeName = "AguiGroupChat.Desktop.DeepLink";
}

/// <summary>
/// 深链服务端：第一个 UI 实例占有命名管道；跳转列表 / 快捷任务启动的新进程把请求交给它，
/// 由它在前台窗口内切换知聚 / 打开弹窗，避免每次深链都新开窗口（以及重复托盘图标）。
/// </summary>
internal sealed class DeepLinkServer : IDisposable
{
    private readonly Action<string?, string?> _onRequest;
    private readonly CancellationTokenSource _cts = new();
    private bool _started;

    public DeepLinkServer(Action<string?, string?> onRequest) => _onRequest = onRequest;

    /// <summary>尝试占有管道；已有实例在服务时返回 false（本窗口不处理深链）。</summary>
    public bool TryStart()
    {
        if (_started) return true;
        try
        {
            var first = CreateServer();
            _started = true;
            _ = Task.Run(() => AcceptLoopAsync(first));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static NamedPipeServerStream CreateServer() =>
        new(DeepLinkProtocol.PipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task AcceptLoopAsync(NamedPipeServerStream server)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(server, leaveOpen: true);
                var line = await reader.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        var group = root.TryGetProperty("group", out var g) ? g.GetString() : null;
                        var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
                        _onRequest(group, action);
                    }
                    catch { /* 非法请求忽略 */ }
                }
            }
            catch { /* 连接中断 / 停机：下一轮重建 */ }
            finally
            {
                try { server.Dispose(); } catch { /* 忽略 */ }
            }
            if (_cts.IsCancellationRequested) break;
            try
            {
                server = CreateServer();
            }
            catch
            {
                break; // 管道已被其他实例接管 / 系统错误：本进程不再服务
            }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* 忽略 */ }
        _cts.Dispose();
    }
}

/// <summary>深链客户端：把请求交给已运行的 UI；没有实例在服务时快速失败（调用方继续正常启动）。</summary>
internal static class DeepLinkClient
{
    public static bool TrySend(string? group, string? action, int timeoutMs = 600)
    {
        if (string.IsNullOrWhiteSpace(group) && string.IsNullOrWhiteSpace(action)) return false;
        try
        {
            using var client = new NamedPipeClientStream(".", DeepLinkProtocol.PipeName,
                PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(new { group, action }));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
