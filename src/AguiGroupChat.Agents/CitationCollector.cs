namespace AguiGroupChat.Agents;

/// <summary>
/// 本次回答的「引用来源」采集器（AsyncLocal）：记忆 / 知识库检索注入处（<see cref="MemoryContextProvider"/>）
/// 把真正注入的命中记录下来，运行结束时由网关回档到消息，供前端展示「📎 引用来源」并可点开查看片段。
/// 与 <see cref="UsageAccumulator"/> 同一作用域约定：最外层运行创建、嵌套沿用。
/// </summary>
internal sealed class CitationCollector
{
    /// <summary>单次回答最多收录的引用条数（防止长会话把消息撑大）。</summary>
    public const int MaxItems = 12;

    /// <summary>单条引用摘录的最大字符数。</summary>
    public const int MaxSnippetChars = 400;

    public static readonly AsyncLocal<CitationCollector?> Ambient = new();

    /// <param name="Kind">来源类型：<c>kb</c> 知识库 / <c>memory</c> 群记忆 / <c>person</c> 个人记忆。</param>
    /// <param name="Title">主标题（知识库名 / 发送者）。</param>
    /// <param name="Detail">副标题（文档名；记忆为空串）。</param>
    /// <param name="Score">相似度（0~1）。</param>
    /// <param name="Snippet">片段摘录。</param>
    public sealed record Item(string Kind, string Title, string Detail, double Score, string Snippet);

    public List<Item> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public void Add(string kind, string title, string detail, double score, string snippet)
    {
        if (Items.Count >= MaxItems) return;
        title ??= ""; detail ??= ""; snippet ??= "";
        // 去重：同一来源（类型 + 标题 + 副标题）只留首次命中
        foreach (var it in Items)
            if (it.Kind == kind && it.Title == title && it.Detail == detail) return;
        Items.Add(new Item(kind, title, detail, Math.Round(score, 4),
            snippet.Length > MaxSnippetChars ? snippet[..MaxSnippetChars] : snippet));
    }
}
