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
}
