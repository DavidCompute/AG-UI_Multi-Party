using System.Collections.Generic;

namespace AguiGroupChat.Hub.Models;

/// <summary>
/// 图谱 RAG（Graph Memory）领域模型：从群消息抽取的「实体 - 关系 - 实体」三元组，
/// 存储在实体表（含向量）与关系边表中；检索时先用语义召回种子实体，再做 n 跳图遍历，
/// 把命中子图文本化注入 prompt，补强向量记忆无法覆盖的关系型知识。
///
/// 与 <see cref="MessageMemory"/> 并列但独立：图存储仅依赖现有向量 embedding 能力，
/// 不引入额外图数据库（PostgreSQL/SQLite 都各自支持递归 CTE 图遍历）。
/// </summary>
public static class GraphMemoryScope
{
    /// <summary>关系类型：同一实体出/入边的通用连接（未抽到关系名时）。</summary>
    public const string RelatedTo = "related_to";
}

/// <summary>图存储中的一条实体记录（幂等键：EntityId）。Embedding 为该实体的语义向量，
/// 用于「从查询文本语义定位种子实体」。</summary>
public sealed record GraphEntityRecord(
    string EntityId,          // 规范化实体 id（小写哈希或规范化名）
    string Name,              // 显示名（如「DeepSeek」「需求评审」）
    string Type,              // 实体类型（Person / Organization / Product / Concept…，抽取器给出）
    string GroupId,           // 来源群（私密群隔离用；空 = 知识库级）
    string? Description,     // 实体描述 / 上下文摘要（可空）
    float[] Embedding,       // 语义向量（用于种子召回）
    long Timestamp,
    int MentionCount = 1);   // 被提及次数（权重）

/// <summary>图存储中的一条关系边（幂等键：SourceId+Relation+TargetId+GroupId）。</summary>
public sealed record GraphEdgeRecord(
    string SourceId,
    string Relation,          // 关系名（如「负责」「推荐」；未识别用 related_to）
    string TargetId,
    string GroupId,
    string SourceName,
    string TargetName,
    double Weight = 1.0,      // 出现的权重 / 置信度
    long Timestamp = 0);

/// <summary>图谱检索命中的子图节点（供注入文本化）。</summary>
public sealed record GraphEntityHit(
    string EntityId,
    string Name,
    string Type,
    double Score,
    string? Description,
    int Hop);

/// <summary>图谱检索命中的关系边（供注入文本化）。</summary>
public sealed record GraphEdgeHit(
    string SourceId,
    string SourceName,
    string Relation,
    string TargetId,
    string TargetName,
    double Weight,
    int Hop);

/// <summary>一次图谱检索的子图结果。</summary>
public sealed record GraphSubgraph(
    IReadOnlyList<GraphEntityHit> Entities,
    IReadOnlyList<GraphEdgeHit> Edges)
{
    public bool IsEmpty => Entities.Count == 0;
}

/// <summary>图谱统计（管理 / 记忆可视化用）。</summary>
public sealed record GraphStats(int EntityCount, int EdgeCount, long LastAt);
