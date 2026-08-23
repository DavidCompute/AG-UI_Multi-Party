using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Hub.Infra;

/// <summary>启动示例数据（appsettings 中 GroupChat:SeedSampleData=true 时启用），便于本地联调。</summary>
public sealed class SampleDataSeeder
{
    private readonly GroupHub _hub;
    private readonly AuthService? _auth;

    public SampleDataSeeder(GroupHub hub, AuthService? auth = null)
    {
        _hub = hub;
        _auth = auth;
    }

    public async Task SeedAsync()
    {
        // 注意：不播种演示账号（zhangsan / lisi）——否则首个真实注册用户会因 FirstUserIsAdmin 判定
        // 时已有这些内建账号而被跳过，导致「第一个注册用户无法成为管理员」。示例群 / 示例智能体
        // 用固定 memberId 创建，不依赖真实登录账号；需要演示登录账号时可用 appsettings 显式配置。

        var group = await _hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "产品需求评审群",
            OwnerId = "user_1001",
            MemberIds = ["user_1002", "agent_prd", "agent_code"],
            Members =
            [
                new MemberSeed { MemberId = "user_1001", Nickname = "张三" },
                new MemberSeed { MemberId = "user_1002", Nickname = "李四" },
                new MemberSeed { MemberId = "agent_prd", MemberType = MemberType.Agent, Nickname = "需求助手" },
                new MemberSeed { MemberId = "agent_code", MemberType = MemberType.Agent, Nickname = "代码助手" },
            ],
        });

        // 注册智能体触发规则（协议 §6）：需求助手=提及触发，代码助手=关键词触发
        _hub.RegisterAgent(new AgentRegisterRequest
        {
            AgentId = "agent_prd",
            Nickname = "需求助手",
            GroupIds = [group.GroupId],
            TriggerMode = AgentTriggerMode.Mentioned,
        });
        _hub.RegisterAgent(new AgentRegisterRequest
        {
            AgentId = "agent_code",
            Nickname = "代码助手",
            GroupIds = [group.GroupId],
            TriggerMode = AgentTriggerMode.Keyword,
            Keywords = ["代码", "实现", "接口"],
        });

        await _hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1001",
            Content = "大家看下本周新需求，@需求助手 帮我生成 V2 版本需求大纲",
            Mentions = ["agent_prd"],
        });
        await _hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1002",
            Content = "我补充一点：V2 需要支持 WebSocket 推送",
        });
    }
}
