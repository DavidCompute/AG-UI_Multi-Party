using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using StackExchange.Redis;
using System.Text.Json;

namespace AguiGroupChat.Hub.Persistence.Redis;

/// <summary>Redis 用户账号存储（6.2 多副本共享）：账号 JSON 存 <c>agui:user:{userId}</c>，用户名索引存 hash。</summary>
public sealed class RedisUserStore : IUserStore
{
    private readonly RedisContext _ctx;
    public RedisUserStore(RedisContext ctx) => _ctx = ctx;

    public bool AddUser(UserAccount user)
    {
        var db = _ctx.Db;
        var key = RedisContext.UserKey(user.UserId);
        if (db.StringSet(key, RedisContext.Serialize(user), when: When.NotExists) == false) return false;
        var once = db.HashSet(RedisContext.UserByNameKey, user.Username, user.UserId, When.NotExists);
        if (!once)
        {
            // 用户名冲突：回滚已写入的用户
            db.KeyDelete(key);
            return false;
        }
        return true;
    }

    public UserAccount? GetUserById(string userId)
        => RedisContext.Deserialize<UserAccount>(_ctx.Db.StringGet(RedisContext.UserKey(userId)));

    public UserAccount? GetUserByUsername(string username)
    {
        var userId = _ctx.Db.HashGet(RedisContext.UserByNameKey, username);
        return userId.IsNullOrEmpty ? null : GetUserById(userId.ToString());
    }

    public bool UpdateUser(UserAccount user)
        => _ctx.Db.StringSet(RedisContext.UserKey(user.UserId), RedisContext.Serialize(user));

    public IReadOnlyList<UserAccount> ListUsers()
    {
        var userIds = _ctx.Db.HashValues(RedisContext.UserByNameKey)
            .Where(v => !v.IsNullOrEmpty).Select(v => v.ToString()).Distinct().ToArray();
        var values = _ctx.Db.StringGet(userIds.Select(u => (RedisKey)RedisContext.UserKey(u)).ToArray());
        return values.Where(v => !v.IsNullOrEmpty).Select(v => RedisContext.Deserialize<UserAccount>(v!)!).ToList();
    }

    public void ClearAll()
    {
        // 先枚举用户 id 集合、再删除：若先删 UserByNameKey，后面就无法从它发现哪些 user:* key 需删除（泄漏用户 key）。
        var db = _ctx.Db;
        var userIds = db.HashValues(RedisContext.UserByNameKey)
            .Where(v => !v.IsNullOrEmpty).Select(v => v.ToString()).Distinct().ToArray();
        if (userIds.Length > 0) db.KeyDelete(userIds.Select(u => (RedisKey)RedisContext.UserKey(u)).ToArray());
        db.KeyDelete(RedisContext.UserByNameKey);
    }
}

/// <summary>
/// Redis 群内智能体触发规则存储：以 <c>agui:registry:agents</c> hash 存（key = "agentId\u0000groupId"），
/// 随 AgentRegistry 写通（Upsert / Delete 即写），启动时整体加载（LoadAll）。
/// </summary>
public sealed class RedisAgentRegistryStore : IAgentRegistryStore
{
    private static readonly char Separator = '\u0000';
    private readonly RedisContext _ctx;
    public RedisAgentRegistryStore(RedisContext ctx) => _ctx = ctx;

    public IReadOnlyList<AgentRegistration> LoadAll()
    {
        var entries = _ctx.Db.HashGetAll(RedisContext.AgentRegistryKey);
        return entries.Where(e => !e.Value.IsNullOrEmpty)
            .Select(e => Deserialize(e.Value.ToString())).ToList();
    }

    public void Upsert(AgentRegistration registration)
        => _ctx.Db.HashSet(RedisContext.AgentRegistryKey, Compose(registration.AgentId, registration.GroupId),
            RedisContext.Serialize(registration));

    public void Delete(string agentId, string? groupId)
    {
        var db = _ctx.Db;
        if (groupId is not null)
        {
            db.HashDelete(RedisContext.AgentRegistryKey, Compose(agentId, groupId));
            return;
        }
        // 删除该智能体的全部群注册
        var entries = db.HashGetAll(RedisContext.AgentRegistryKey);
        foreach (var e in entries)
        {
            if (e.Value.IsNullOrEmpty) continue;
            var reg = Deserialize(e.Value.ToString());
            if (reg.AgentId == agentId) db.HashDelete(RedisContext.AgentRegistryKey, e.Name);
        }
    }

