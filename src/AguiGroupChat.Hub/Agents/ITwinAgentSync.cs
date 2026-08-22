namespace AguiGroupChat.Hub.Agents;

/// <summary>
/// 分身跟随同步（GroupHub 钩子用）：公开群新增用户成员时自动加入其分身，用户退群时分身跟随退出。
/// 实现见 AguiGroupChat.Agents.TwinService（与 IMessageMemory 相同：接口在 Hub、实现在 Agents）。
/// </summary>
public interface ITwinAgentSync
{
    /// <summary>返回用户的已启用分身信息；未启用返回 null。</summary>
    TwinAgentInfo? GetTwinAgent(string userId);
}

/// <summary>已启用分身的轻量信息。</summary>
public sealed record TwinAgentInfo(string AgentId, string Nickname);
