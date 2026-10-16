namespace AguiGroupChat.Agents;

/// <summary>
/// 记忆写入侧的「重复编码」去重缓存（ROADMAP §二「记忆写入去重」）。
///
/// <para>
/// 写入路径在调用 embedding 之前，先用<b>实际入库文本</b>（已由 <see cref="AgentMessageMemory"/> 净化 +
/// 按 <see cref="MemoryOptions.MaxWriteChars"/> 截断后的那一份）查本缓存；
/// 命中即直接复用向量、跳过 embedding 调用。相同文本在同一 embedding 模型下向量恒等，
/// 因此复用在语义上<b>完全等价</b>——召回结果一字不差，只是省去重复的推理开销
/// （CPU 上 bge-m3 单条最多可达数秒，占着后台池槽位）。
/// </para>
///
/// <para>
/// 有界 LRU：容量 <c>MemoryOptions.WriteEmbeddingCacheSize</c>（0 = 关闭，调用方不构造本类），
/// 无 TTL——同进程内模型不变，已算出的向量不会失效；容量满时淘汰最久未使用者以限制内存
/// （每条约占「维度 × 4 字节」，bge-m3 1024 维 ≈ 4KB，512 条约 2MB）。
/// </para>
///
/// <para>
/// 线程安全：写入队列为单消费者，但导入 / 后台任务可能并发访问，故内部加锁；
/// 锁内仅做字典 / 链表操作，不含任何 I/O。
/// </para>
/// </summary>
public sealed class MemoryEmbeddingCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, float[]> _map;
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes;
    private readonly object _lock = new();

    public MemoryEmbeddingCache(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        _map = new Dictionary<string, float[]>(_capacity, StringComparer.Ordinal);
        _nodes = new Dictionary<string, LinkedListNode<string>>(_capacity, StringComparer.Ordinal);
    }

    /// <summary>容量上限（条目数）。</summary>
    public int Capacity => _capacity;

    /// <summary>当前条目数（诊断 / 测试用）。</summary>
    public int Count { get { lock (_lock) return _map.Count; } }

    /// <summary>取已缓存的向量；未命中返回 null。命中时把该键提升为最近使用。</summary>
    public float[]? TryGet(string key)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(key, out var vector)) return null;
            if (_nodes.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
            }
            return vector;
        }
    }

    /// <summary>写入 / 更新缓存（已存在则替换并提升为最近使用）；超容量时淘汰最久未使用者。</summary>
    public void Set(string key, float[] vector)
    {
        if (vector.Length == 0) return;
        lock (_lock)
        {
            if (_nodes.TryGetValue(key, out var existing))
            {
                _map[key] = vector;
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }
            var node = _lru.AddFirst(key);
            _nodes[key] = node;
            _map[key] = vector;
            while (_map.Count > _capacity && _lru.Last is { } oldest)
            {
                _lru.RemoveLast();
                _nodes.Remove(oldest.Value);
                _map.Remove(oldest.Value);
            }
        }
    }
}
