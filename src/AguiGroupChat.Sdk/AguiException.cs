namespace AguiGroupChat.Sdk;

/// <summary>
/// SDK 调用失败时抛出的异常：携带 Hub 返回的协议错误码与状态码。
/// </summary>
public sealed class AguiException : Exception
{
    public AguiException(string code, string message, int? statusCode = null, string? rawBody = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        RawBody = rawBody;
    }

    /// <summary>协议错误码，见 <see cref="ErrorCodes"/>。</summary>
    public string Code { get; }

    /// <summary>HTTP 状态码（网络错误时为 null）。</summary>
    public int? StatusCode { get; }

    /// <summary>服务端返回的原始响应体。</summary>
    public string? RawBody { get; }
}
