using System.Globalization;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Hub.Persistence.Postgres;

/// <summary>
/// PostgreSQL + pgvector 图谱记忆存储：实体（<c>agui_graph_entities</c>，含 vector 向量列）+ 关系边
/// （<c>agui_graph_edges</c>）。检索分两步——① 用查询向量按余弦相似度召回「种子」实体；
/// ② 用递归 CTE 从种子做 n 跳双向图遍历，取回可达子图（实体 + 边）供注入。
/// pgvector 扩展不可用时内部降级为不可用，不影响群聊主流程。
/// </summary>
public sealed class PgGraphMemoryStore : IGraphMemoryStore
{
    private readonly PostgresStore _pg;
    private readonly int _dimensions;
    private readonly ILogger<PgGraphMemoryStore> _logger;
    private volatile bool _ready;

    public PgGraphMemoryStore(PostgresStore pg, int dimensions, ILogger<PgGraphMemoryStore> logger)
    {
        _pg = pg;
        _dimensions = Math.Max(8, dimensions);
        _logger = logger;
    }

    public void EnsureSchema()
    {
        try
        {
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $$"""
                CREATE EXTENSION IF NOT EXISTS vector;
                CREATE TABLE IF NOT EXISTS agui_graph_entities (
                    entity_id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    type TEXT NOT NULL DEFAULT 'Concept',
                    group_id TEXT NOT NULL,
                    description TEXT,
                    embedding vector({{_dimensions}}),
                    timestamp BIGINT NOT NULL,
                    mention_count INTEGER NOT NULL DEFAULT 1
                );
                CREATE TABLE IF NOT EXISTS agui_graph_edges (
                    source_id TEXT NOT NULL,
                    relation TEXT NOT NULL,
                    target_id TEXT NOT NULL,
                    group_id TEXT NOT NULL,
                    source_name TEXT NOT NULL,
                    target_name TEXT NOT NULL,
                    weight DOUBLE PRECISION NOT NULL DEFAULT 1.0,
                    timestamp BIGINT NOT NULL,
                    PRIMARY KEY (source_id, relation, target_id, group_id)
                );
                CREATE INDEX IF NOT EXISTS idx_graph_entities_hnsw ON agui_graph_entities USING hnsw (embedding vector_cosine_ops);
                CREATE INDEX IF NOT EXISTS idx_graph_entities_group ON agui_graph_entities(group_id);
                CREATE INDEX IF NOT EXISTS idx_graph_edges_src ON agui_graph_edges(source_id);
                CREATE INDEX IF NOT EXISTS idx_graph_edges_dst ON agui_graph_edges(target_id);
                CREATE INDEX IF NOT EXISTS idx_graph_edges_group ON agui_graph_edges(group_id);
                """;
            cmd.ExecuteNonQuery();
            _ready = true;
            _logger.LogInformation("图谱记忆已启用（pgvector，实体向量维度 {Dimensions}）", _dimensions);
        }
        catch (Exception ex)
        {
            _ready = false;
            _logger.LogWarning(ex, "图谱记忆初始化失败（需 PostgreSQL + pgvector 扩展），已禁用");
        }
    }

