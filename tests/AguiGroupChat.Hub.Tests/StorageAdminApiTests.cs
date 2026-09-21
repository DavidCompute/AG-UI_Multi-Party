using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 存储治理接口：只读统计管理员可看、非管理员 403；回收<b>默认关闭</b>（fail-closed），
/// 开启后才允许，且只删“无引用且过了宽限期”的孤儿附件。
/// </summary>
public sealed class StorageAdminApiTests : IClassFixture<AdminApiServerFixture>
{
    private readonly AdminApiServerFixture _fixture;
    private readonly HttpClient _client;

    public StorageAdminApiTests(AdminApiServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    private async Task<string> AdminTokenAsync()
    {
        var login = await _client.PostAsJsonAsync("/ag-ui/user/login",
            new { username = "admin_chief", password = "secret1" });
        if (login.StatusCode == HttpStatusCode.Unauthorized)
        {
            (await _client.PostAsJsonAsync("/ag-ui/user/register",
                new { username = "admin_chief", password = "secret1", nickname = "admin_chief" })).EnsureSuccessStatusCode();
            login = await _client.PostAsJsonAsync("/ag-ui/user/login",
                new { username = "admin_chief", password = "secret1" });
        }
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private async Task<string> PlainUserTokenAsync()
    {
        const string name = "storage_plain_user";
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register",
            new { username = name, password = "secret1", nickname = "p" });
        if (res.StatusCode == HttpStatusCode.Conflict)
            res = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = name, password = "secret1" });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    /// <summary>造一个真实的“孤儿附件”目录（无任何消息 / 知识库 / 头像引用）。</summary>
    private string NewOrphanAttachment()
    {
        var store = _fixture.App.Services.GetRequiredService<AttachmentStore>();
        var id = "att_" + Guid.NewGuid().ToString("N")[..16];
        Assert.True(store.RestoreFile(id, "orphan.txt", [1, 2, 3]));
        return id;
    }

    [Fact]
    public async Task StorageStats_ReadableByAdmin_ForbiddenForOthers()
    {
        var admin = await AdminTokenAsync();
        using var ok = Authed(HttpMethod.Get, "/ag-ui/admin/storage", admin);
        var res = await _client.SendAsync(ok);
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(d.GetProperty("available").GetBoolean());
        Assert.True(d.TryGetProperty("totalBytes", out _));
        Assert.True(d.TryGetProperty("orphanBytes", out _));
        Assert.False(d.GetProperty("allowReclaim").GetBoolean()); // 默认关闭

        var plain = await PlainUserTokenAsync();
        using var denied = Authed(HttpMethod.Get, "/ag-ui/admin/storage", plain);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
    }

    [Fact]
    public async Task Reclaim_IsFailClosed_ByDefault()
    {
        var admin = await AdminTokenAsync();
        NewOrphanAttachment(); // 有可回收的东西，但开关没开

        using var post = Authed(HttpMethod.Post, "/ag-ui/admin/storage/reclaim", admin);
        post.Content = JsonContent.Create(new { });
        var res = await _client.SendAsync(post);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode); // 未开启 → 拒绝（不是静默什么都不做）
    }

    [Fact]
    public async Task Reclaim_WhenEnabled_DeletesTheOrphanAndKeepsReferencedFiles()
    {
        var admin = await AdminTokenAsync();
        var store = _fixture.App.Services.GetRequiredService<AttachmentStore>();
        var orphan = NewOrphanAttachment();

        // 同一时刻造一个“仍被引用”的附件（挂到消息上）——它绝不能被回收
        var referenced = "att_" + Guid.NewGuid().ToString("N")[..16];
        Assert.True(store.RestoreFile(referenced, "keep.txt", [4, 5, 6]));
        var hub = _fixture.App.Services.GetRequiredService<AguiGroupChat.Hub.Messaging.GroupHub>();
        var group = await hub.CreateGroupAsync(new AguiGroupChat.Hub.Models.GroupCreateRequest
        { GroupName = "storage-test", OwnerId = "admin_chief", MemberIds = [] });
        await hub.SendMessageAsync(new AguiGroupChat.Hub.Models.GroupMessageSendRequest
        {
            GroupId = group.GroupId, UserId = "admin_chief", Content = "带附件",
            Attachments = [new AguiGroupChat.Hub.Models.AttachmentInfo
            {
                AttachmentId = referenced, Name = "keep.txt", ContentType = "text/plain",
                Size = 3, Url = $"/ag-ui/files/{referenced}/keep.txt", Kind = "text",
            }],
        });

        // 宽限期置 0（归一化后最小 1 小时）无法把“刚创建的文件”算成孤儿，因此这里把文件时间改旧
        Touch(store, orphan);
        Touch(store, referenced);

        var options = _fixture.App.Services.GetRequiredService<StorageGovernanceOptions>();
        options.AllowReclaim = true; // 运行时可改（单例），无需重启
        try
        {
            using var post = Authed(HttpMethod.Post, "/ag-ui/admin/storage/reclaim", admin);
            post.Content = JsonContent.Create(new { });
            var res = await _client.SendAsync(post);
            res.EnsureSuccessStatusCode();

            Assert.Null(store.ResolvePath(orphan));      // 孤儿被回收
            Assert.NotNull(store.ResolvePath(referenced)); // 仍被消息引用 → 一个字节都不动
        }
        finally
        {
            options.AllowReclaim = false;
            store.Delete(orphan);
            store.Delete(referenced);
        }
    }

    /// <summary>把附件文件的修改时间往前推，越过宽限期（默认 168 小时）。</summary>
    private static void Touch(AttachmentStore store, string attachmentId)
    {
        var path = store.ResolvePath(attachmentId);
        Assert.NotNull(path);
        File.SetLastWriteTimeUtc(path!, DateTime.UtcNow.AddDays(-30));
    }
}
