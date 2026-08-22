using System.Text;
using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Agents.Tools;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>智能体工具集测试：计算器 / 单位换算 / 群聊上下文工具 / 网络工具（SSRF 防护）与工具注册。</summary>
public sealed class ToolsTests
{
    // ================= CalculatorTool =================

    [Theory]
    [InlineData("(1+2)*3", "9")]
    [InlineData("15%4", "3")]
    [InlineData("2^10", "1024")]
    [InlineData("sqrt(144)", "12")]
    [InlineData("-2^2", "-4")]       // 幂优先于一元负号：-(2^2)
    [InlineData("2^-2", "0.25")]
    [InlineData("pi", "3.1415926536")]
    [InlineData("1.5e3", "1500")]
    [InlineData("min(3,1,2)", "1")]
    [InlineData("max(3,1,2)", "3")]
    [InlineData("100/3", "33.3333333333")]
    [InlineData("round(2.5)", "3")]
    public void Calculator_Evaluates(string expr, string expected)
        => Assert.Equal(expected, CalculatorTool.Evaluate(expr));

    [Theory]
    [InlineData("")]
    [InlineData("1 + rm -rf /")]    // 注入：未知标识符
    [InlineData("sqrt(1); drop table")] // 注入：非法字符
    [InlineData("1/0")]             // 除零
    [InlineData("1%0")]             // 取模零
    [InlineData("foo(1)")]          // 未知函数
    [InlineData("(1+2")]            // 括号不闭合
    [InlineData("1..2")]            // 无效数字
    public void Calculator_RejectsInvalid(string expr)
        => Assert.Contains("失败", CalculatorTool.Evaluate(expr));

    [Fact]
    public void Calculator_RejectsOverlongExpression()
    {
        var result = CalculatorTool.Evaluate(new string('1', 250) + "+2");
        Assert.Contains("过长", result);
    }

    // ================= UnitConverterTool =================

    [Theory]
    [InlineData(100, "km", "mile", "100 km = 62.1371192237 mile")]
    [InlineData(37, "c", "f", "37 c = 98.6 f")]
    [InlineData(2, "t", "kg", "2 t = 2000 kg")]
    [InlineData(1, "day", "h", "1 day = 24 h")]
    [InlineData(1, "gb", "mb", "1 gb = 1024 mb")]
    [InlineData(90, "kmh", "mps", "90 kmh = 25 mps")]
    [InlineData(1, "foot", "inch", "1 foot = 12 inch")]
    [InlineData(1, "lb", "kg", "1 lb = 0.45359237 kg")]
    [InlineData(0, "k", "c", "0 k = -273.15 c")]
    public void UnitConverter_Converts(double value, string from, string to, string expected)
        => Assert.Equal(expected, UnitConverterTool.Convert(value, from, to));

    [Theory]
    [InlineData(1, "km", "kg", "类别不同")]   // 类别不一致
    [InlineData(1, "parsec", "m", "未知单位")] // 未知单位
    [InlineData(1, "", "m", "请提供")]         // 缺单位
    public void UnitConverter_RejectsInvalid(double value, string from, string to, string expectedPart)
        => Assert.Contains(expectedPart, UnitConverterTool.Convert(value, from, to));

    // ================= 工具注册（AgentCatalog） =================

    private static AgentCatalog CreateCatalog(AgentOptions options)
        => new(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());

