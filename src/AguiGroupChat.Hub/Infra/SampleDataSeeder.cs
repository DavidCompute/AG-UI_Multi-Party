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
        SeedDemoUsers();

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

    /// <summary>
    /// 演示账号：与示例群成员 user_1001 / user_1002 固定对应，密码均为 123456。
    /// 重复启动（如热重载）时账号已存在则静默跳过。
    /// </summary>
    private void SeedDemoUsers()
    {
        if (_auth is null) return;
        try { _auth.Register("zhangsan", "123456", "张三", null, "user_1001"); }
        catch (AguiProtocolException) { /* 已存在则跳过 */ }
        try { _auth.Register("lisi", "123456", "李四", null, "user_1002"); }
        catch (AguiProtocolException) { /* 已存在则跳过 */ }
    }
}
