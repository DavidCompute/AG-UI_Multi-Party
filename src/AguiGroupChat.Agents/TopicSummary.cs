using System.Collections.Concurrent;

namespace AguiGroupChat.Agents;

/// <summary>
/// 一个话题的<b>滚动小结</b>：把“较早对话”压缩成紧凑摘要随话题持续更新，
/// 供滑动窗口截掉早期内容后仍能“记得之前聊到哪”（长话题接续记忆）。
/// 经 <see cref="TopicSummaryStore"/> 持久化到扩展区（agui_sections / JSON 快照）。
/// </summary>
public sealed class TopicSummaryRecord
{
    public required string GroupId { get; init; }
    public required string TopicId { get; init; }
    /// <summary>小结正文（模型生成 / mock 确定性模板）。</summary>
    public string Summary { get; set; } = "";
    /// <summary>已纳入小结的最新一条消息 ID（游标）：下次从它之后继续累积新消息。</summary>
    public string? WatermarkMessageId { get; set; }
    /// <summary>最近一次生成时间戳（毫秒，UTC）。</summary>
    public long UpdatedAtMs { get; set; }
    /// <summary>累计纳入小结的消息条数（展示/调试用）。</summary>
    public int MessageCount { get; set; }
}

/// <summary>
/// 话题滚动小结服务：内存持有 + 扩展区持久化；同话题同时只允许一个生成任务（并发防抖）。
/// key = "groupId|topicId"。
/// </summary>
public sealed class TopicSummaryStore
{
    private readonly ConcurrentDictionary<string, TopicSummaryRecord> _records = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _generating = new(StringComparer.Ordinal);

    public static string Key(string groupId, string topicId) => groupId + "|" + (string.IsNullOrEmpty(topicId) ? "main" : topicId);

    public TopicSummaryRecord? Get(string groupId, string topicId)
        => _records.TryGetValue(Key(groupId, topicId), out var r) ? r : null;

    public void Put(TopicSummaryRecord record)
        => _records[Key(record.GroupId, record.TopicId)] = record;

    public void Remove(string groupId, string topicId)
    {
        _records.TryRemove(Key(groupId, topicId), out _);
        _generating.TryRemove(Key(groupId, topicId), out _);
    }

    /// <summary>占用该话题的生成名额（成功返回 true 表示可以开始生成；false = 已在生成中）。</summary>
    public bool TryBeginGenerate(string groupId, string topicId) => _generating.TryAdd(Key(groupId, topicId), 1);

    public void EndGenerate(string groupId, string topicId) => _generating.TryRemove(Key(groupId, topicId), out _);

    /// <summary>快照（持久化）。</summary>
    public IReadOnlyList<TopicSummaryRecord> Snapshot() => _records.Values.ToList();

    /// <summary>恢复（清空后灌入）。</summary>
    public void Restore(IEnumerable<TopicSummaryRecord> records)
    {
        _records.Clear();
        foreach (var r in records)
        {
            if (string.IsNullOrWhiteSpace(r.GroupId) || string.IsNullOrEmpty(r.Summary)) continue;
            _records[Key(r.GroupId, r.TopicId)] = r;
        }
    }
}
