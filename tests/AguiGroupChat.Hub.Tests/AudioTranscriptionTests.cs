using System.Net;
using System.Text;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 多模态进阶 · 语音问答（ASR）：语音附件转写为文字并注入模型上下文。
///
/// <para>
/// 此前的行为是「语音仅携元数据供前端播放、完全不进上下文」（<see cref="AttachmentStore.IsExtractable"/>
/// 对 audio 返回 false，且被 <c>AttachmentStoreTests</c> / <c>GroupHubTests</c> 钉住，本类不改该语义）。
/// 语音问答是<b>网关层</b>在组织附件区时额外做的：开启且能解析出端点时，先转写再把转写文本当作该附件的
/// 「正文」走同一条注入 / 预算路径。
/// </para>
///
/// <para>
/// 覆盖要点：默认关闭 / 端点派生 / 成功解析（含非 JSON 纯文本）/ HTTP 失败与空文本静默降级 / 按附件缓存 /
/// 非语音与非音频读取失败跳过 / 单条消息转写数量上限，以及网关级「转写文本确实进入附件区并带『语音转写』标签」。
/// </para>
/// </summary>
public sealed class AudioTranscriptionTests : IDisposable
{
    private readonly string _dir;
    private readonly AttachmentStore _store;

    public AudioTranscriptionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "agui-asr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _store = new AttachmentStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 临时目录清理失败不影响结论 */ }
    }

    /// <summary>保存一个语音附件（默认 4 字节占位内容，分类只看扩展名/MIME）。</summary>
    private AttachmentInfo SaveAudio(string name = "voice.mp3", int size = 4)
    {
        using var ms = new MemoryStream(new byte[size]);
        return _store.Save(name, "audio/mpeg", ms, size);
    }

    /// <summary>构造被测服务：默认开启转写，端点由调用方给定。</summary>
    private AudioTranscriptionService Service(FakeHandler handler, Action<AudioOptions>? configure = null,
        Action<AgentOptions>? configureOptions = null)
    {
        var options = new AgentOptions { Provider = "mock" };
        configureOptions?.Invoke(options);
        // 默认给端点（代表“已正确配置”的常态）；专测端点派生/缺失的用例自行覆盖为 null。
        var audio = new AudioOptions { Enabled = true, Endpoint = "https://asr.local/t" };
        configure?.Invoke(audio);
        options.Audio = audio;
        return new AudioTranscriptionService(options, _store, NullLogger<AudioTranscriptionService>.Instance, new HttpClient(handler));
    }

    private static HttpResponseMessage Ok(string body, string contentType = "application/json")
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private static HttpResponseMessage Status(HttpStatusCode code)
        => new(code) { Content = new StringContent("{\"error\":\"boom\"}", Encoding.UTF8, "application/json") };

    // ── 配置 / 端点解析 ────────────────────────────────────────────────

    [Fact]
    public void Disabled_IsNotEnabled_AndNeverCallsEndpoint()
    {
        var handler = new FakeHandler(_ => Ok("""{"text":"你好"}"""));
        var options = new AgentOptions { Provider = "mock", Audio = new AudioOptions { Enabled = false } };
        var svc = new AudioTranscriptionService(options, _store,
            NullLogger<AudioTranscriptionService>.Instance, new HttpClient(handler));

        Assert.False(svc.IsEnabled);
        Assert.Equal(0, svc.MaxFilesPerMessage);
        Assert.Null(svc.Endpoint);
    }

    [Fact]
    public async Task Enabled_WithoutAnyEndpoint_IsInert()
    {
        // Enabled=true 但既无 Agents:Audio:Endpoint 也无 Agents:Endpoint → 解析不出端点，保持旧行为。
        var handler = new FakeHandler(_ => Ok("""{"text":"你好"}"""));
        var svc = Service(handler, a => a.Endpoint = null);
        Assert.False(svc.IsEnabled);

        var att = SaveAudio();
        Assert.Null(await svc.TryTranscribeAsync(att));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Endpoint_DerivesFromAgentsEndpoint_WhenAudioEndpointBlank()
    {
        var handler = new FakeHandler(_ => Ok("{}"));
        var svc = Service(handler,
            configure: a => a.Endpoint = null,
            configureOptions: o => o.Endpoint = "https://api.example.com/base/");

        Assert.True(svc.IsEnabled);
        Assert.Equal("https://api.example.com/base/v1/audio/transcriptions", svc.Endpoint);
    }

    [Fact]
    public void ExplicitAudioEndpoint_WinsOverAgentsEndpoint()
    {
        var handler = new FakeHandler(_ => Ok("{}"));
        var svc = Service(handler,
            configure: a => a.Endpoint = "https://asr.local/transcribe",
            configureOptions: o => o.Endpoint = "https://api.example.com");

        Assert.Equal("https://asr.local/transcribe", svc.Endpoint);
    }

    [Fact]
    public void MaxFilesPerMessage_DefaultsToThree_AndHonorsConfig()
    {
        var handler = new FakeHandler(_ => Ok("{}"));
        Assert.Equal(3, Service(handler).MaxFilesPerMessage);
        Assert.Equal(1, Service(handler, a => a.MaxFilesPerMessage = 1).MaxFilesPerMessage);
    }

    // ── 成功路径 ──────────────────────────────────────────────────────

    [Fact]
    public async Task Success_ParsesJsonText_AndSendsMultipartWithModelLanguageAndAuth()
    {
        var handler = new FakeHandler(_ => Ok("""{"text":"  请帮我写一份周报  "}"""));
        var svc = Service(handler,
            configure: a => { a.Endpoint = "https://asr.local/transcribe"; a.Language = "zh"; },
            configureOptions: o => o.ApiKey = "sk-test");

        var att = SaveAudio();
        var text = await svc.TryTranscribeAsync(att);

        Assert.Equal("请帮我写一份周报", text); // 结果已 Trim
        Assert.Equal(1, handler.Calls);
        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        Assert.Equal("https://asr.local/transcribe", handler.Last!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Last!.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test", handler.Last!.Headers.Authorization?.Parameter);
        Assert.Equal("multipart/form-data", handler.Last!.Content!.Headers.ContentType?.MediaType);

        // multipart 各部分的 name 是否带引号随框架版本而异，这里只校验字段名与值都确实在体内
        var body = handler.LastBody!;
        Assert.Contains("form-data", body);
        Assert.Contains("model", body);
        Assert.Contains("whisper-1", body);
        Assert.Contains("language", body);
        Assert.Contains("zh", body);
        Assert.Contains("voice.mp3", body);
    }

    [Fact]
    public async Task NonJsonPlainText_IsUsedVerbatim()
    {
        var handler = new FakeHandler(_ => Ok("  你好世界  ", "text/plain"));
        var svc = Service(handler, a => a.Endpoint = "https://asr.local/t");

        Assert.Equal("你好世界", await svc.TryTranscribeAsync(SaveAudio()));
    }

    [Fact]
    public async Task CustomModel_IsSentInsteadOfDefault()
    {
        var handler = new FakeHandler(_ => Ok("""{"text":"ok"}"""));
        var svc = Service(handler, a => { a.Endpoint = "https://asr.local/t"; a.Model = "whisper-large-v3"; });

        await svc.TryTranscribeAsync(SaveAudio());
        Assert.Contains("whisper-large-v3", handler.LastBody!);
        Assert.DoesNotContain("whisper-1", handler.LastBody!);
    }

    // ── 降级路径（一律返回 null，绝不抛出） ─────────────────────────────

    [Fact]
    public async Task HttpFailure_ReturnsNull()
    {
        var handler = new FakeHandler(_ => Status(HttpStatusCode.InternalServerError));
        var svc = Service(handler, a => a.Endpoint = "https://asr.local/t");
        Assert.Null(await svc.TryTranscribeAsync(SaveAudio()));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task WhitespaceText_ReturnsNull()
    {
        var handler = new FakeHandler(_ => Ok("""{"text":"   "}"""));
        var svc = Service(handler, a => a.Endpoint = "https://asr.local/t");
        Assert.Null(await svc.TryTranscribeAsync(SaveAudio()));
    }

    [Fact]
    public async Task JsonWithoutTextField_ReturnsNull()
    {
        var handler = new FakeHandler(_ => Ok("""{"result":"ops"}"""));
        var svc = Service(handler, a => a.Endpoint = "https://asr.local/t");
        Assert.Null(await svc.TryTranscribeAsync(SaveAudio()));
    }

    [Fact]
    public async Task NonAudioAttachment_IsSkippedWithoutRequest()
    {
        var handler = new FakeHandler(_ => Ok("""{"text":"不应被调用"}"""));
        var svc = Service(handler, a => a.Endpoint = "https://asr.local/t");

        AttachmentInfo txt;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("纯文本")))
            txt = _store.Save("note.txt", "text/plain", ms, ms.Length);

        Assert.Null(await svc.TryTranscribeAsync(txt));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task OversizeAudio_IsSkippedWithoutRequest()
    {
        var handler = new FakeHandler(_ => Ok("""{"text":"不应被调用"}"""));
        var svc = Service(handler, a => { a.Endpoint = "https://asr.local/t"; a.MaxBytes = 2; });

        var att = SaveAudio(size: 4); // 4 字节 > 2 字节上限
        Assert.Null(await svc.TryTranscribeAsync(att));
        Assert.Equal(0, handler.Calls);
    }

    // ── 缓存 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SameAttachment_TranscribedOnlyOnce()
    {
        var handler = new FakeHandler(_ => Ok("""{"text":"缓存命中"}"""));
        var svc = Service(handler, a => a.Endpoint = "https://asr.local/t");
        var att = SaveAudio();

        Assert.Equal("缓存命中", await svc.TryTranscribeAsync(att));
        Assert.Equal("缓存命中", await svc.TryTranscribeAsync(att));
        Assert.Equal(1, handler.Calls); // 第二次直接命中缓存，不再发请求

        svc.ClearCache();
        Assert.Equal("缓存命中", await svc.TryTranscribeAsync(att));
        Assert.Equal(2, handler.Calls); // 清缓存后重新转写
    }

    // ── 网关级装配：转写文本确实进入附件区 ───────────────────────────────

    private static AgentOptions GatewayOptions() => new()
    {
        Provider = "mock",
        Agents =
        [
            new AgentDefinition
            {
                AgentId = "agent_a", Nickname = "语音助手", Description = "测试",
                Instructions = "你是语音助手", TriggerMode = AgentTriggerMode.Mentioned,
            },
        ],
    };

    private AgentGateway Gateway(AgentOptions options, IServiceProvider services)
    {
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        return new AgentGateway(catalog, services, options, _store, NullLogger<AgentGateway>.Instance);
    }

    private static AgentInvocationContext Context(AttachmentInfo att) => new(
        GroupId: "g1", ThreadId: "t1", AgentId: "agent_a", AgentNickname: "语音助手",
        TriggerMessageId: "m1", TriggerUserId: "u1", Content: "（语音）", Mentions: [], MentionAll: false,
        Attachments: [att]);

    [Fact]
    public async Task Gateway_EnabledTranscription_InlinesTranscriptAsAttachmentBody()
    {
        var handler = new FakeHandler(_ => Ok("""{"text":"请帮我写一份周报"}"""));
        var options = GatewayOptions();
        options.Audio = new AudioOptions { Enabled = true, Endpoint = "https://asr.local/t" };
        var services = new ServiceCollection()
            .AddSingleton(new AudioTranscriptionService(options, _store, NullLogger<AudioTranscriptionService>.Instance, new HttpClient(handler)))
            .BuildServiceProvider();

        var gateway = Gateway(options, services);
        var att = SaveAudio();

        var (text, incomplete) = await gateway.BuildAttachmentsSectionAsync(Context(att), CancellationToken.None);
        var s = text.ToString();

        Assert.Contains("语音（已转写为文字，见下方）", s);
        Assert.Contains("语音转写", s);
        Assert.Contains("请帮我写一份周报", s);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, incomplete); // 转写文本完整注入，不算截断
    }

    [Fact]
    public async Task Gateway_DisabledTranscription_KeepsMetadataOnlyNote()
    {
        // 未注册 AudioTranscriptionService（默认关闭形态）→ 语音仍只携元数据，附件区如实提示内容不可理解。
        var options = GatewayOptions();
        var services = new ServiceCollection().BuildServiceProvider();

        var gateway = Gateway(options, services);
        var (text, _) = await gateway.BuildAttachmentsSectionAsync(Context(SaveAudio()), CancellationToken.None);
        var s = text.ToString();

        Assert.Contains("语音（未启用语音转写，内容无法被模型理解）", s);
        Assert.DoesNotContain("已转写为文字", s); // 未转写时不得出现「已转写」正文标注
    }

    [Fact]
    public async Task Gateway_TranscriptionCap_OnlyTranscribesConfiguredCount()
    {
        var handler = new FakeHandler(_ => Ok("""{"text":"第一段"}"""));
        var options = GatewayOptions();
        options.Audio = new AudioOptions { Enabled = true, Endpoint = "https://asr.local/t", MaxFilesPerMessage = 1 };
        var services = new ServiceCollection()
            .AddSingleton(new AudioTranscriptionService(options, _store, NullLogger<AudioTranscriptionService>.Instance, new HttpClient(handler)))
            .BuildServiceProvider();

        var gateway = Gateway(options, services);
        var ctx = new AgentInvocationContext(
            GroupId: "g1", ThreadId: "t1", AgentId: "agent_a", AgentNickname: "语音助手",
            TriggerMessageId: "m1", TriggerUserId: "u1", Content: "（两条语音）", Mentions: [], MentionAll: false,
            Attachments: [SaveAudio("a.mp3"), SaveAudio("b.mp3")]);

        var (text, _) = await gateway.BuildAttachmentsSectionAsync(ctx, CancellationToken.None);
        var s = text.ToString();

        Assert.Equal(1, handler.Calls);                          // 第二条超上限，不再转写
        Assert.Contains("语音（本次未转写，内容未注入）", s);       // 如实标注未转写，而非误报「未启用」
        Assert.Equal(1, CountOccurrences(s, "语音转写"));          // 只有第一条注入了转写正文
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
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
