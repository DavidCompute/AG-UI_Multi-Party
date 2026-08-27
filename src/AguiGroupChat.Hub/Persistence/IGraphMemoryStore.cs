using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Hub.Persistence;

/// <summary>
/// 图谱记忆（Graph RAG）的存储抽象：PostgreSQL + pgvector 实现见 <see cref="Postgres.PgGraphMemoryStore"/>，
/// SQLite（及 MySQL）共用关系实现见 <see cref="Relational.RelationalGraphMemoryStore"/>。
/// 存储完整实体-关系图 + 实体向量，检索时「向量召回种子实体 → n 跳图遍历」。
/// </summary>
public interface IGraphMemoryStore
{
    /// <summary>启动时建表 + 建索引（幂等；pgvector / 递归 CTE 不可用时内部降级为不可用，不抛异常）。</summary>
    void EnsureSchema();

    /// <summary>写入 / 合并一条实体（按 EntityId 幂等；存在则更新描述 / 提及次数 / 时间，权重累加）。</summary>
    void UpsertEntity(GraphEntityRecord entity);

    /// <summary>写入 / 合并一条关系边（按 Source+Relation+Target+Group 幂等；存在则累加权重）。</summary>
    void UpsertEdge(GraphEdgeRecord edge);

    /// <summary>按余弦相似度召回种子实体（可限定群，私密群隔离由调用方保证）。</summary>
    IReadOnlyList<GraphEntityHit> SearchEntities(float[] embedding, int topK, double minScore, string? groupId);

    /// <summary>从种子实体做 n 跳图遍历，返回可达子图（实体 + 边，去重 + 上限保护）。</summary>
    GraphSubgraph ExpandSubgraph(string seedEntityId, int hops, int maxNodes);

    /// <summary>解散群时删除该群全部实体与边（物理删除）。</summary>
    void RemoveGroup(string groupId);

    /// <summary>清空全部图谱（系统初始化用）。</summary>
    void ClearAll();

    /// <summary>图谱统计（实体数 / 边数 / 最近写入时间）。</summary>
    GraphStats Stats();
}
