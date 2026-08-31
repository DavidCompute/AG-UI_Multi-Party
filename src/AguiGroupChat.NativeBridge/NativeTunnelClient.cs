using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AguiGroupChat.NativeBridge;

/// <summary>
/// 基于 HTTP/SSE 的反向隧道<b>客户端</b>（跑在“没有公网 IP”的内网机器上）：
/// 主动向公网 Hub 发起一条 SSE 长连接并注册（绑定一个数字员工 agentId），
/// Hub 把对该数字员工的「客户端技能（本机 shell）」执行任务沿隧道下行，本客户端用 <see cref="ShellRunner"/>
/// 执行并把结果 <c>POST</c> 回 Hub —— 从而让内网桥被公网/网关调用执行，无需公网 IP。
/// 断线自动重连。协议见 Hub 侧 <c>NativeTunnelApi</c>。
/// </summary>
public sealed class NativeTunnelClient
{
    /// <summary>JSON 反序列化选项：Hub 下行载荷为 camelCase，需大小写不敏感映射到 record 字段。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // SSE 长连接不能被 HttpClient 默认 100s 请求超时中断（中断会周期性断开隧道、重连，导致调用瞬间没有桥而回落到服务器端）；
    // 隧道连接的生命周期由 Hub 端 SSE 流 + 本端 _quit 取消令牌控制，客户端不设请求超时。
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _hubBase;
    private readonly string _agent;
    private readonly string _token;
    private readonly ShellRunner _runner = new();
    private readonly CancellationTokenSource _quit = new();

    public NativeTunnelClient(string hubBase, string agent, string token)
    {
        _hubBase = hubBase.TrimEnd('/');
        _agent = agent;
        _token = token;
    }

    public void Dispose() => _quit.Cancel();

    /// <summary>启动隧道（阻塞）：循环连接 + 处理任务，直到进程退出 / 取消。</summary>
    public async Task RunAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _quit.Token);
        var ct = linked.Token;
        Console.WriteLine($"  [隧道] 本机桥将经隧道提供给数字员工：{_agent}");
        Console.WriteLine($"  [隧道] Hub: {_hubBase}  (断线自动重连)");
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(ct).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(2); // 正常连接返回（断开）后重置退避
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"  [隧道] 连接异常：{ex.Message}（{backoff.TotalSeconds}s 后重连）");
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2)); // 指数退避
            }
        }
    }

    /// <summary>完成一次隧道会话：连上、收任务、逐个执行并回传；连接断开则返回（由外层重连）。</summary>
    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        var url = $"{_hubBase}/ag-ui/native-tunnel/connect";
        using var req = new HttpRequestMessage(HttpMethod.Get, url)
        {
            RequestUri = new Uri($"{url}?agent={Uri.EscapeDataString(_agent)}&token={Uri.EscapeDataString(_token)}"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        Console.WriteLine("  [隧道] 已连入 Hub（等待任务…）");

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var dataLines = new List<string>();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            var trimmed = line.TrimStart('\r');
            if (trimmed.Length == 0)
            {
                // SSE 事件结束：若积累了 data 行，则处理（Hub 只发 data 字段）
                if (dataLines.Count is not 0)
                {
                    var payload = string.Concat(dataLines);
                    dataLines.Clear();
                    await HandleTaskAsync(payload, ct).ConfigureAwait(false);
                }
                continue;
            }
            if (trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                var val = trimmed["data:".Length..];
                if (val.StartsWith(' ')) val = val[1..];
                dataLines.Add(val);
            }
            // 其它字段(id/event/retry)忽略
        }
        // 流正常结束(服务器关闭)= 连接断开,返回让外层重连
        Console.WriteLine("  [隧道] 隧道连接已断开，准备重连");
    }

    /// <summary>解析并执行一个下行任务，然后回传结果。</summary>
    private async Task HandleTaskAsync(string payload, CancellationToken ct)
    {
        TunnelTask? task = null;
        try
        {
            // 大小写不敏感：Hub 下行 JSON 为 camelCase（taskId/kind/command/cwd/timeoutSec/query），
            // 与本地 record（PascalCase）字段匹配。默认 JsonSerializer 大小写敏感，缺失映射会导致 TaskId 为 null 而静默丢弃任务。
            task = JsonSerializer.Deserialize<TunnelTask>(payload, JsonOptions);
        }
        catch { Console.WriteLine("  [隧道] 无法解析任务：" + Truncate(payload)); return; }
        if (task is null || string.IsNullOrWhiteSpace(task.TaskId))
            return;

        string? output = null; string? errorLog = null;
        try
        {
            output = await _runner.RunAsync(task.Command ?? "", task.Cwd, task.TimeoutSec, task.Query, ct).ConfigureAwait(false);
            Console.WriteLine($"  [隧道] 任务 {task.TaskId} 执行完成（{Truncate(output ?? "")}）");
        }
        catch (Exception ex)
        {
            errorLog = "本机执行失败：" + ex.Message;
            Console.WriteLine($"  [隧道] 任务 {task.TaskId} 执行异常：{ex.Message}");
        }
        await PostResultAsync(task.TaskId, output, errorLog, ct).ConfigureAwait(false);
    }

    private async Task PostResultAsync(string taskId, string? output, string? errorLog, CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { taskId, output, errorLog, agent = _agent, token = _token });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{_hubBase}/ag-ui/native-tunnel/result", content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                Console.WriteLine($"  [隧道] 结果回传失败：HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [隧道] 结果回传异常：{ex.Message}");
        }
    }

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..120] + "…";

    /// <summary>下行任务体（与 Hub 侧序列化字段对齐）。</summary>
    private sealed record TunnelTask(string TaskId, string? Kind, string? Command, string? Cwd, int? TimeoutSec, string? Query);
}