    private static string Compose(string agentId, string groupId) => $"{agentId}{Separator}{groupId}";

    private static AgentRegistration Deserialize(string json)
        => JsonSerializer.Deserialize<AgentRegistration>(json, AguiJson.Options) ?? throw new InvalidDataException("注册记录反序列化失败");
}


/// <summary>
/// Redis 模型用量存储：每行键 <c>agui:usage:{date}:{agent}:{user}</c> 以 hash 存各 token 计数，
/// 调用时 INCR 累加；日期索引 set <c>agui:usage:dates</c> 用于按区间检索。
/// </summary>
public sealed class RedisUsageStore : IUsageStore
{
    private readonly RedisContext _ctx;
    public RedisUsageStore(RedisContext ctx) => _ctx = ctx;

    public void RecordUsage(string date, string agentId, string userId, long promptTokens, long completionTokens, long reasoningTokens)
    {
        var db = _ctx.Db;
        var row = RedisContext.UsageRowKey(date, agentId, userId);
        var tx = db.CreateTransaction();
        _ = tx.HashIncrementAsync(row, "prompt", promptTokens);
        _ = tx.HashIncrementAsync(row, "completion", completionTokens);
        _ = tx.HashIncrementAsync(row, "reasoning", reasoningTokens);
        _ = tx.HashIncrementAsync(row, "calls", 1);
        _ = tx.SetAddAsync(RedisContext.UsageDateIndexKey, date);
        _ = tx.KeyPersistAsync(row);
        tx.Execute();
    }

    public long GetUserUsage(string userId, string date)
    {
        var db = _ctx.Db;
        var server = _ctx.Mux.GetServer(_ctx.Mux.GetEndPoints()[0]);
        var keys = server.Keys(pattern: $"agui:usage:{date}:*:*");
        long total = 0;
        foreach (var key in keys)
        {
            var agentAndUser = key.ToString()[$"agui:usage:{date}:".Length..];
            var idx = agentAndUser.LastIndexOf(':');
            if (idx < 0) continue;
            var userPart = agentAndUser[(idx + 1)..];
            if (userPart != userId) continue;
            total += GetRowTotal(key.ToString());
        }
        return total;
    }

    public IReadOnlyList<UsageAggregate> GetUsageBetween(string fromDate, string toDate)
    {
        var db = _ctx.Db;
        var result = new List<UsageAggregate>();
        var allDates = db.SetMembers(RedisContext.UsageDateIndexKey)
            .Where(v => !v.IsNullOrEmpty).Select(v => v.ToString()).Where(d => d.CompareTo(fromDate) >= 0 && d.CompareTo(toDate) <= 0)
            .OrderBy(d => d).ToArray();
        if (allDates.Length == 0) return result;
        var server = _ctx.Mux.GetServer(_ctx.Mux.GetEndPoints()[0]);
        var keys = server.Keys(pattern: "agui:usage:*").ToArray();
        foreach (var key in keys)
        {
            var full = key.ToString();
            var afterPrefix = full["agui:usage:".Length..];
            var parts = afterPrefix.Split(':');
            if (parts.Length < 3) continue;
            var date = parts[0];
            var agent = parts[1];
            var user = string.Join(":", parts.Skip(2)); // user 部分（agent / user 内部不含冒号）
            if (date.CompareTo(fromDate) < 0 || date.CompareTo(toDate) > 0) continue;
            var fields = db.HashGetAll(key);
            var prompt = Field(fields, "prompt");
            var completion = Field(fields, "completion");
            var reasoning = Field(fields, "reasoning");
            var calls = Field(fields, "calls");
            result.Add(new UsageAggregate(date, agent, user, prompt, completion, reasoning, calls));
        }
        return result.OrderBy(u => u.Date).ToList();
    }

    public void ClearAll() => _ctx.FlushAguiKeys();

    private long GetRowTotal(string key)
    {
        var fields = _ctx.Db.HashGetAll(key);
        return Field(fields, "prompt") + Field(fields, "completion") + Field(fields, "reasoning");
    }

    private static long Field(HashEntry[] fields, string name)
    {
        var e = Array.Find(fields, f => f.Name == name);
        return e.Name.IsNull ? 0 : (long)e.Value;
    }
}
