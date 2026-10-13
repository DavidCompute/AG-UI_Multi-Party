using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;

namespace AguiGroupChat.Web;

/// <summary>一次 Webhook 投递的记录（用于管理界面「最近投递」展示与排障）。</summary>
public sealed record WebhookDeliveryRecord
{
    public string DeliveryId { get; init; } = "";
    public string SubscriptionId { get; init; } = "";
    public string EventType { get; init; } = "";
    public string GroupId { get; init; } = "";
    public string Url { get; init; } = "";
    public int Attempt { get; init; }
    public int? StatusCode { get; init; }
    public bool Ok { get; init; }
    public long AtMs { get; init; }
    public long DurationMs { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Webhook 出站调度器：实现 <see cref="IGroupEventObserver"/>，把 <see cref="GroupHub"/> 扇出的事件
/// 按订阅过滤后异步投递到外部回调地址。
///
/// 关键约束：<see cref="OnEvent"/> 在实时扇出路径上被同步调用（每条消息、每个流式增量都会命中），
/// 因此这里只做「取事件类型 → 过滤 → 入队」，绝不发起网络 I/O；真正的 HTTP 投递由后台工作线程完成。
///
/// 投递语义：每次投递带 HMAC-SHA256 签名（<c>X-KnowGath-Signature: sha256=&lt;hex&gt;</c>，
/// 签名内容为 <c>{timestamp}.{body}</c>），失败重试至多 <see cref="MaxAttempts"/> 次（指数退避），
/// 单次超时 <see cref="AttemptTimeoutMs"/>；投递结果记入内存环形缓冲（最近 <see cref="MaxLog"/> 条）。
/// </summary>
public sealed class WebhookDispatcher : IGroupEventObserver, IDisposable
{
    private const int MaxAttempts = 3;
    private const int AttemptTimeoutMs = 10_000;
    private const int MaxLog = 100;
    private const int QueueCapacity = 5_000;

    /// <summary>重试退避（秒，按尝试序号取用）。默认 <c>[1, 5, 25]</c>；测试可调小以避免长等待。</summary>
    public IReadOnlyList<int> RetryBackoffSeconds { get; set; } = [1, 5, 25];

    /// <summary>空订阅（= 全部事件）时排除的高频噪声类型：纯流式增量与输入状态。</summary>
    private static readonly HashSet<string> NoiseTypes = new(StringComparer.Ordinal)
    {
        EventTypes.TextMessageContent,
        EventTypes.TextMessageReasoning,
        EventTypes.GroupTyping,
        EventTypes.GroupMessageRead,
    };

    private readonly WebhookState _state;
    private readonly SecretVault? _vault;
    private readonly ILogger<WebhookDispatcher> _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Channel<DeliveryJob> _queue;
    private readonly ConcurrentQueue<WebhookDeliveryRecord> _log = new();
    private readonly CancellationTokenSource _cts = new();
    private Task[]? _workers;

    private sealed record DeliveryJob(WebhookSubscription Sub, string DeliveryId, string EventType, string GroupId, string Body);

    // 事件类型读取器缓存：OnEvent 在热路径上，避免每次反射。
    private static readonly ConcurrentDictionary<Type, Func<object, string?>> TypeReaders = new();

    public WebhookDispatcher(WebhookState state, SecretVault? vault, ILogger<WebhookDispatcher> logger, HttpClient? http = null)
    {
        _state = state;
        _vault = vault;
        _logger = logger;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan, // 单次超时由 CancellationTokenSource 控制（便于区分取消来源）
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KnowGath-Webhook/1.0");
        _queue = Channel.CreateBounded<DeliveryJob>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite, // 队列满时丢弃新事件（集成是增强，不反压主流程）
        });
    }

    /// <summary>启动后台投递工作线程（幂等）。</summary>
    public void Start()
    {
        if (_workers is not null) return;
        // 少量并发即可覆盖多数集成场景；单目标顺序由重试内联阻塞保证。
        var count = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        _workers = Enumerable.Range(0, count).Select(_ => Task.Run(() => ProcessAsync(_cts.Token))).ToArray();
    }

