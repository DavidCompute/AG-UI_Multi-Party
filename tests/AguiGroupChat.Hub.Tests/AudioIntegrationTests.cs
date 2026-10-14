using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 多模态进阶 · 语音问答 / 语音回复的<b>真实端点集成测试</b>（默认跳过，仅当配置了环境变量时运行）。
///
/// <para>
/// 用真实 OpenAI 兼容服务（如 `docker compose --profile voice up -d` 起的内置 speaches）验证完整契约，
/// 与只需假 handler 的单元测试互补：
/// <list type="bullet">
///   <item><c>AGUI_ASR_TEST_ENDPOINT</c> + <c>AGUI_ASR_TEST_WAV</c>（一段含语音的音频文件路径）→ 真实转写；</item>
///   <item><c>AGUI_TTS_TEST_ENDPOINT</c> + <c>AGUI_TTS_TEST_MODEL</c> + <c>AGUI_TTS_TEST_VOICE</c> → 真实合成。</item>
/// </list>
/// 未配置对应环境变量时直接返回（视为通过），因此在常规 CI 上不会因缺少外部服务而失败。
/// </para>
/// </summary>
public sealed class AudioIntegrationTests : IDisposable
{
    private readonly string _dir;

    public AudioIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "agui-voice-it-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败不影响结论 */ }
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    [Fact]
    public async Task RealEndpoint_TranscribesFixtureAudio_ToText()
    {
        var endpoint = Env("AGUI_ASR_TEST_ENDPOINT");
        var wav = Env("AGUI_ASR_TEST_WAV");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(wav) || !File.Exists(wav)) return; // 未配置 → 跳过

        var store = new AttachmentStore(_dir);
        AttachmentInfo att;
        await using (var fs = File.OpenRead(wav))
            att = store.Save(Path.GetFileName(wav), "audio/wav", fs, fs.Length, maxBytes: 25 * 1024 * 1024);

        var options = new AgentOptions
        {
            Provider = "mock",
            Audio = new AudioOptions
            {
                Enabled = true,
                Endpoint = endpoint,
                Model = Env("AGUI_ASR_TEST_MODEL") ?? "Systran/faster-whisper-small",
                Language = Env("AGUI_ASR_TEST_LANGUAGE") ?? "zh",
                MaxBytes = 25 * 1024 * 1024,
                TimeoutSeconds = 300,
            },
        };
        var svc = new AudioTranscriptionService(options, store, NullLogger<AudioTranscriptionService>.Instance);
        Assert.True(svc.IsEnabled, "ASR 端点应可解析（检查 AGUI_ASR_TEST_ENDPOINT）");

        var text = await svc.TryTranscribeAsync(att);

        Assert.False(string.IsNullOrWhiteSpace(text), "真实转写应返回非空文本（检查模型是否已下载）");
        // 夹具音频说的是「…平台的语音问答测试…」；whisper-small 可能输出繁体（平/臺同形），故只钉稳定词。
        Assert.Contains("平台", text!);
    }

    [Fact]
    public async Task RealEndpoint_SynthesizesSpeech_ToAudioAttachment()
    {
        var endpoint = Env("AGUI_TTS_TEST_ENDPOINT");
        var model = Env("AGUI_TTS_TEST_MODEL");
        var voice = Env("AGUI_TTS_TEST_VOICE");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(voice)) return; // 未配置 → 跳过

        var store = new AttachmentStore(_dir);
        var options = new AgentOptions
        {
            Provider = "mock",
            Audio = new AudioOptions
            {
                SpeechEnabled = true,
                SpeechEndpoint = endpoint,
                SpeechModel = model,
                SpeechVoice = voice,
                SpeechFormat = "mp3",
                TimeoutSeconds = 300,
            },
        };
        var svc = new SpeechSynthesisService(options, store, NullLogger<SpeechSynthesisService>.Instance);
        Assert.True(svc.IsEnabled, "TTS 端点应可解析（检查 AGUI_TTS_TEST_ENDPOINT）");

        var att = await svc.TrySynthesizeAsync("Hello from KnowGath. This is a text to speech integration test.");

        Assert.NotNull(att);
        Assert.Equal("audio", att!.Kind);
        var saved = store.TryReadBytes(att.AttachmentId);
        Assert.NotNull(saved);
        Assert.True(saved!.Value.Bytes.Length > 1000, $"合成音频应非空（实际 {saved.Value.Bytes.Length} 字节）");
    }
}
