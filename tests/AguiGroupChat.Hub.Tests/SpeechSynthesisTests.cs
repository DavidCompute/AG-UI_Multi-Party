using System.Net;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 多模态进阶 · 语音回复（TTS）：把数字员工回复文本合成为音频附件。
///
/// <para>
/// 与语音问答（ASR）相反方向；默认关闭，开启且能解析出端点才生效；失败 / 空文本静默降级不阻断。
/// 这里用假 HttpMessageHandler 覆盖端点解析、请求体、成功入库、失败降级与正文清理 / 截断。
/// </para>
/// </summary>
public sealed class SpeechSynthesisTests : IDisposable
{
    private readonly string _dir;
    private readonly AttachmentStore _store;

    public SpeechSynthesisTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "agui-tts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _store = new AttachmentStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 临时目录清理失败不影响结论 */ }
    }

    private SpeechSynthesisService Service(FakeHandler handler, Action<AudioOptions>? configure = null,
        Action<AgentOptions>? configureOptions = null)
    {
        var options = new AgentOptions { Provider = "mock" };
        configureOptions?.Invoke(options);
        var audio = new AudioOptions { SpeechEnabled = true, SpeechEndpoint = "https://tts.local/speech" };
        configure?.Invoke(audio);
        options.Audio = audio;
        return new SpeechSynthesisService(options, _store, NullLogger<SpeechSynthesisService>.Instance, new HttpClient(handler));
    }

    private static HttpResponseMessage Audio(string contentType = "audio/mpeg", int bytes = 4096)
    {
        var content = new ByteArrayContent(new byte[bytes]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public void Disabled_IsNotEnabled_AndNeverCallsEndpoint()
    {
        var handler = new FakeHandler(_ => Audio());
        var options = new AgentOptions { Provider = "mock", Audio = new AudioOptions { SpeechEnabled = false } };
        var svc = new SpeechSynthesisService(options, _store, NullLogger<SpeechSynthesisService>.Instance, new HttpClient(handler));

        Assert.False(svc.IsEnabled);
        Assert.Null(svc.Endpoint);
    }

    [Fact]
    public async Task Enabled_WithoutEndpoint_IsInert()
    {
        var handler = new FakeHandler(_ => Audio());
        var svc = Service(handler, a => a.SpeechEndpoint = null); // 且 Agents:Endpoint 为空 → 解析不出端点
        Assert.False(svc.IsEnabled);
        Assert.Null(await svc.TrySynthesizeAsync("你好"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Endpoint_DerivesFromAgentsEndpoint_WhenSpeechEndpointBlank()
    {
        var handler = new FakeHandler(_ => Audio());
        var svc = Service(handler, a => a.SpeechEndpoint = null, configureOptions: o => o.Endpoint = "https://api.example.com/base/");
        Assert.True(svc.IsEnabled);
        Assert.Equal("https://api.example.com/base/v1/audio/speech", svc.Endpoint);
    }

    [Fact]
    public async Task Success_PostsJson_AndSavesAudioAttachment()
    {
        var handler = new FakeHandler(_ => Audio("audio/mpeg", 5000));
        var svc = Service(handler, a => { a.SpeechModel = "speaches-ai/piper-en_US-lessac-medium"; a.SpeechVoice = "lessac"; },
            configureOptions: o => o.ApiKey = "sk-tts");

        var att = await svc.TrySynthesizeAsync("Hello, this is a spoken reply.");

        Assert.NotNull(att);
        Assert.Equal("audio", att!.Kind);
        Assert.Equal("audio/mpeg", att.ContentType);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        Assert.Equal("Bearer", handler.Last!.Headers.Authorization?.Scheme);
        Assert.Equal("sk-tts", handler.Last!.Headers.Authorization?.Parameter);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("speaches-ai/piper-en_US-lessac-medium", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("lessac", doc.RootElement.GetProperty("voice").GetString());
        Assert.Equal("mp3", doc.RootElement.GetProperty("response_format").GetString());
        Assert.Equal("Hello, this is a spoken reply.", doc.RootElement.GetProperty("input").GetString());

        // 音频确实落盘为附件（可被消息回挂）
        var saved = _store.TryReadBytes(att.AttachmentId);
        Assert.NotNull(saved);
        Assert.Equal(5000, saved!.Value.Bytes.Length);
    }

    [Theory]
    [InlineData("wav", ".wav", "audio/wav")]
    [InlineData("opus", ".opus", "audio/opus")]
    [InlineData("", ".mp3", "audio/mpeg")]
    public async Task SpeechFormat_ControlsExtensionAndMime(string format, string ext, string mime)
    {
        var handler = new FakeHandler(_ => Audio(mime, 2048));
        var svc = Service(handler, a => a.SpeechFormat = format);
        var att = await svc.TrySynthesizeAsync("格式测试");
        Assert.NotNull(att);
        Assert.EndsWith(ext, att!.Name);
        Assert.Equal(mime, att.ContentType);
    }

    [Fact]
    public async Task HttpFailure_ReturnsNull()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });
        var svc = Service(handler);
        Assert.Null(await svc.TrySynthesizeAsync("失败也要静默"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task EmptyOrWhitespaceText_ReturnsNull_NoRequest()
    {
        var handler = new FakeHandler(_ => Audio());
        var svc = Service(handler);
        Assert.Null(await svc.TrySynthesizeAsync("   "));
        Assert.Null(await svc.TrySynthesizeAsync(null));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task LongText_IsTruncatedToSpeechMaxChars()
    {
        var handler = new FakeHandler(_ => Audio());
        var svc = Service(handler, a => a.SpeechMaxChars = 10);
        await svc.TrySynthesizeAsync(new string('字', 50));
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(10, doc.RootElement.GetProperty("input").GetString()!.Length);
    }

    [Theory]
    [InlineData("普通正文", "普通正文")]
    [InlineData("# 标题\n正文内容", "标题 正文内容")]
    [InlineData("看 [链接](https://x.com) 和 ![图](a.png)", "看 链接 和")]
    [InlineData("`code` 与 **加粗**", "code 与 加粗")]
    public void ForSpeech_StripsMarkdownArtifacts(string input, string expected)
    {
        Assert.Equal(expected, SpeechSynthesisService.ForSpeech(input));
    }

    [Fact]
    public async Task OnlyForVoice_SkipsWhenTriggerHasNoVoice_SynthesizesWhenItDoes()
    {
        var handler = new FakeHandler(_ => Audio());
        var svc = Service(handler, a => a.SpeechOnlyForVoice = true);

        Assert.True(svc.OnlyWhenVoiceTurn);
        // 触发消息不带语音 → 跳过（不发请求）
        Assert.Null(await svc.TrySynthesizeAsync("普通文字回复", triggerHadVoice: false));
        Assert.Equal(0, handler.Calls);
        // 触发消息带语音 → 正常合成
        var att = await svc.TrySynthesizeAsync("对语音的回复", triggerHadVoice: true);
        Assert.NotNull(att);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OnlyForVoice_DefaultOn_SkipsNonVoiceTrigger()
    {
        var handler = new FakeHandler(_ => Audio());
        var svc = Service(handler); // 默认 SpeechOnlyForVoice=true
        Assert.True(svc.OnlyWhenVoiceTurn);
        Assert.Null(await svc.TrySynthesizeAsync("普通文字回复", triggerHadVoice: false));
        Assert.Equal(0, handler.Calls);
        Assert.NotNull(await svc.TrySynthesizeAsync("语音回合的回复", triggerHadVoice: true));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OnlyForVoice_ExplicitlyOff_SynthesizesRegardlessOfTrigger()
    {
        var handler = new FakeHandler(_ => Audio());
        var svc = Service(handler, a => a.SpeechOnlyForVoice = false);
        Assert.False(svc.OnlyWhenVoiceTurn);
        Assert.NotNull(await svc.TrySynthesizeAsync("普通回复", triggerHadVoice: false));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TrySynthesizeBytes_UsesVoiceOverride_AndReturnsAudioBytes()
    {
        var handler = new FakeHandler(_ => Audio("audio/mpeg", 3333));
        var svc = Service(handler, a => a.SpeechVoice = "default_voice");

        var bytes = await svc.TrySynthesizeBytesAsync("试听一下", "zf_xiaoyi");
        Assert.NotNull(bytes);
        Assert.Equal(3333, bytes!.Length);
        Assert.Equal(1, handler.Calls);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("zf_xiaoyi", doc.RootElement.GetProperty("voice").GetString()); // 覆盖生效
    }

    [Fact]
    public void VoicesEndpoint_DerivesFromSpeechEndpoint()
    {
        var handler = new FakeHandler(_ => Audio());
        var svc = Service(handler, a => a.SpeechEndpoint = "https://tts.local/api/v1/audio/speech");
        Assert.Equal("https://tts.local/api/v1/audio/voices", svc.VoicesEndpoint);
        var custom = Service(handler, a => a.SpeechEndpoint = "https://custom.local/say");
        Assert.Null(custom.VoicesEndpoint); // 非标准形状：无法推导
    }

    [Fact]
    public async Task Speed_IsSentAndClamped()
    {
        var h1 = new FakeHandler(_ => Audio());
        var s1 = Service(h1, a => a.SpeechSpeed = 1.5);
        await s1.TrySynthesizeBytesAsync("语速", null);
        using (var d1 = JsonDocument.Parse(h1.LastBody!))
            Assert.Equal(1.5, d1.RootElement.GetProperty("speed").GetDouble(), 3);

        var h2 = new FakeHandler(_ => Audio());
        var s2 = Service(h2, a => a.SpeechSpeed = 9.0); // 超上限 → 夹到 4.0
        await s2.TrySynthesizeBytesAsync("语速", null);
        using (var d2 = JsonDocument.Parse(h2.LastBody!))
            Assert.Equal(4.0, d2.RootElement.GetProperty("speed").GetDouble(), 3);
    }

    /// <summary>可编程假 HttpMessageHandler：记录调用次数与请求，按委托返回响应。</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Last = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }
}
