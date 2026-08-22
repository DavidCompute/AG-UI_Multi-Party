namespace AguiGroupChat.Hub.Infra;

/// <summary>
/// 协议错误（协议 §7 错误码扩展）。
/// HTTP 上行映射为对应状态码（400/403/404/409），WebSocket / SSE 下行映射为 RUN_ERROR 事件。
/// </summary>
public sealed class AguiProtocolException : Exception
{
    public string ErrorCode { get; }

    public AguiProtocolException(string errorCode, string message) : base(message)
        => ErrorCode = errorCode;
}