    public void UpsertEntity(GraphEntityRecord e)
    {
        if (!_ready || e.Embedding.Length == 0) return;
        try
        {
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agui_graph_entities (entity_id, name, type, group_id, description, embedding, timestamp, mention_count)
                VALUES (@id, @name, @type, @gid, @desc, @emb::vector, @time, 1)
                ON CONFLICT (entity_id) DO UPDATE SET
                    name = EXCLUDED.name,
                    type = EXCLUDED.type,
                    description = COALESCE(EXCLUDED.description, agui_graph_entities.description),
                    embedding = EXCLUDED.embedding,
                    timestamp = EXCLUDED.timestamp,
                    mention_count = agui_graph_entities.mention_count + 1
                """;
            cmd.Parameters.AddWithValue("id", e.EntityId);
            cmd.Parameters.AddWithValue("name", e.Name);
            cmd.Parameters.AddWithValue("type", e.Type);
            cmd.Parameters.AddWithValue("gid", e.GroupId);
            cmd.Parameters.AddWithValue("desc", (object?)e.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("emb", ToVectorText(e.Embedding));
            cmd.Parameters.AddWithValue("time", e.Timestamp);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "图谱实体写入失败：{Entity}", e.Name);
        }
    }

    public void UpsertEdge(GraphEdgeRecord edge)
    {
        if (!_ready) return;
        try
        {
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agui_graph_edges (source_id, relation, target_id, group_id, source_name, target_name, weight, timestamp)
                VALUES (@s, @r, @t, @gid, @sn, @tn, @w, @time)
                ON CONFLICT (source_id, relation, target_id, group_id) DO UPDATE SET
                    weight = agui_graph_edges.weight + EXCLUDED.weight,
                    timestamp = EXCLUDED.timestamp,
                    source_name = EXCLUDED.source_name,
                    target_name = EXCLUDED.target_name
                """;
            cmd.Parameters.AddWithValue("s", edge.SourceId);
            cmd.Parameters.AddWithValue("r", edge.Relation);
            cmd.Parameters.AddWithValue("t", edge.TargetId);
            cmd.Parameters.AddWithValue("gid", edge.GroupId);
            cmd.Parameters.AddWithValue("sn", edge.SourceName);
            cmd.Parameters.AddWithValue("tn", edge.TargetName);
            cmd.Parameters.AddWithValue("w", edge.Weight);
            cmd.Parameters.AddWithValue("time", edge.Timestamp);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "图谱关系写入失败：{Src} {Rel} {Dst}", edge.SourceId, edge.Relation, edge.TargetId);
        }
    }

    public IReadOnlyList<GraphEntityHit> SearchEntities(float[] embedding, int topK, double minScore, string? groupId)
    {
        if (!_ready || embedding.Length == 0) return [];
        topK = Math.Clamp(topK, 1, 100);
        try
        {
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            var groupFilter = string.IsNullOrWhiteSpace(groupId) ? "" : " AND group_id = @gid";
            cmd.CommandText = $"""
                SELECT entity_id, name, type, description, 1 - (embedding <=> @q::vector) AS score
                FROM agui_graph_entities
                WHERE embedding IS NOT NULL AND 1 - (embedding <=> @q::vector) >= @minScore{groupFilter}
                ORDER BY embedding <=> @q::vector
                LIMIT @k
                """;
            cmd.Parameters.AddWithValue("q", ToVectorText(embedding));
            cmd.Parameters.AddWithValue("minScore", minScore);
            cmd.Parameters.AddWithValue("k", topK);
            if (!string.IsNullOrWhiteSpace(groupId)) cmd.Parameters.AddWithValue("gid", groupId);
            var list = new List<GraphEntityHit>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new GraphEntityHit(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetDouble(4), reader.IsDBNull(3) ? null : reader.GetString(3), 0));
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "图谱实体召回失败");
            return [];
        }
    }

    public GraphSubgraph ExpandSubgraph(string seedEntityId, int hops, int maxNodes)
    {
        if (!_ready) return new GraphSubgraph([], []);
        hops = Math.Clamp(hops, 1, 4);
        maxNodes = Math.Clamp(maxNodes, 1, 200);
        try
        {
            // 迭代式 BFS：逐层查询 frontier 的出入边，向外扩展 n 跳（纯 C# 遍历，与 SQLite 实现一致，避免递归 CTE 跨库差异）。
            var visited = new HashSet<string>(StringComparer.Ordinal) { seedEntityId };
            var frontier = new List<string> { seedEntityId };
            for (var hop = 1; hop <= hops && frontier.Count > 0 && visited.Count < maxNodes; hop++)
            {
                var neighbors = QueryNeighbors(frontier, visited);
                if (neighbors.Count == 0) break;
                var next = new List<string>();
                foreach (var n in neighbors)
                {
                    if (visited.Count >= maxNodes) break;
                    if (visited.Add(n)) next.Add(n);
                }
                frontier = next;
            }

            var entities = QueryEntities(visited, seedEntityId);
            var edges = QueryEdgesWithin(visited);
            return new GraphSubgraph(entities, edges);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "图谱子图遍历失败：{Seed}", seedEntityId);
            return new GraphSubgraph([], []);
        }
    }

    /// <summary>查询 frontier 集合的出入边邻居（去重、排除已访问）。</summary>
    private List<string> QueryNeighbors(List<string> frontier, HashSet<string> visited)
    {
        var result = new List<string>();
        if (frontier.Count == 0) return result;
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT target_id FROM agui_graph_edges WHERE source_id = ANY(@f)
            UNION
            SELECT source_id FROM agui_graph_edges WHERE target_id = ANY(@f)
            """;
        cmd.Parameters.AddWithValue("f", frontier.ToArray());
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!visited.Contains(id)) result.Add(id);
        }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>拉取实体集合（种子优先）。</summary>
    private List<GraphEntityHit> QueryEntities(HashSet<string> ids, string seed)
    {
        var list = new List<GraphEntityHit>();
        if (ids.Count == 0) return list;
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entity_id, name, type, description FROM agui_graph_entities WHERE entity_id = ANY(@ids)";
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new GraphEntityHit(
                EntityId: reader.GetString(0), Name: reader.GetString(1), Type: reader.GetString(2), Score: 0,
                Description: reader.IsDBNull(3) ? null : reader.GetString(3), Hop: 0));
        }
        return list.OrderBy(e => e.EntityId != seed).ToList(); // 种子排最前
    }

    /// <summary>返回节点集合内部出现的全部边。</summary>
    private List<GraphEdgeHit> QueryEdgesWithin(HashSet<string> ids)
    {
        var edges = new List<GraphEdgeHit>();
        if (ids.Count == 0) return edges;
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT source_id, source_name, relation, target_id, target_name, weight
            FROM agui_graph_edges
            WHERE source_id = ANY(@ids) AND target_id = ANY(@ids)
            """;
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            edges.Add(new GraphEdgeHit(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetDouble(5), 0));
        }
        return edges;
    }

    public void RemoveGroup(string groupId)
    {
        if (!_ready) return;
        try
        {
            using var conn = _pg.Open();
            using var cmdE = conn.CreateCommand();
            cmdE.CommandText = "DELETE FROM agui_graph_entities WHERE group_id = @gid";
            cmdE.Parameters.AddWithValue("gid", groupId);
            cmdE.ExecuteNonQuery();
            using var cmdR = conn.CreateCommand();
            cmdR.CommandText = "DELETE FROM agui_graph_edges WHERE group_id = @gid";
            cmdR.Parameters.AddWithValue("gid", groupId);
            cmdR.ExecuteNonQuery();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "图谱群删除失败：{GroupId}", groupId); }
    }

    public void ClearAll()
    {
        if (!_ready) return;
        try
        {
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM agui_graph_edges; DELETE FROM agui_graph_entities;";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "图谱清空失败"); }
    }

    public GraphStats Stats()
    {
        if (!_ready) return new GraphStats(0, 0, 0);
        try
        {
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT (SELECT COUNT(*) FROM agui_graph_entities),
                       (SELECT COUNT(*) FROM agui_graph_edges),
                       COALESCE((SELECT MAX(timestamp) FROM agui_graph_entities), 0)
                """;
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return new GraphStats((int)reader.GetInt64(0), (int)reader.GetInt64(1), reader.GetInt64(2));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "图谱统计失败"); return new GraphStats(0, 0, 0); }
    }

    /// <summary>float[] → pgvector 文本格式「[0.1,0.2,…]」（G9 保证往返精度）。</summary>
    private static string ToVectorText(float[] v)
        => "[" + string.Join(",", v.Select(f => f.ToString("G9", CultureInfo.InvariantCulture))) + "]";
}
