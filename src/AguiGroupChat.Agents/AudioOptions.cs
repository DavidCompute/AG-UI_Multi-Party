namespace AguiGroupChat.Agents;

/// <summary>
/// 语音问答（ASR）配置：把用户发来的<b>语音附件</b>转写为文字后注入模型上下文，
/// 使数字员工能「听懂」语音消息（此前音频仅携带元数据供前端播放、完全不进上下文）。
///
/// <para>
/// 默认<b>关闭</b>：未显式开启时行为与旧版完全一致（语音仅元数据）。开启且能解析出端点才生效——
/// 端点优先取 <see cref="Endpoint"/>，留空则由 <c>Agents:Endpoint</c> 派生
/// <c>{base}/v1/audio/transcriptions</c>（与 OpenAI 音频转写接口一致，便于对接 whisper 类服务 / 兼容端点）。
/// </para>
/// </summary>
public sealed class AudioOptions
{
    /// <summary>是否启用语音转写（默认关闭；开启且端点可解析才生效）。</summary>
    public bool Enabled { get; set; }

    /// <summary>转写端点完整 URL；留空时由 <c>Agents:Endpoint</c> 派生 <c>{base}/v1/audio/transcriptions</c>。</summary>
    public string? Endpoint { get; set; }

    /// <summary>转写专用 API Key；留空回退 <c>Agents:ApiKey</c>。</summary>
    public string? ApiKey { get; set; }

    /// <summary>转写模型（默认 <c>whisper-1</c>）。</summary>
    public string Model { get; set; } = "whisper-1";

    /// <summary>语言提示（ISO-639-1，如 <c>zh</c>）；留空自动识别。</summary>
    public string? Language { get; set; }

    /// <summary>单文件字节上限（默认 25MB，与多数托管转写服务一致）。</summary>
    public long MaxBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>单次请求超时（秒，默认 60）。</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>单条消息最多转写的语音文件数（默认 3，控成本）。</summary>
    public int MaxFilesPerMessage { get; set; } = 3;

    // ── 语音回复（TTS）：把数字员工的文字回复合成为音频附件挂到消息上 ──

    /// <summary>是否启用语音回复（TTS）：回复文本合成为音频附件。默认关闭。</summary>
    public bool SpeechEnabled { get; set; }

    /// <summary>语音合成端点完整 URL；留空时由 <c>Agents:Endpoint</c> 派生 <c>{base}/v1/audio/speech</c>。</summary>
    public string? SpeechEndpoint { get; set; }

    /// <summary>语音合成模型（HF 仓库 id / 服务商模型名，如 <c>speaches-ai/piper-en_US-lessac-medium</c>）。</summary>
    public string? SpeechModel { get; set; }

    /// <summary>音色名（随模型而定，如 <c>zf_xiaoxiao</c> / <c>huayan</c> / <c>af_heart</c>）。</summary>
    public string? SpeechVoice { get; set; }

    /// <summary>输出格式（<c>mp3</c> / <c>wav</c> / <c>opus</c>；默认 mp3）。</summary>
    public string SpeechFormat { get; set; } = "mp3";

    /// <summary>合成正文长度上限（字符，默认 600；过长只取前段，控成本与时长：CPU 上约 0.2s/字）。</summary>
    public int SpeechMaxChars { get; set; } = 600;

    /// <summary>语速（1.0 = 原速；Kokoro / OpenAI 兼容服务的 <c>speed</c> 参数，建议 0.5–2.0）。</summary>
    public double SpeechSpeed { get; set; } = 1.0;

    /// <summary>语音合成请求超时（秒，默认 300）。CPU 上合成较慢（实测约 0.2s/字），需比转写宽松得多。</summary>
    public int SpeechTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// 仅当触发消息带<b>语音附件</b>时才合成（默认 <c>true</c> = 只在「语音进→语音出」时出声）；
    /// 设 false 则开启语音回复后<b>每条回复都带音频</b>。
    /// 默认 true 的理由：Kokoro 在 CPU 上合成很慢（约 0.2s/字），每条文字回复都合成既重又吵；
    /// 非语音回合仍可由用户按需点前端 🔊 朗读（Web Speech，无附件）。
    /// </summary>
    public bool SpeechOnlyForVoice { get; set; } = true;
}
