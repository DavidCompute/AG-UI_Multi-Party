namespace AguiGroupChat.Sdk;

/// <summary>
/// SDK 客户端配置：Hub 基址 + 会话令牌来源。
/// </summary>
public sealed class AguiClientOptions
{
    /// <summary>Hub 根地址，如 <c>http://localhost:5100</c> 或 <c>https://hub.example.com</c>。必填。</summary>
    public required Uri BaseUri { get; set; }

    /// <summary>
    /// 会话令牌提供者。登录 / 注册后 SDK 会自动设置；
    /// 第三方应用也可在外部登录后手动赋值（<see cref="AguiClient.Token"/>）。
    /// </summary>
    public Func<string?>? TokenProvider { get; set; }

    /// <summary>请求超时（默认 60 秒）。</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>实时通道传输方式（默认 WebSocket）。</summary>
    public RealtimeTransport Transport { get; set; } = RealtimeTransport.WebSocket;
}
