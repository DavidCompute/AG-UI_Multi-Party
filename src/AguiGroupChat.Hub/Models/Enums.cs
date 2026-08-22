namespace AguiGroupChat.Hub.Models;

/// <summary>成员类型：用户 / 智能体（协议 2.2）。</summary>
public enum MemberType { User, Agent }

/// <summary>群角色：群主 / 管理员 / 普通成员（协议 2.2）。</summary>
public enum GroupRole { Owner, Admin, Normal }

/// <summary>在线状态（协议 2.2）。</summary>
public enum OnlineStatus { Online, Offline, Busy }

/// <summary>消息可见范围（协议 2.3）。</summary>
public enum MessageVisibility { All, Mentioned, Private }

/// <summary>离群类型（协议 4.3）：主动退群 / 被移出。</summary>
public enum LeaveType { Voluntary, Kick }

/// <summary>
/// 智能体触发模式（协议 §6）。
/// </summary>
public enum AgentTriggerMode
{
    /// <summary>提及触发：被 @ 或 @全体时才发言。</summary>
    Mentioned,

    /// <summary>全量监听：每条消息都接收（是否回复由网关自行决定）。</summary>
    AllMessages,

    /// <summary>关键词触发：命中关键词才发言。</summary>
    Keyword,

    /// <summary>
    /// 语境触发：对所有消息做语境判断，由模型根据上下文自主决定是否发言
    /// （不要求 @ 或关键词）。判断由 IAgentGateway 实现，返回 AGENT_DECIDED_SILENT 表示保持沉默。
    /// </summary>
    Contextual,
}
