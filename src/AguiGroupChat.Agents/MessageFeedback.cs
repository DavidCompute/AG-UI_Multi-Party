using System.Collections.Concurrent;

namespace AguiGroupChat.Agents;

/// <summary>一条消息反馈（数字员工回复的 👍 / 👎）。key = messageId|userId（一人一条，重复提交覆盖）。</summary>
public sealed class MessageFeedbackEntry
{
    public required string MessageId { get; init; }
    public required string GroupId { get; init; }
    public string TopicId { get; init; } = "main";
    /// <summary>被反馈的数字员工。</summary>
    public required string AgentId { get; init; }
    /// <summary>反馈者。</summary>
    public required string UserId { get; init; }
    /// <summary>1 = 👍；-1 = 👎。</summary>
    public int Value { get; set; }
    /// <summary>👎 时的快速标签（太啰嗦 / 太简短 / 答非所问 / 不专业 / 不友好 / 未引用来源…）。</summary>
    public string[] Tags { get; set; } = [];
    /// <summary>被反馈消息的正文摘录（≤120 字，供偏好参考）。</summary>
    public string Snippet { get; set; } = "";
    public long CreatedAtMs { get; set; }
}

/// <summary>
/// 消息反馈服务：内存持有 + 扩展区持久化（agui_sections / JSON 快照）。
/// 聚合口径在网关注入侧：按“反馈者 + 最近负面反馈（同群或同数字员工）”生成偏好提示。
/// </summary>
public sealed class MessageFeedbackStore
{
    private readonly ConcurrentDictionary<string, MessageFeedbackEntry> _entries = new(StringComparer.Ordinal);

    public static string Key(string messageId, string userId) => messageId + "|" + userId;

    public void Put(MessageFeedbackEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.MessageId) || string.IsNullOrWhiteSpace(entry.UserId)) return;
        _entries[Key(entry.MessageId, entry.UserId)] = entry;
    }

    /// <summary>某用户的反馈（按时间倒序）。</summary>
    public IReadOnlyList<MessageFeedbackEntry> ByUser(string userId)
        => _entries.Values.Where(e => e.UserId == userId).OrderByDescending(e => e.CreatedAtMs).ToList();

    /// <summary>某条消息的某用户反馈。</summary>
    public MessageFeedbackEntry? Get(string messageId, string userId)
        => _entries.TryGetValue(Key(messageId, userId), out var e) ? e : null;

    public IReadOnlyList<MessageFeedbackEntry> Snapshot() => _entries.Values.ToList();

    public void Restore(IEnumerable<MessageFeedbackEntry> entries)
    {
        _entries.Clear();
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.MessageId) || string.IsNullOrWhiteSpace(e.UserId)) continue;
            _entries[Key(e.MessageId, e.UserId)] = e;
        }
    }
}
