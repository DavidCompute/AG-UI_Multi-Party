using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Hub;
using AguiGroupChat.Sdk;
using AguiGroupChat.Sdk.Models;
using Xunit;

namespace AguiGroupChat.Sdk.Tests;

/// <summary>
/// SDK 端到端集成测试夹具：自托管真实 Kestrel（复用 HubApp 装配逻辑），
/// 播种示例数据（zhangsan/123456 → user_1001）并开启强制令牌鉴权，
/// 验证 SDK 的 HTTP 与 WebSocket 通道能对接真实 Hub。
/// </summary>
public sealed class SdkServerFixture : IAsyncLifetime
{
    public WebApplication App { get; private set; } = null!;
    public string HttpBase { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "true",
            ["Agents:Provider"] = "mock",
            ["Persistence:Enabled"] = "false",
            // 强制令牌鉴权，验证 SDK 走 Bearer 令牌路径（不使用 memberId 回退）
            ["Auth:RequireTokenOnRealTime"] = "true",
        });
        HubApp.ConfigureServices(builder);

        // 与 Web 生产应用一致：HTTP API 枚举字符串化（协议 §2），SDK 响应模型据此约定反序列化
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        await App.StartAsync();
        HubApp.InitializePersistence(App);
        // 种子：示例数据中的 user_1001（zhangsan / 123456）已加入「产品需求评审群」
        await HubApp.SeedSampleDataAsync(App);
        // v1.0.75 起不再播种演示账号——这里由夹具显式创建固定身份账号（zhangsan/lisi → user_1001/user_1002），
        // 对齐种子群的既有成员并绕开首账号管理员判定，供 SDK 用例登录使用；已存在则跳过。
        var auth = App.Services.GetRequiredService<AguiGroupChat.Hub.Users.AuthService>();
        if (auth is not null && App.Services.GetRequiredService<AguiGroupChat.Hub.Users.IUserStore>().GetUserByUsername("zhangsan") is null)
        {
            auth.Register("zhangsan", "123456", "张三", null, "user_1001");
            auth.Register("lisi", "123456", "李四", null, "user_1002");
        }
        HttpBase = App.Urls.First().TrimEnd('/');
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class SdkTests : IClassFixture<SdkServerFixture>, IAsyncLifetime
{
    private readonly SdkServerFixture _fixture;
    private AguiClient? _client;

    public SdkTests(SdkServerFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { _client?.Dispose(); return Task.CompletedTask; }

    private AguiClient NewClient()
    {
        _client?.Dispose();
        _client = new AguiClient(new AguiClientOptions
        {
            BaseUri = new Uri(_fixture.HttpBase),
        });
        return _client;
    }

    [Fact]
    public async Task Login_ListGroups_SendMessage_HttpFlow()
    {
        var client = NewClient();

        // 登录（演示种子账号）
        var auth = await client.LoginAsync("zhangsan", "123456");
        Assert.Equal("user_1001", auth.UserId);
        Assert.False(string.IsNullOrEmpty(auth.Token));
        client.Token = auth.Token;

        // 我的群列表（本人可查）
        var groups = await client.GetMyGroupsAsync("user_1001");
        Assert.NotNull(groups);
        var group = Assert.Single(groups!, g => g.GroupId is not null);
        var groupId = group.GroupId!;

        // 群快照
        var snapshot = await client.GetGroupSnapshotAsync(groupId);
        Assert.Equal(groupId, snapshot?.GroupId);
        Assert.Contains(snapshot!.Members!, m => m.MemberId == "user_1001");
        Assert.Contains(snapshot.Members!, m => m.MemberType == MemberType.Agent);

        // 发消息并提及需求助手
        var req = new GroupMessageSendRequest
        {
            GroupId = groupId,
            UserId = "user_1001",
            Content = "SDK 集成测试：请给出一份大纲",
            Mentions = ["agent_prd"],
        };
        var msg = await client.SendMessageAsync(req);
        Assert.NotNull(msg);
        Assert.Equal("SDK 集成测试：请给出一份大纲", msg!.Content);
        Assert.StartsWith("msg_", msg.MessageId);

        // 历史
        var history = await client.GetMessagesAsync(groupId);
        Assert.Contains(history!, m => m.MessageId == msg.MessageId);
    }

    [Fact]
    public async Task Register_CreateGroup_Search_Flow()
    {
        var client = NewClient();

        // 注册即登录
        var username = "sdk_" + Guid.NewGuid().ToString("N")[..8];
        var auth = await client.RegisterAsync(username, "123456", "SDK用户");
        client.Token = auth.Token;

        // 建群
        var create = await client.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "SDK 测试群",
            OwnerId = auth.UserId!,
        });
        Assert.NotNull(create);
        var groupId = create!.GroupId;
        Assert.StartsWith("group_", groupId);

        // 发消息 + 搜索
        await client.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = groupId,
            UserId = auth.UserId,
            Content = "搜索关键词 unicorn",
        });
        var search = await client.SearchMessagesAsync(groupId, "unicorn");
        Assert.Contains(search!, m => m.Content == "搜索关键词 unicorn");

        // 解散
        await client.DisbandGroupAsync(groupId, auth.UserId!);
        await Assert.ThrowsAsync<AguiException>(async () => await client.GetGroupSnapshotAsync(groupId));
    }

    [Fact]
    public async Task Login_Unauthorized_ThrowsAguiException()
    {
        var client = NewClient();
        var ex = await Assert.ThrowsAsync<AguiException>(() => client.LoginAsync("nobody", "wrong"));
        Assert.Equal(ErrorCodes.UserBadCredentials, ex.Code);
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task UnauthenticatedCreateGroup_ThrowsUnauthorized()
    {
        var client = NewClient();
        // 未登录调用写接口（强制令牌模式）应抛 USER_UNAUTHORIZED
        var ex = await Assert.ThrowsAsync<AguiException>(async () =>
            await client.CreateGroupAsync(new GroupCreateRequest
            {
                GroupName = "x",
                OwnerId = "user_x",
            }));
        Assert.Equal(ErrorCodes.UserUnauthorized, ex.Code);
    }
}