    /// <inheritdoc />
    public void OnEvent(string groupId, object evt)
    {
        try
        {
            var subs = _state.Snapshot();
            if (subs.Count == 0) return;
            var type = EventTypeOf(evt);
            if (string.IsNullOrEmpty(type)) return;

            // 先按订阅过滤，仅当至少有一个订阅命中时才序列化事件体（避免无谓的 per-delta 序列化）
            List<WebhookSubscription>? matched = null;
            foreach (var sub in subs)
            {
                if (!sub.Enabled) continue;
                if (!ShouldDeliver(sub, groupId, type!)) continue;
                (matched ??= []).Add(sub);
            }
            if (matched is null || matched.Count == 0) return;

            var data = JsonSerializer.SerializeToElement(evt, AguiJson.Options);
            foreach (var sub in matched)
            {
                var deliveryId = "wd_" + IdGenerator.NewId();
                var body = BuildEnvelope(deliveryId, type!, groupId, data);
                var job = new DeliveryJob(sub, deliveryId, type!, groupId, body);
                if (!_queue.Writer.TryWrite(job))
                    _logger.LogWarning("Webhook 队列已满，丢弃事件 {Type}（订阅 {Id}）", type, sub.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Webhook 事件入队失败（忽略）");
        }
    }

    /// <summary>订阅过滤（纯函数，可单独测试）：可选群过滤 + 事件类型匹配；订阅未指定事件类型时
    /// 按「除高频噪声外的全部事件」语义。</summary>
    public static bool ShouldDeliver(WebhookSubscription sub, string groupId, string eventType)
    {
        if (sub.GroupId is { Length: > 0 } g && !string.Equals(g, groupId, StringComparison.Ordinal)) return false;
        return sub.EventTypes.Count > 0
            ? sub.EventTypes.Contains(eventType, StringComparer.Ordinal)
            : !NoiseTypes.Contains(eventType);
    }

    /// <summary>从事件对象读取 <c>Type</c> 属性（按具体类型缓存读取器，避免热路径反射开销）。</summary>
    public static string? EventTypeOf(object evt)
    {
        var reader = TypeReaders.GetOrAdd(evt.GetType(), t =>
        {
            var p = t.GetProperty("Type");
            return p is not null && p.PropertyType == typeof(string)
                ? o => (string?)p.GetValue(o)
                : _ => null;
        });
        return reader(evt);
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(ct))
            {
                try { await DeliverAsync(job, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Webhook 投递处理异常（订阅 {Id}）", job.Sub.Id); }
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    private async Task DeliverAsync(DeliveryJob job, CancellationToken ct)
    {
        _ = await DeliverWithRetryAsync(job, ct);
    }

    /// <summary>带重试的投递：返回最后一次投递记录（成功或最终失败）。</summary>
    private async Task<WebhookDeliveryRecord> DeliverWithRetryAsync(DeliveryJob job, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var record = await AttemptAsync(job, attempt, ct);
            Record(record);
            if (record.Ok)
            {
                _state.MarkDelivered(job.Sub.Id, record.AtMs);
                return record;
            }
            if (attempt >= MaxAttempts)
            {
                _logger.LogWarning("Webhook 投递最终失败（订阅 {Id}，事件 {Type}，HTTP {Status}）：{Error}",
                    job.Sub.Id, job.EventType, record.StatusCode, record.Error);
                return record;
            }
            var backoff = RetryBackoffSeconds;
            var seconds = backoff.Count > 0 ? backoff[Math.Min(attempt - 1, backoff.Count - 1)] : 1;
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); }
            catch (OperationCanceledException) { return record; }
        }
    }

    /// <summary>直接投递一个事件（含重试），并等待最终结果。用于测试与需要同步结果的场景；
    /// 实时路径走 <see cref="OnEvent"/> + 后台工作线程。</summary>
    public async Task<WebhookDeliveryRecord> DeliverEventAsync(WebhookSubscription sub, string eventType, string groupId, object evt,
        CancellationToken ct = default)
    {
        var deliveryId = "wd_" + IdGenerator.NewId();
        var body = BuildEnvelope(deliveryId, eventType, groupId, JsonSerializer.SerializeToElement(evt, AguiJson.Options));
        return await DeliverWithRetryAsync(new DeliveryJob(sub, deliveryId, eventType, groupId, body), ct);
    }

    private async Task<WebhookDeliveryRecord> AttemptAsync(DeliveryJob job, int attempt, CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow;
        var ts = start.ToUnixTimeMilliseconds();
        var secret = _vault?.Decrypt(job.Sub.Secret) ?? job.Sub.Secret;
        var signature = Sign(secret, ts, job.Body);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, job.Sub.Url)
            {
                Content = new StringContent(job.Body, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("X-KnowGath-Event", job.EventType);
            req.Headers.TryAddWithoutValidation("X-KnowGath-Delivery", job.DeliveryId);
            req.Headers.TryAddWithoutValidation("X-KnowGath-Timestamp", ts.ToString());
            req.Headers.TryAddWithoutValidation("X-KnowGath-Signature", "sha256=" + signature);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AttemptTimeoutMs);
            using var resp = await _http.SendAsync(req, cts.Token);
            var status = (int)resp.StatusCode;
            var ok = status is >= 200 and < 300;
            return new WebhookDeliveryRecord
            {
                DeliveryId = job.DeliveryId,
                SubscriptionId = job.Sub.Id,
                EventType = job.EventType,
                GroupId = job.GroupId,
                Url = job.Sub.Url,
                Attempt = attempt,
                StatusCode = status,
                Ok = ok,
                AtMs = ts,
                DurationMs = (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds,
                Error = ok ? null : $"HTTP {status}",
            };
        }
        catch (Exception ex)
        {
            return new WebhookDeliveryRecord
            {
                DeliveryId = job.DeliveryId,
                SubscriptionId = job.Sub.Id,
                EventType = job.EventType,
                GroupId = job.GroupId,
                Url = job.Sub.Url,
                Attempt = attempt,
                StatusCode = null,
                Ok = false,
                AtMs = ts,
                DurationMs = (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds,
                Error = ex switch
                {
                    OperationCanceledException when ct.IsCancellationRequested => "已取消",
                    OperationCanceledException => $"超时（{AttemptTimeoutMs / 1000}s）",
                    _ => ex.Message,
                },
            };
        }
    }

    /// <summary>发送一条测试投递（PING），同步返回结果（供管理界面「测试」按钮）。</summary>
    public async Task<WebhookDeliveryRecord> SendTestAsync(WebhookSubscription sub, CancellationToken ct)
    {
        var deliveryId = "wd_test_" + IdGenerator.NewId();
        var groupId = sub.GroupId ?? "";
        var body = BuildEnvelope(deliveryId, "PING", groupId,
            AguiJson.Element(new { message = "KnowGath webhook 测试投递" }));
        var job = new DeliveryJob(sub, deliveryId, "PING", groupId, body);
        var record = await AttemptAsync(job, 1, ct);
        Record(record);
        if (record.Ok) _state.MarkDelivered(sub.Id, record.AtMs);
        return record;
    }

    private static string BuildEnvelope(string deliveryId, string eventType, string groupId, JsonElement data)
        => AguiJson.Serialize(new
        {
            deliveryId,
            @event = eventType,
            groupId,
            sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            data,
        });

    /// <summary>HMAC-SHA256 签名（十六进制小写）：签名内容为 <c>{timestamp}.{body}</c>。</summary>
    public static string Sign(string secret, long timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var data = Encoding.UTF8.GetBytes(timestamp.ToString() + "." + body);
        return Convert.ToHexString(hmac.ComputeHash(data)).ToLowerInvariant();
    }

    private void Record(WebhookDeliveryRecord record)
    {
        _log.Enqueue(record);
        while (_log.Count > MaxLog && _log.TryDequeue(out _)) { }
    }

    /// <summary>最近的投递记录（新→旧）。</summary>
    public IReadOnlyList<WebhookDeliveryRecord> RecentDeliveries() => _log.Reverse().ToList();

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* 忽略 */ }
        _queue.Writer.TryComplete();
        if (_ownsHttp) _http.Dispose();
        _cts.Dispose();
    }
}