    [Fact]
    public void BuildTools_RegistersLocalTools()
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = true,
            Agents = [new AgentDefinition { AgentId = "a1", Nickname = "助手", Description = "", Instructions = "" }],
        };
        var catalog = CreateCatalog(options);
        var names = catalog.GetAgentToolNames("a1").ToHashSet();
        Assert.Contains("get_current_time", names);
        Assert.Contains("calculator", names);
        Assert.Contains("unit_converter", names);
        Assert.Contains("group_memory_search", names);
        Assert.Contains("read_attachment", names);
        Assert.Contains("publish_announcement", names);
        Assert.DoesNotContain("web_search", names);  // 网络工具默认不启用
    }

    // ================= 4.1 差异化审批策略（per-agent approval override） =================

    [Fact]
    public void Approval_ToolWrappedByDefault()
    {
        // 全局默认名单包含 publish_announcement：该工具应被审批包装
        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = true,
            Agents = [new AgentDefinition { AgentId = "a1", Nickname = "助手", Description = "", Instructions = "" }],
        };
        var catalog = CreateCatalog(options);
        Assert.Contains("publish_announcement", catalog.GetAgentApprovalToolNames("a1"));
    }

    [Fact]
    public void Approval_PerAgentOverride_ReplacesGlobal()
    {
        // 智能体级名单 ["calculator"] 覆盖全局：本智能体只有 calculator 需审批（publish_announcement 不再审批）
        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = true,
            RequireApprovalToolNames = ["publish_announcement"], // 全局默认
            Agents =
            [
                new AgentDefinition { AgentId = "a1", Nickname = "A", RequireApprovalToolNames = ["calculator"], Description = "", Instructions = "" },
                new AgentDefinition { AgentId = "a2", Nickname = "B", Description = "", Instructions = "" },
            ],
        };
        var catalog = CreateCatalog(options);
        // a1：用自身名单，仅 calculator 需审批
        var a1Approvals = catalog.GetAgentApprovalToolNames("a1").ToHashSet();
        Assert.Contains("calculator", a1Approvals);
        Assert.DoesNotContain("publish_announcement", a1Approvals);
        // a2：未配置，回退全局，仍为 publish_announcement 需审批
        var a2Approvals = catalog.GetAgentApprovalToolNames("a2").ToHashSet();
        Assert.Contains("publish_announcement", a2Approvals);
        Assert.DoesNotContain("calculator", a2Approvals);
    }

    [Fact]
    public void Approval_CreateSkillAlwaysApprovalRequired()
    {
        // create_skill 为极敏感工具，即使智能体名单为空也始终需审批
        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = true,
            Agents = [new AgentDefinition { AgentId = "a1", Nickname = "助手", Description = "", Instructions = "" }],
        };
        var catalog = CreateCatalog(options);
        Assert.Contains("create_skill", catalog.GetAgentApprovalToolNames("a1"));
    }

    [Fact]
    public void BuildTools_EnableWebTools_AddsWebTools()
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = true,
            EnableWebTools = true,
            Agents = [new AgentDefinition { AgentId = "a1", Nickname = "助手", Description = "", Instructions = "" }],
        };
        var catalog = CreateCatalog(options);
        var names = catalog.GetAgentToolNames("a1").ToHashSet();
        Assert.Contains("web_search", names);
        Assert.Contains("read_url", names);
    }

    [Fact]
    public void BuildTools_Disabled_NoTools()
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = false,
            Agents = [new AgentDefinition { AgentId = "a1", Nickname = "助手", Description = "", Instructions = "" }],
        };
        var catalog = CreateCatalog(options);
        Assert.Empty(catalog.GetAgentToolNames("a1"));
    }

    // ================= GroupContextTools =================

    [Fact]
    public async Task ReadAttachment_ReturnsText()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agui-tools-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new AttachmentStore(dir);
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes("这是附件内容 SKY-2026"));
            var info = store.Save("说明.txt", "text/plain", ms, ms.Length);

            var tools = new GroupContextTools(new ServiceCollection().AddSingleton(store).BuildServiceProvider(), new AgentOptions(), NullLoggerFactory.Instance);
            var text = await tools.ReadAttachment(info.AttachmentId);
            Assert.Contains("SKY-2026", text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ReadAttachment_UnknownId_ReturnsError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agui-tools-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new AttachmentStore(dir);
            var tools = new GroupContextTools(new ServiceCollection().AddSingleton(store).BuildServiceProvider(), new AgentOptions(), NullLoggerFactory.Instance);
            var text = await tools.ReadAttachment("att_missing");
            Assert.Contains("不存在", text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private sealed class FakeMemory : IMessageMemory
    {
        public IReadOnlyList<MessageMemoryHit> Hits { get; set; } = [];
        public void Remember(MessageMemoryEntry entry) { }
        public void Forget(string groupId, string messageId) { }
        public void RemoveGroup(string groupId) { }
        public Task<IReadOnlyList<MessageMemoryHit>> SearchAsync(string groupId, string agentId, string query, CancellationToken ct = default)
            => Task.FromResult(Hits);
        public Task<IReadOnlyList<MessageMemoryHit>> SearchPersonAsync(string personId, string currentGroupId, string query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MessageMemoryHit>>([]);
        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset) => [];
        public long CountMessages(string? groupId, string? senderId, string? keyword) => 0;
        public IReadOnlyList<MessageMemoryGroupStat> GroupStats() => [];
        public bool DeleteByMessageId(string messageId) => false;
        public bool UpdateImportance(string messageId, int importance) => false;
        public int ForgetGroup(string? groupId, double? retentionHours) => 0;
        public int PruneExpired() => 0;
    }

    [Fact]
    public async Task SearchMemory_WithoutRunContext_ReturnsHint()
    {
        var tools = new GroupContextTools(new ServiceCollection().BuildServiceProvider(), new AgentOptions(), NullLoggerFactory.Instance);
        var result = await tools.SearchMemory("项目背景");
        Assert.Contains("运行上下文", result);
    }

    [Fact]
    public async Task SearchMemory_WithRunContext_ReturnsHits()
    {
        var memory = new FakeMemory
        {
            Hits =
            [
                new MessageMemoryHit("m1", "需求：用 WebSocket 推送", "user_1", 1700000000000, 0.9),
                new MessageMemoryHit("m2", "决策：先做最小闭环", "agent_1", 1700000000000, 0.8),
            ],
        };
        var tools = new GroupContextTools(
            new ServiceCollection().AddSingleton<IMessageMemory>(memory).BuildServiceProvider(),
            new AgentOptions(),
            NullLoggerFactory.Instance);

        var ctx = new AgentInvocationContext("g1", "t1", "agent_1", "助手", "msg1", "user_1", "hello", [], false);
        var prev = AgentGateway.AmbientContext.Value;
        AgentGateway.AmbientContext.Value = ctx;
        try
        {
            var result = await tools.SearchMemory("WebSocket");
            Assert.Contains("WebSocket 推送", result);
            Assert.Contains("相似度 0.9", result);
        }
        finally { AgentGateway.AmbientContext.Value = prev; }
    }

    /// <summary>低相关记忆（相似度 &lt; 0.40）被物理过滤：返回明确提示而非强行引用，避免记忆泛滥。</summary>
    [Fact]
    public async Task SearchMemory_FiltersLowScoreHits()
    {
        var memory = new FakeMemory
        {
            Hits =
            [
                new MessageMemoryHit("m1", "需求：用 WebSocket 推送", "user_1", 1700000000000, 0.9),
                new MessageMemoryHit("m2", "无关闲聊：今天天气不错", "user_2", 1700000000000, 0.3), // 低于 0.40 阈值
            ],
        };
        var tools = new GroupContextTools(
            new ServiceCollection().AddSingleton<IMessageMemory>(memory).BuildServiceProvider(),
            new AgentOptions(),
            NullLoggerFactory.Instance);
        var ctx = new AgentInvocationContext("g1", "t1", "agent_1", "助手", "msg1", "user_1", "hello", [], false);
        var prev = AgentGateway.AmbientContext.Value;
        AgentGateway.AmbientContext.Value = ctx;
        try
        {
            var result = await tools.SearchMemory("WebSocket");
            Assert.Contains("WebSocket 推送", result);
            Assert.DoesNotContain("今天天气不错", result); // 低分命中不返回
        }
        finally { AgentGateway.AmbientContext.Value = prev; }
    }

    /// <summary>全部命中都低相关时返回「未检索到足够相关」并提示不要编造。</summary>
    [Fact]
    public async Task SearchMemory_AllLowScore_ReturnsNoRelevantHint()
    {
        var memory = new FakeMemory
        {
            Hits =
            [
                new MessageMemoryHit("m1", "无关内容一", "user_1", 1700000000000, 0.2),
                new MessageMemoryHit("m2", "无关内容二", "user_2", 1700000000000, 0.15),
            ],
        };
        var tools = new GroupContextTools(
            new ServiceCollection().AddSingleton<IMessageMemory>(memory).BuildServiceProvider(),
            new AgentOptions(),
            NullLoggerFactory.Instance);
        var ctx = new AgentInvocationContext("g1", "t1", "agent_1", "助手", "msg1", "user_1", "hello", [], false);
        var prev = AgentGateway.AmbientContext.Value;
        AgentGateway.AmbientContext.Value = ctx;
        try
        {
            var result = await tools.SearchMemory("问题");
            Assert.Contains("未检索到足够相关", result);
            Assert.Contains("不要编造", result);
        }
        finally { AgentGateway.AmbientContext.Value = prev; }
    }

    /// <summary>多条高相关命中时收紧到最多 3 条（避免一次注入过多记忆）。</summary>
    [Fact]
    public async Task SearchMemory_LimitsToTop3()
    {
        var memory = new FakeMemory
        {
            Hits = Enumerable.Range(1, 6)
                .Select(i => new MessageMemoryHit("m" + i, $"相关记忆 {i}", "user_1", 1700000000000, 0.9 - i * 0.05))
                .ToList(),
        };
        var tools = new GroupContextTools(
            new ServiceCollection().AddSingleton<IMessageMemory>(memory).BuildServiceProvider(),
            new AgentOptions(),
            NullLoggerFactory.Instance);
        var ctx = new AgentInvocationContext("g1", "t1", "agent_1", "助手", "msg1", "user_1", "hello", [], false);
        var prev = AgentGateway.AmbientContext.Value;
        AgentGateway.AmbientContext.Value = ctx;
        try
        {
            var result = await tools.SearchMemory("问题");
            // 命中 0.85/0.80/0.75 三条（0.9-6*0.05=0.6 仍 ≥0.4，但只取前 3）
            Assert.Equal(3, result.Split("\n").Length);
            Assert.Contains("相关记忆 1", result);
            Assert.DoesNotContain("相关记忆 4", result);
        }
        finally { AgentGateway.AmbientContext.Value = prev; }
    }

    [Fact]
    public async Task SearchMemory_MemoryNotEnabled_ReturnsHint()
    {
        var tools = new GroupContextTools(new ServiceCollection().BuildServiceProvider(), new AgentOptions(), NullLoggerFactory.Instance);
        var ctx = new AgentInvocationContext("g1", "t1", "agent_1", "助手", "msg1", "user_1", "hello", [], false);
        var prev = AgentGateway.AmbientContext.Value;
        AgentGateway.AmbientContext.Value = ctx;
        try
        {
            var result = await tools.SearchMemory("背景");
            Assert.Contains("未启用", result);
        }
        finally { AgentGateway.AmbientContext.Value = prev; }
    }

    // ================= WebTools（SSRF 防护） =================

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://localhost/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // 云元数据端点
    public async Task ReadUrl_RejectsPrivateAddress(string url)
    {
        var tools = new WebTools(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance);
        var result = await tools.ReadUrl(url);
        Assert.Contains("拒绝", result);
    }

    [Fact]
    public async Task ReadUrl_RejectsNonHttp()
    {
        var tools = new WebTools(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance);
        Assert.Contains("仅支持 http/https", await tools.ReadUrl("ftp://example.com/file"));
        Assert.Contains("仅支持 http/https", await tools.ReadUrl("not-a-url"));
    }

    [Fact]
    public async Task WebSearch_EmptyQuery_ReturnsError()
    {
        var tools = new WebTools(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance);
        Assert.Contains("查询词为空", await tools.WebSearch(""));
    }

    // ================= 端到端：工具调用链路 =================

    /// <summary>端到端：智能体 A 配置技能指向 B，触发 A 后 mock 调用技能 → 子代理 B 经框架 run 回复 → 结果回灌群聊。</summary>
    [Fact]
    public async Task Gateway_SkillCall_InvokesTargetAgent()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["agent_host"],
            Members =
            [
                new MemberSeed { MemberId = "agent_host", MemberType = MemberType.Agent, Nickname = "主助手" },
            ],
        });
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = true,
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_host", Nickname = "主助手", Description = "测试", Instructions = "你是主助手",
                    TriggerMode = AgentTriggerMode.Mentioned,
                    Skills =
                    [
                        new AgentSkillConfig { SkillId = "skill_docs", Description = "查询文档时调用", TargetAgentId = "agent_docs" },
                    ],
                },
                new AgentDefinition
                {
                    AgentId = "agent_docs", Nickname = "文档专家", Description = "测试", Instructions = "你是文档专家",
                    TriggerMode = AgentTriggerMode.Mentioned,
                },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var services = new ServiceCollection().AddSingleton(f.Hub).BuildServiceProvider();
        var gateway = new AgentGateway(catalog, services, options, attachmentStore: null, NullLogger<AgentGateway>.Instance);

        // 技能作为工具已挂载（工具名 = skillId）
        Assert.Contains("skill_docs", catalog.GetAgentToolNames("agent_host"));

        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId,
            ThreadId: "thread_" + group.GroupId,
            AgentId: "agent_host",
            AgentNickname: "主助手",
            TriggerMessageId: "msg_trigger",
            TriggerUserId: "user_1",
            Content: "请调用技能帮我查文档",
            Mentions: [],
            MentionAll: false), CancellationToken.None);
        Assert.True(result.Accepted);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        var events = new List<string>();
        while (DateTimeOffset.UtcNow < deadline)
        {
            events.AddRange(f.Drain(inbox));
            if (events.Any(e => HubFixture.TypeOf(e) == EventTypes.TextMessageEnd)) break;
            await Task.Delay(100);
        }
        // 技能调用链路：主助手 → skill_docs → 子代理「文档专家」回复 → 结果回灌群聊
        var texts = events
            .Where(e => HubFixture.TypeOf(e) == EventTypes.TextMessageContent)
            .Select(e => HubFixture.Parse(e).GetProperty("delta").GetString());
        Assert.Contains(texts, t => t is not null && t.Contains("文档专家", StringComparison.Ordinal));
    }

    /// <summary>技能循环引用防护：A→B→A 只展开一层，不抛异常。</summary>
    [Fact]
    public void SkillCall_CircularReference_DoesNotRecurse()
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = false,
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_a", Nickname = "A", Description = "", Instructions = "",
                    Skills =
                    [
                        new AgentSkillConfig { SkillId = "to_b", Description = "", TargetAgentId = "agent_b" },
                    ],
                },
                new AgentDefinition
                {
                    AgentId = "agent_b", Nickname = "B", Description = "", Instructions = "",
                    Skills =
                    [
                        new AgentSkillConfig { SkillId = "to_a", Description = "", TargetAgentId = "agent_a" },
                    ],
                },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var names = catalog.GetAgentToolNames("agent_a");
        Assert.Contains("to_b", names);   // A 的技能正常挂载
        Assert.DoesNotContain("to_a", names); // B 作为子代理时不带自己的技能（单层展开）
    }

    /// <summary>非法 SkillId（中文/空格/点号等）不挂载为工具：OpenAI 工具名规范 ^[a-zA-Z0-9_-]+$，
    /// 否则每次模型调用都会 400（invalid function name）。</summary>
    [Theory]
    [InlineData("技能分析")]
    [InlineData("skill docs")]
    [InlineData("skill.docs")]
    [InlineData("skill/1")]
    public void SkillCall_InvalidSkillId_IsSkipped(string skillId)
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = false,
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_host", Nickname = "主", Description = "", Instructions = "",
                    Skills =
                    [
                        new AgentSkillConfig { SkillId = skillId, Description = "", TargetAgentId = "agent_docs" },
                    ],
                },
                new AgentDefinition { AgentId = "agent_docs", Nickname = "文档", Description = "", Instructions = "" },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        Assert.DoesNotContain(skillId, catalog.GetAgentToolNames("agent_host"));
    }

    /// <summary>合法 SkillId 正常挂载（回归：避免正则把合规名也拦截）。</summary>
    [Fact]
    public void SkillCall_ValidSkillId_IsMounted()
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = false,
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_host", Nickname = "主", Description = "", Instructions = "",
                    Skills =
                    [
                        new AgentSkillConfig { SkillId = "skill_文档助手", Description = "", TargetAgentId = "agent_docs" },
                        new AgentSkillConfig { SkillId = "skill-docs-2", Description = "", TargetAgentId = "agent_docs" },
                        new AgentSkillConfig { SkillId = "skill_x", Description = "", TargetAgentId = "agent_docs" },
                    ],
                },
                new AgentDefinition { AgentId = "agent_docs", Nickname = "文档", Description = "", Instructions = "" },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var names = catalog.GetAgentToolNames("agent_host");
        Assert.Contains("skill-docs-2", names);
        Assert.Contains("skill_x", names);
        Assert.DoesNotContain("skill_文档助手", names); // 中文仍被拦截
    }

    /// <summary>SkillId 留空 → 自动生成合法名（skill_&lt;目标ID&gt;）并写回定义（快照 / mock 一致）；
    /// 同目标多技能去重（_2 后缀）。</summary>
    [Fact]
    public void SkillCall_EmptySkillId_AutoGeneratesAndWritesBack()
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = false,
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_host", Nickname = "主", Description = "", Instructions = "",
                    Skills =
                    [
                        new AgentSkillConfig { SkillId = "", Description = "一", TargetAgentId = "agent_docs" },
                        new AgentSkillConfig { SkillId = "", Description = "二", TargetAgentId = "agent_docs" },
                    ],
                },
                new AgentDefinition { AgentId = "agent_docs", Nickname = "文档", Description = "", Instructions = "" },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var names = catalog.GetAgentToolNames("agent_host").ToList();
        Assert.Contains("skill_agent_docs", names);
        Assert.Contains("skill_agent_docs_2", names);
        // 写回定义：下次挂载 / 快照 / mock 使用同一名字
        var def = catalog.GetDefinition("agent_host")!;
        Assert.Equal("skill_agent_docs", def.Skills![0].SkillId);
        Assert.Equal("skill_agent_docs_2", def.Skills[1].SkillId);
    }

    /// <summary>mock 智能体收到「计算 2^10」→ 模型调用 calculator → 真实执行 → 结果回灌群聊。</summary>
    [Fact]
    public async Task Gateway_CalculatorTool_ExecutesAndStreamsResult()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["agent_calc"],
            Members =
            [
                new MemberSeed { MemberId = "agent_calc", MemberType = MemberType.Agent, Nickname = "计算助手" },
            ],
        });
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = true,
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_calc", Nickname = "计算助手", Description = "测试", Instructions = "你是计算助手",
                    TriggerMode = AgentTriggerMode.Mentioned,
                },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var services = new ServiceCollection().AddSingleton(f.Hub).BuildServiceProvider();
        var gateway = new AgentGateway(catalog, services, options, attachmentStore: null, NullLogger<AgentGateway>.Instance);

        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId,
            ThreadId: "thread_" + group.GroupId,
            AgentId: "agent_calc",
            AgentNickname: "计算助手",
            TriggerMessageId: "msg_trigger",
            TriggerUserId: "user_1",
            Content: "帮我计算 2^10",
            Mentions: [],
            MentionAll: false), CancellationToken.None);
        Assert.True(result.Accepted);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        var events = new List<string>();
        while (DateTimeOffset.UtcNow < deadline)
        {
            events.AddRange(f.Drain(inbox));
            if (events.Any(e => HubFixture.TypeOf(e) == EventTypes.TextMessageEnd)) break;
            await Task.Delay(100);
        }
        var texts = events
            .Where(e => HubFixture.TypeOf(e) == EventTypes.TextMessageContent)
            .Select(e => HubFixture.Parse(e).GetProperty("delta").GetString());
        Assert.Contains(texts, t => t is not null && t.Contains("1024", StringComparison.Ordinal));
    }
}
