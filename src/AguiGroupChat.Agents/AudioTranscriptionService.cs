using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 语音转写（ASR）服务：把消息里的<b>语音附件</b>转成文字，供网关注入模型上下文（多模态进阶 · 语音问答）。
///
/// <para>
/// 设计约束（与视觉一致的口径）：
/// <list type="bullet">
///   <item>默认关闭；未配置端点时 <see cref="IsEnabled"/>=false，网关保持「语音仅元数据」的旧行为；</item>
///   <item>调用失败 / 超时 / 超限一律返回 null 并记日志，<b>绝不抛出、绝不阻断对话</b>；</item>
///   <item>转写结果按 attachmentId 缓存（同一段语音在多轮里只转一次，避免重复计费）。</item>
/// </list>
/// 协议与 OpenAI 音频转写一致：<c>POST {endpoint}</c>，<c>multipart/form-data</c>（<c>file</c> + <c>model</c>
/// [+ <c>language</c>]），响应 <c>{"text":"…"}</c>（非 JSON 纯文本亦兼容）。
/// </para>
/// </summary>
public sealed class AudioTranscriptionService : IDisposable
{
    /// <summary>转写缓存上限（超出即清空重建，避免无界增长）。</summary>
    private const int CacheMax = 1000;

    private readonly AttachmentStore? _store;
    private readonly ILogger<AudioTranscriptionService> _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly AudioOptions? _audio;
    private readonly string? _endpoint;
    private readonly string? _apiKey;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public AudioTranscriptionService(AgentOptions options, AttachmentStore? store, ILogger<AudioTranscriptionService> logger, HttpClient? http = null)
    {
        _store = store;
        _logger = logger;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan, // 单次超时由 CancellationTokenSource 控制
        };
        _audio = options.Audio;
        if (_audio is not { Enabled: true }) return;
        _endpoint = !string.IsNullOrWhiteSpace(_audio.Endpoint)
            ? _audio.Endpoint!.Trim()
            : (!string.IsNullOrWhiteSpace(options.Endpoint)
                ? options.Endpoint!.Trim().TrimEnd('/') + "/v1/audio/transcriptions"
                : null);
        _apiKey = !string.IsNullOrWhiteSpace(_audio.ApiKey) ? _audio.ApiKey : options.ApiKey;
    }

    /// <summary>是否可用：已启用、能解析出端点、且有附件存储。为 false 时网关保持旧行为（语音仅元数据）。</summary>
    public bool IsEnabled => _audio is { Enabled: true } && _endpoint is not null && _store is not null;

    /// <summary>单条消息最多转写的语音文件数（0 = 禁用转写）。</summary>
    public int MaxFilesPerMessage => IsEnabled ? Math.Max(0, _audio!.MaxFilesPerMessage) : 0;

    /// <summary>转写端点（诊断 / 日志；未启用为 null）。</summary>
    public string? Endpoint => _endpoint;

    /// <summary>
    /// 尝试把语音附件转写为文字。不可用 / 非语音 / 读取失败 / 超限 / 请求失败均返回 null（调用方按「无转写」处理）。
    /// </summary>
    public async Task<string?> TryTranscribeAsync(AttachmentInfo attachment, CancellationToken ct = default)
    {
        if (!IsEnabled) return null;
        if (!string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase)
            && attachment.ContentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) != true)
            return null;
        if (_cache.TryGetValue(attachment.AttachmentId, out var cached)) return cached;

        var o = _audio!;
        var file = _store!.TryReadBytes(attachment.AttachmentId, o.MaxBytes);
        if (file is null)
        {
            _logger.LogInformation("语音转写跳过：附件不可读或超过 {Max} 字节（{Id}）", o.MaxBytes, attachment.AttachmentId);
            return null;
        }

        try
        {
            using var form = new MultipartFormDataContent();
            var bytes = new ByteArrayContent(file.Value.Bytes);
            bytes.Headers.ContentType = new MediaTypeHeaderValue(SafeMime(file.Value.ContentType));
            form.Add(bytes, "file", file.Value.FileName);
            form.Add(new StringContent(o.Model), "model");
            if (!string.IsNullOrWhiteSpace(o.Language)) form.Add(new StringContent(o.Language!), "language");

            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = form };
            if (!string.IsNullOrWhiteSpace(_apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 5, 600)));
            using var resp = await _http.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("语音转写失败 HTTP {Status}：{Body}", (int)resp.StatusCode, Truncate(body, 300));
                return null;
            }

            var text = ParseTranscript(body);
            if (string.IsNullOrWhiteSpace(text)) { _logger.LogWarning("语音转写返回空文本（{Id}）", attachment.AttachmentId); return null; }
            text = text.Trim();
            if (_cache.Count >= CacheMax) _cache.Clear();
            _cache[attachment.AttachmentId] = text;
            return text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "语音转写异常（忽略，本次按「无转写」处理；{Id}）", attachment.AttachmentId);
            return null;
        }
    }

    /// <summary>解析转写响应：标准 <c>{"text":"…"}</c>；非 JSON 时把整段响应体当纯文本（兼容非标准端点）。</summary>
    private static string? ParseTranscript(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var trimmed = body.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    return t.GetString();
                return null; // 是 JSON 但没有 text 字段：不猜
            }
            catch (JsonException) { /* 落空按纯文本处理 */ }
        }
        return trimmed;
    }

    private static string SafeMime(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return "application/octet-stream";
        var semi = contentType!.IndexOf(';');
        return (semi > 0 ? contentType[..semi] : contentType).Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>清空转写缓存（系统初始化 / 账号擦除用）。</summary>
    public void ClearCache() => _cache.Clear();

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
