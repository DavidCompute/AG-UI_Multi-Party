using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace AguiGroupChat.Desktop;

internal static class DeepLinkProtocol
{
    /// <summary>同一用户会话内唯一的深链命名管道（<see cref="PipeOptions.CurrentUserOnly"/> 隔离其他用户）。</summary>
    public const string PipeName = "AguiGroupChat.Desktop.DeepLink";

    /// <summary>逐窗口管道名（1.0.312）：多开时让另一个窗口也能被定向切知聚。
    /// 只保留字母数字下划线，避免非法管道字符。</summary>
    public static string PipeNameFor(string windowId)
    {
        var safe = new string((windowId ?? "").Where((ch) => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        if (safe.Length == 0) safe = "window";
        return PipeName + "." + safe;
    }
}

/// <summary>深链请求（1.0.313 带话题 / 消息定位）：任一字段可空。</summary>
/// <param name="Group">目标知聚。</param>
/// <param name="Action">快捷动作（new-group / admin）。</param>
/// <param name="TopicId">目标话题（可空 = 主话题）。</param>
/// <param name="MessageId">目标消息（可空 = 只切到话题）。</param>
internal sealed record DeepLinkRequest(string? Group, string? Action, string? TopicId, string? MessageId);

/// <summary>
/// 深链服务端：第一个 UI 实例占有命名管道；跳转列表 / 快捷任务启动的新进程把请求交给它，
/// 由它在前台窗口内切换知聚 / 打开弹窗，避免每次深链都新开窗口（以及重复托盘图标）。
/// 1.0.313 起每个窗口额外开一条 <see cref="DeepLinkProtocol.PipeNameFor"/> 管道，供其他窗口定向投递。
/// </summary>
internal sealed class DeepLinkServer : IDisposable
{
    private readonly Action<DeepLinkRequest> _onRequest;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private bool _started;

    public DeepLinkServer(Action<DeepLinkRequest> onRequest, string? pipeName = null)
    {
        _onRequest = onRequest;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DeepLinkProtocol.PipeName : pipeName!;
    }

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

    private NamedPipeServerStream CreateServer() =>
        new(_pipeName, PipeDirection.In, 1,
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
                        var topicId = root.TryGetProperty("topicId", out var tp) ? tp.GetString() : null;
                        var messageId = root.TryGetProperty("messageId", out var mi) ? mi.GetString() : null;
                        _onRequest(new DeepLinkRequest(group, action, topicId, messageId));
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
    public static bool TrySend(string? group, string? action, string? pipeName = null, int timeoutMs = 600)
        => TrySend(group, action, topicId: null, messageId: null, pipeName: pipeName, timeoutMs: timeoutMs);

    public static bool TrySend(string? group, string? action, string? topicId, string? messageId,
        string? pipeName = null, int timeoutMs = 600)
    {
        if (string.IsNullOrWhiteSpace(group) && string.IsNullOrWhiteSpace(action)) return false;
        try
        {
            using var client = new NamedPipeClientStream(".",
                string.IsNullOrWhiteSpace(pipeName) ? DeepLinkProtocol.PipeName : pipeName!,
                PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(new { group, action, topicId, messageId }));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
