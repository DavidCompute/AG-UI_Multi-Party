using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 语音回复（TTS）服务：把数字员工的<b>文字回复</b>合成为音频文件并存为附件，供消息回挂（多模态进阶 · 语音出）。
///
/// <para>
/// 与语音问答（<see cref="AudioTranscriptionService"/>）相反方向：这里是「文字 → 音频」。设计约束一致：
/// <list type="bullet">
///   <item>默认关闭；未配置 / 未启用时网关不产生任何音频，行为与旧版一致；</item>
///   <item>调用失败 / 超时 / 空文本一律返回 null 并记日志，<b>绝不抛出、绝不阻断回复</b>；</item>
///   <item>仅合成正文前 <see cref="AudioOptions.SpeechMaxChars"/> 字符（控成本与时长）。</item>
/// </list>
/// 协议与 OpenAI 语音合成一致：<c>POST {endpoint}</c> JSON
/// <c>{ model, input, voice, response_format }</c>，响应为音频字节。
/// </para>
/// </summary>
public sealed class SpeechSynthesisService : IDisposable
{
    private readonly AttachmentStore? _store;
    private readonly ILogger<SpeechSynthesisService> _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly AudioOptions? _audio;
    private readonly string? _endpoint;
    private readonly string? _apiKey;

    public SpeechSynthesisService(AgentOptions options, AttachmentStore? store, ILogger<SpeechSynthesisService> logger, HttpClient? http = null)
    {
        _store = store;
        _logger = logger;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan, // 单次超时由 CancellationTokenSource 控制
        };
        _audio = options.Audio;
        if (_audio is not { SpeechEnabled: true }) return;
        _endpoint = !string.IsNullOrWhiteSpace(_audio.SpeechEndpoint)
            ? _audio.SpeechEndpoint!.Trim()
            : (!string.IsNullOrWhiteSpace(options.Endpoint)
                ? options.Endpoint!.Trim().TrimEnd('/') + "/v1/audio/speech"
                : null);
        _apiKey = !string.IsNullOrWhiteSpace(_audio.ApiKey) ? _audio.ApiKey : options.ApiKey;
    }

    /// <summary>是否可用：已启用语音回复、能解析出端点、且有附件存储。</summary>
    public bool IsEnabled => _audio is { SpeechEnabled: true } && _endpoint is not null && _store is not null;

    /// <summary>是否只在触发消息带语音附件时才合成（<see cref="AudioOptions.SpeechOnlyForVoice"/>）。</summary>
    public bool OnlyWhenVoiceTurn => _audio?.SpeechOnlyForVoice == true;

    /// <summary>合成端点（诊断 / 日志；未启用为 null）。</summary>
    public string? Endpoint => _endpoint;

    /// <summary>
    /// 尝试把回复文本合成为音频附件。不可用 / 空文本 / 请求失败返回 null（调用方按「无语音」处理）。
    /// <paramref name="triggerHadVoice"/>：触发本次回复的消息是否带语音附件；
    /// 当配置了 <see cref="AudioOptions.SpeechOnlyForVoice"/> 且它为 false 时直接返回 null（不合成）。
    /// </summary>
    public async Task<AttachmentInfo?> TrySynthesizeAsync(string? text, CancellationToken ct = default, bool triggerHadVoice = true)
    {
        if (!IsEnabled) return null;
        if (OnlyWhenVoiceTurn && !triggerHadVoice) return null;
        var clean = ForSpeech(text);
        if (clean.Length == 0) return null;

        var o = _audio!;
        if (clean.Length > o.SpeechMaxChars) clean = clean[..o.SpeechMaxChars];

        var (ext, mime) = FormatOf(o.SpeechFormat);
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = string.IsNullOrWhiteSpace(o.SpeechModel) ? "tts-1" : o.SpeechModel,
                ["input"] = clean,
                ["voice"] = string.IsNullOrWhiteSpace(o.SpeechVoice) ? "alloy" : o.SpeechVoice,
                ["response_format"] = o.SpeechFormat,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(payload) };
            if (!string.IsNullOrWhiteSpace(_apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.SpeechTimeoutSeconds, 5, 1800)));
            using var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                _logger.LogWarning("语音回复合成失败 HTTP {Status}：{Body}", (int)resp.StatusCode, Truncate(body, 300));
                return null;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            if (bytes.Length == 0) { _logger.LogWarning("语音回复合成返回空音频"); return null; }

            using var ms = new MemoryStream(bytes);
            var info = _store!.Save("语音回复" + ext, mime, ms, bytes.Length);
            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "语音回复合成异常（忽略，本次不挂语音）");
            return null;
        }
    }

    /// <summary>把正文整理成适合朗读的纯文本：去代码块 / 行内代码 / 图片 / 链接标记 / 标题符号 / 强调符。</summary>
    internal static string ForSpeech(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text;
        s = Regex.Replace(s, "```[\\s\\S]*?```", " ");          // 围栏代码块
        s = Regex.Replace(s, "`([^`]*)`", "$1");                 // 行内代码
        s = Regex.Replace(s, "!\\[[^\\]]*\\]\\([^)]*\\)", " ");   // 图片
        s = Regex.Replace(s, "\\[([^\\]]*)\\]\\([^)]*\\)", "$1"); // 链接 → 文本
        s = Regex.Replace(s, "(?m)^\\s{0,3}#{1,6}\\s*", "");      // 标题 #
        s = Regex.Replace(s, "[*_~>]", "");                      // 强调 / 引用符
        s = Regex.Replace(s, "\\s+", " ");
        return s.Trim();
    }

    private static (string Ext, string Mime) FormatOf(string? format) => (format ?? "").Trim().ToLowerInvariant() switch
    {
        "wav" => (".wav", "audio/wav"),
        "opus" => (".opus", "audio/opus"),
        "aac" => (".aac", "audio/aac"),
        "flac" => (".flac", "audio/flac"),
        _ => (".mp3", "audio/mpeg"),
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
