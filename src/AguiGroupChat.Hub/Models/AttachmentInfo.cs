namespace AguiGroupChat.Hub.Models;

/// <summary>
/// 消息附件（Hub 扩展字段，协议标准之外）：上传后随 GROUP_MESSAGE_SEND / TEXT_MESSAGE_START 传递。
/// 智能体收到带附件消息时：text / document 类附件由服务端读取文本注入上下文，
/// image / audio / binary 类仅携带元数据（文件名 / 类型 / URL）供模型感知。
/// </summary>
public sealed class AttachmentInfo
{
    /// <summary>附件 ID（att_xxx），与上传文件目录一一对应。</summary>
    public required string AttachmentId { get; init; }

    /// <summary>原始文件名（已消毒，供前端展示与下载）。</summary>
    public required string Name { get; init; }

    /// <summary>MIME 类型。</summary>
    public required string ContentType { get; init; }

    /// <summary>字节大小。</summary>
    public required long Size { get; init; }

    /// <summary>下载地址（GET /ag-ui/files/{id}/{name}）。</summary>
    public required string Url { get; init; }

    /// <summary>附件类别：image / audio / text / document / binary（前端渲染样式与智能体上下文注入策略）。</summary>
    public required string Kind { get; init; }
}
