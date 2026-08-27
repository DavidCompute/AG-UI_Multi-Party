using System.Collections.Concurrent;

namespace AguiGroupChat.Agents;

/// <summary>
/// 客户端执行技能（ExecutionLocation=Client）的结果共享存储：
/// 前端经本机桥/SDK 执行后回传的 <c>toolResult</c>，由 <see cref="AguiGroupChat.Agents.AgentGateway.ResumeRunAsync"/>
/// 在恢复前写入，客户端的技能占位函数（见 <see cref="AgentCatalog.Create"/>）执行时读取并返回给模型，
/// 使模型「以为」工具已在客户端执行并拿到了真实结果。
/// 键为技能工具名（SkillId）；取走即清（单次消费），避免串话。
/// </summary>
public static class ClientToolResultStore
{
    private static readonly ConcurrentDictionary<string, string> _results = new(StringComparer.Ordinal);

    /// <summary>写入某个客户端技能的前端执行结果。</summary>
    public static void Put(string toolName, string result) => _results[toolName] = result;

    /// <summary>读取并消费某个客户端技能的结果；无结果返回 null。</summary>
    public static string? ConsumeOrDefault(string toolName)
        => _results.TryRemove(toolName, out var v) ? v : null;

    /// <summary>仅测试用：清理全部。</summary>
    public static void Clear() => _results.Clear();
}
