using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence.Relational;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Hub.Persistence;

/// <summary>
/// SQLite（及 MySQL）图谱记忆存储：实体（<c>agui_graph_entities</c>）+ 关系边
/// （<c>agui_graph_edges</c>）。实体向量存 BLOB 列，种子召回用「读取候选 + .NET 内存余弦」
/// （与 <see cref="SqliteVecMessageMemoryStore"/> 的 BLOB 降级语义一致，跨端行为统一）；
/// 图遍历用 SQLite / MySQL 的递归 CTE（<c>WITH RECURSIVE</c>）。
/// </summary>
public sealed class RelationalGraphMemoryStore : IGraphMemoryStore
{
    private readonly RelationalStore _db;
    private readonly int _dimensions;
    private readonly ILogger<RelationalGraphMemoryStore> _logger;
    private volatile bool _ready;

    public RelationalGraphMemoryStore(RelationalStore db, int dimensions, ILogger<RelationalGraphMemoryStore> logger)
    {
        _db = db;
        _dimensions = Math.Max(8, dimensions);
        _logger = logger;
    }

    public void EnsureSchema()
    {
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS agui_graph_entities (
                    entity_id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    type TEXT NOT NULL DEFAULT 'Concept',
                    group_id TEXT NOT NULL,
                    description TEXT,
                    embedding BLOB,
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
                    weight REAL NOT NULL DEFAULT 1.0,
                    timestamp BIGINT NOT NULL,
                    PRIMARY KEY (source_id, relation, target_id, group_id)
                );
                CREATE INDEX IF NOT EXISTS idx_graph_entities_group ON agui_graph_entities(group_id);
                CREATE INDEX IF NOT EXISTS idx_graph_edges_src ON agui_graph_edges(source_id);
                CREATE INDEX IF NOT EXISTS idx_graph_edges_dst ON agui_graph_edges(target_id);
                CREATE INDEX IF NOT EXISTS idx_graph_edges_group ON agui_graph_edges(group_id);
                """;
            cmd.ExecuteNonQuery();
            _ready = true;
            _logger.LogInformation("图谱记忆已启用（SQLite/MySQL，实体向量 BLOB + 内存余弦，维度 {Dimensions}）", _dimensions);
        }
        catch (Exception ex)
        {
            _ready = false;
            _logger.LogWarning(ex, "图谱记忆初始化失败（SQLite/MySQL 图存储），已禁用");
        }
    }

    public void UpsertEntity(GraphEntityRecord e)
    {
        if (!_ready || e.Embedding.Length == 0) return;
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agui_graph_entities (entity_id, name, type, group_id, description, embedding, timestamp, mention_count)
                VALUES (@id, @name, @type, @gid, @desc, @emb, @time, 1)
                ON CONFLICT (entity_id) DO UPDATE SET
                    name = excluded.name,
                    type = excluded.type,
                    description = COALESCE(excluded.description, agui_graph_entities.description),
                    embedding = excluded.embedding,
                    timestamp = excluded.timestamp,
                    mention_count = agui_graph_entities.mention_count + 1
                """;
            AddParam(cmd, "id", e.EntityId);
            AddParam(cmd, "name", e.Name);
            AddParam(cmd, "type", e.Type);
            AddParam(cmd, "gid", e.GroupId);
            AddParam(cmd, "desc", (object?)e.Description ?? DBNull.Value);
            AddParam(cmd, "emb", EncodeVector(e.Embedding));
            AddParam(cmd, "time", e.Timestamp);
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
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agui_graph_edges (source_id, relation, target_id, group_id, source_name, target_name, weight, timestamp)
                VALUES (@s, @r, @t, @gid, @sn, @tn, @w, @time)
                ON CONFLICT (source_id, relation, target_id, group_id) DO UPDATE SET
                    weight = agui_graph_edges.weight + excluded.weight,
                    timestamp = excluded.timestamp,
                    source_name = excluded.source_name,
                    target_name = excluded.target_name
                """;
            AddParam(cmd, "s", edge.SourceId);
            AddParam(cmd, "r", edge.Relation);
            AddParam(cmd, "t", edge.TargetId);
            AddParam(cmd, "gid", edge.GroupId);
            AddParam(cmd, "sn", edge.SourceName);
            AddParam(cmd, "tn", edge.TargetName);
            AddParam(cmd, "w", edge.Weight);
            AddParam(cmd, "time", edge.Timestamp);
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
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            var groupFilter = string.IsNullOrWhiteSpace(groupId) ? "" : " AND group_id = @gid";
            cmd.CommandText = $"SELECT entity_id, name, type, description, embedding FROM agui_graph_entities WHERE embedding IS NOT NULL{groupFilter}";
            if (!string.IsNullOrWhiteSpace(groupId)) AddParam(cmd, "gid", groupId);
            var candidates = new List<(GraphEntityHit Hit, float[] Emb)>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var emb = DecodeVector((byte[])reader.GetValue(4));
                    var score = Cosine(embedding, emb);
                    if (score < minScore) continue;
                    candidates.Add((new GraphEntityHit(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        score, reader.IsDBNull(3) ? null : reader.GetString(3), 0), emb));
                }
            }
            return candidates.OrderByDescending(c => c.Hit.Score).Take(topK).Select(c => c.Hit).ToList();
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
            // 迭代式 BFS：逐层查询 frontier 的出入边，向外扩展 n 跳（纯 C# 遍历，避免递归 CTE 的跨库方言差异/坑）。
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

            // 拉取实体命中 + 节点间出现的全部边
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
        using var conn = _db.Open();
        var inList = string.Join(",", Enumerable.Range(0, frontier.Count).Select(i => "@f" + i));
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                SELECT target_id FROM agui_graph_edges WHERE source_id IN ({inList})
                UNION
                SELECT source_id FROM agui_graph_edges WHERE target_id IN ({inList})";
            for (var i = 0; i < frontier.Count; i++) AddParam(cmd, "f" + i, frontier[i]);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!visited.Contains(id)) result.Add(id);
            }
        }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>拉取实体集合（含种子优先），保持 BFS 层级大致顺序（种子在前）。</summary>
    private List<GraphEntityHit> QueryEntities(HashSet<string> ids, string seed)
    {
        var list = new List<GraphEntityHit>();
        if (ids.Count == 0) return list;
        using var conn = _db.Open();
        var inList = string.Join(",", Enumerable.Range(0, ids.Count).Select(i => "@e" + i));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT entity_id, name, type, description FROM agui_graph_entities WHERE entity_id IN ({inList})";
        var arr = ids.ToArray();
        for (var i = 0; i < arr.Length; i++) AddParam(cmd, "e" + i, arr[i]);
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
        using var conn = _db.Open();
        var inList = string.Join(",", Enumerable.Range(0, ids.Count).Select(i => "@e" + i));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT source_id, source_name, relation, target_id, target_name, weight
            FROM agui_graph_edges
            WHERE source_id IN ({inList}) AND target_id IN ({inList})";
        var arr = ids.ToArray();
        for (var i = 0; i < arr.Length; i++) AddParam(cmd, "e" + i, arr[i]);
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
            using var conn = _db.Open();
            using var cmdE = conn.CreateCommand();
            cmdE.CommandText = "DELETE FROM agui_graph_entities WHERE group_id = @gid";
            AddParam(cmdE, "gid", groupId);
            cmdE.ExecuteNonQuery();
            using var cmdR = conn.CreateCommand();
            cmdR.CommandText = "DELETE FROM agui_graph_edges WHERE group_id = @gid";
            AddParam(cmdR, "gid", groupId);
            cmdR.ExecuteNonQuery();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "图谱群删除失败：{GroupId}", groupId); }
    }

    public void ClearAll()
    {
        if (!_ready) return;
        try
        {
            using var conn = _db.Open();
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
            using var conn = _db.Open();
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

    /// <summary>给 DbCommand 追加命名参数（兼容 SQLite / MySQL 的 @name 参数，避免 SqliteParameter 强耦合）。
    /// 显式补 @ 前缀：Microsoft.Data.Sqlite / MySqlConnector 按名字匹配 @name 占位符，缺前缀会导致误按位置绑定。</summary>
    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name.StartsWith('@') ? name : "@" + name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static byte[] EncodeVector(float[] v)
    {
        var bytes = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DecodeVector(byte[] bytes)
    {
        var v = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
        return v;
    }

    private static double Cosine(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < n; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
