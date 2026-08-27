using AguiGroupChat.Hub.Infra;
using StackExchange.Redis;
using System.Text.Json;

namespace AguiGroupChat.Hub.Persistence.Redis;

/// <summary>
/// Redis 共享存储的上下文封装：持有连接复用器并集中定义 key 命名空间与 JSON 序列化约定。
/// 供 6.2 Web 多副本横向扩展使用——多副本共享 Redis 时各 Store 读写同一批 key，
/// 使登录会话 / 群组 / 用户 / 任务 / 用量 / 扩展区跨进程一致。
/// 约定：所有 key 以 <c>agui:</c> 为前缀；消息正文 JSON 使用 <see cref="AguiJson"/>（与快照 / 协议一致）。
/// </summary>
public sealed class RedisContext : IDisposable
{
    private readonly ConnectionMultiplexer _redis;

    public RedisContext(string connectionString)
    {
        _redis = ConnectionMultiplexer.Connect(connectionString);
    }

    public IConnectionMultiplexer Mux => _redis;

    public IDatabase Db => _redis.GetDatabase();

    /// <summary>测试/工具用：清空当前库全部业务 key（<c>agui:*</c>），系统初始化用。</summary>
    public void FlushAguiKeys()
    {
        var server = _redis.GetServer(_redis.GetEndPoints()[0]);
        var keys = server.Keys(pattern: "agui:*").ToArray();
        if (keys.Length > 0) Db.KeyDelete(keys);
    }

    public static string GroupKey(string groupId) => $"agui:group:{groupId}";
    public static string MembersKey(string groupId) => $"agui:members:{groupId}";
    public static string TopicsKey(string groupId) => $"agui:topics:{groupId}";
    public static string MsgIndexKey(string groupId) => $"agui:msgs:{groupId}";
    public static string MsgKey(string groupId, string messageId) => $"agui:msg:{groupId}:{messageId}";
    public static string RecalledKey(string groupId, string messageId) => $"agui:recalled:{groupId}:{messageId}";
    public static string ReadKey(string memberId, string groupId, string topicId) => $"agui:reads:{groupId}:{topicId}:{memberId}";
    public static string LastMsgKey(string groupId) => $"agui:lastmsg:{groupId}";
    public static string UserKey(string userId) => $"agui:user:{userId}";
    public static string UserByNameKey => "agui:userby:name";
    public static string AgentRegistryKey => "agui:registry:agents";
    public static string UsageRowKey(string date, string agentId, string userId) => $"agui:usage:{date}:{agentId}:{userId}";
    public static string UsageDateIndexKey => "agui:usage:dates";
    public static string SectionKey(string name) => $"agui:section:{name}";

    public static string Serialize<T>(T value) => AguiJson.Serialize(value);
    public static T? Deserialize<T>(string json) => AguiJson.Deserialize<T>(json);
    public static T? Deserialize<T>(RedisValue value) => value.IsNullOrEmpty ? default : AguiJson.Deserialize<T>(value.ToString());

    public void Dispose() => _redis.Dispose();
}
