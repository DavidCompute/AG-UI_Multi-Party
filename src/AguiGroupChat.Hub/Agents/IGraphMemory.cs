using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Hub.Agents;

/// <summary>
/// 图谱记忆（Graph RAG）：从群消息抽取「实体-关系-实体」构建知识图谱，回复前按语义召回
/// 种子实体 + n 跳图遍历，把命中子图注入上下文（补强向量记忆无法覆盖的关系型知识）。
/// 未启用时 GroupHub / MemoryContextProvider 收到 null，图谱功能对既有流程完全透明。
/// 实现见 <c>AguiGroupChat.Agents.GraphMemory</c>。
/// </summary>
public interface IGraphMemory
{
    /// <summary>写入一条消息到图谱（fire-and-forget：内部异步抽取实体/关系 + 写图，失败不影响主流程）。</summary>
    void Remember(GraphMessageEntry entry);

    /// <summary>解散群时删除该群全部实体与边（物理删除）。</summary>
    void RemoveGroup(string groupId);

    /// <summary>清空全部图谱（系统初始化用）。</summary>
    void ClearAll();

    /// <summary>按查询语义召回种子实体并做 n 跳图遍历，返回注入用子图（端点不可用 / 未启用时返回空）。</summary>
    Task<GraphSubgraph> SearchAsync(string groupId, string query, CancellationToken ct = default);

    /// <summary>图谱统计（管理 / 可视化用）。</summary>
    GraphStats Stats();
}

/// <summary>图谱写入的输入：一条待提取实体/关系的消息（与 <see cref="MessageMemoryEntry"/> 并列，但图只关心内容与来源群）。</summary>
public sealed record GraphMessageEntry(
    string GroupId,
    string SenderId,
    string Content,
    long Timestamp);
