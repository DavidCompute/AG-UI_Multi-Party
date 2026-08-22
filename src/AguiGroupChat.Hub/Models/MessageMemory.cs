namespace AguiGroupChat.Hub.Models;

/// <summary>记忆级别（分级治理）：普通 / 重要 / 关键。检索时同相似度下高级别优先。</summary>
public static class MemoryImportance
{
    public const int Normal = 0;
    public const int Important = 1;
    public const int Critical = 2;

    public const int Min = Normal;
    public const int Max = Critical;

    public static bool IsValid(int importance) => importance is >= Min and <= Max;
}

/// <summary>一条待写入语义记忆的群消息（由 GroupHub 在消息落库 / 结束时触发）。</summary>
public sealed record MessageMemoryEntry(
    string MessageId,
    string GroupId,
    string TopicId,
    string SenderId,
    string SenderType,
    string Content,
    long Timestamp);

/// <summary>记忆存储中的一条记录（含向量）。Importance=记忆级别（0 普通 / 1 重要 / 2 关键）；
/// ExpiresAt=自动遗忘的过期时间戳（毫秒，null=永不过期，检索与清理均按此过滤）。</summary>
public sealed record MessageMemoryRecord(
    string MessageId,
    string GroupId,
    string TopicId,
    string SenderId,
    string SenderType,
    string Content,
    float[] Embedding,
    long Timestamp,
    int Importance = MemoryImportance.Normal,
    long? ExpiresAt = null);

/// <summary>语义检索命中结果（Score 为余弦相似度 0..1）。</summary>
public sealed record MessageMemoryHit(
    string MessageId,
    string Content,
    string SenderId,
    long Timestamp,
    double Score,
    int Importance = MemoryImportance.Normal,
    string GroupId = "");

/// <summary>记忆条目（可视化列表用，无向量）。</summary>
public sealed record MessageMemoryItem(
    string MessageId,
    string GroupId,
    string TopicId,
    string SenderId,
    string SenderType,
    string Content,
    long Timestamp,
    int Importance,
    long? ExpiresAt);

/// <summary>群级记忆统计（记忆可视化用）。</summary>
public sealed record MessageMemoryGroupStat(
    string GroupId,
    int Count,
    long LastAt,
    int ExpiredCount);
