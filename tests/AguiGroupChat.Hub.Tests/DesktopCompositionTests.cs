using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AguiGroupChat.Desktop;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 桌面版组合根的<b>接口完整性</b>：前端会调用的 <c>/ag-ui/*</c> API 必须都真正挂上。
///
/// <para>
/// 背景（真实缺陷）：桌面版是**另一套组合根**（<c>src/AguiGroupChat.Desktop.Core/DesktopApp.cs</c>），
/// 与 Web 版（<c>src/AguiGroupChat.Web/Program.cs</c>）各写一份 <c>app.Map*Api()</c> 列表。
/// 加新 API 时很容易只改 Web 那份 —— 图库（<c>MapImageLibraryApi</c>）就漏了很久：
/// 持久化都注册了、前端也有完整界面，但 HTTP 路由根本没挂。
/// 而桌面根末尾有 <c>app.MapFallbackToFile("index.html")</c>，于是这些请求不会 404，
/// 而是**返回首页 HTML**：前端 <c>res.ok</c> 为真、<c>res.json()</c> 解析失败，
/// 表现为“点了没反应”或莫名其妙的状态码报错 —— 比 404 更难查。
/// </para>
///
/// <para>
/// 因此这里不看“有没有抛错”，而是拿**真实桌面宿主**逐条探测：响应不得是 SPA 首页（<c>text/html</c>）、
/// 也不得是 404 —— 401（要登录）/ 403 / 400 都算“路由存在”。新增 API 只改 Web 不放桌面时，本用例会红。
/// </para>
/// </summary>
public sealed class DesktopCompositionServerFixture : IAsyncLifetime
{
    public WebApplication App { get; private set; } = null!;
    public string HttpBase { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // 非 backend 模式：DesktopApp 自己挑空闲端口（不抢 5200，也不与运行中的真实桌面冲突），
        // 并且**内部已 StartAsync**（见 DesktopApp.Start 末尾）—— 这里不能再启一次。
        var (app, baseUrl) = DesktopApp.Start([], preferredPort: 5350, backendMode: false);
        App = app;
        HttpBase = baseUrl;
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.StopAsync();
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class DesktopCompositionTests : IClassFixture<DesktopCompositionServerFixture>
{
    private readonly DesktopCompositionServerFixture _fixture;
    private readonly HttpClient _client;

    public DesktopCompositionTests(DesktopCompositionServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    /// <summary>前端会调用的接口（路径 → 方法）。401/403/400 都算“已挂载”，HTML/404 都算“没挂”。</summary>
    public static TheoryData<string, string> FrontendApis => new()
    {
        { "GET", "/ag-ui/settings/branding" },        // 品牌（公开读）+ 顶栏 / 登录页
        { "GET", "/ag-ui/admin/config/governance" },  // 配置治理（管理员控制台）
        { "GET", "/ag-ui/image-libs" },               // 图库：列表（本次缺陷的主角）
        { "POST", "/ag-ui/mention-suggest" },         // 输入时「建议 @ 谁」
        { "POST", "/ag-ui/message-feedback" },        // 消息 👍/👎
        { "POST", "/ag-ui/plan/pause" },              // 编排计划「暂停」
        { "POST", "/ag-ui/plan/resume" },             // 编排计划「继续」
        { "GET", "/ag-ui/group/group_x/topic-summary" }, // 话题滚动小结
        // 对照：已挂载的接口（证明探测方式本身有效）
        { "GET", "/ag-ui/kb" },
    };

    [Theory]
    [MemberData(nameof(FrontendApis))]
    public async Task FrontendApi_IsMappedOnDesktopHost(string method, string path)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        // 故意不带令牌：401 说明路由在（缺的只是身份）；这正是我们想区分的
        var res = await _client.SendAsync(req);
        var mediaType = res.Content.Headers.ContentType?.MediaType ?? "";

        // 未挂载的接口在桌面根上会退化成三种"假"响应，三种都要拒：
        //   ① GET  → 命中 app.MapFallbackToFile("index.html")：**200 + text/html**；
        //   ② POST → 同一路径上有 GET 的 fallback 端点，方法不匹配 → **405**；
        //   ③ 两者之外还可能 404。
        // 已挂载的接口：公开的 200 JSON，需登录的 401（不会走到 fallback）。
        Assert.NotEqual(HttpStatusCode.NotFound, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.MethodNotAllowed, res.StatusCode);
        Assert.False(mediaType.Contains("html", StringComparison.OrdinalIgnoreCase),
            $"{method} {path} 返回了 SPA 首页（{mediaType}）—— 说明该 API 没挂在桌面组合根上，"
            + "前端会 res.ok=true 却解析不出 JSON（表现为“点了没反应”）。请在 DesktopApp.cs 补上对应的 app.Map*Api()。");
    }

    /// <summary>
    /// 两个组合根的 API 清单必须一致（除已文档化的两项）。
    ///
    /// <para>
    /// 上面的 HTTP 探测只能盖住“我列出来的那些路径”；这条源码扫描盖住**将来**：
    /// 以后在 Web 加了新 API 而忘了同步桌面时，这里就会红，不必等人去点。
    /// </para>
    /// </summary>
    [Fact]
    public void DesktopCompositionRoot_MapsEveryApiTheWebRootMaps()
    {
        var web = MappedApis(File.ReadAllText(RepoFile("src", "AguiGroupChat.Web", "Program.cs")));
        var desktop = MappedApis(File.ReadAllText(RepoFile("src", "AguiGroupChat.Desktop.Core", "DesktopApp.cs")));

        // 故意不挂的：它们是“公网 Hub + 内网桥”场景的能力，而桌面宿主就是用户本机
        // （见 DesktopApp.cs 里那段注释）。
        string[] intentional = ["MapNativeTunnelApi", "MapNativeBridgeDownloadApi"];

        var missing = web.Except(desktop).Except(intentional).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            "Web 组合根挂了但这些没挂到桌面组合根上：" + string.Join("、", missing)
            + "。桌面根末尾有 MapFallbackToFile，漏挂的接口不会 404 —— GET 会返回首页 HTML、POST 会 405，"
            + "前端表现为“点了没反应”，比 404 更难查。请在 DesktopApp.cs 补上 app.Map*Api()；"
            + "确实不需要的（仅内网桥场景）加进本用例的 intentional 名单并写清理由。");

        // 反向：桌面挂了 Web 没挂的 —— 同样说明两边又漂移了
        var extra = desktop.Except(web).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(extra.Count == 0, "桌面组合根挂了 Web 没有的接口：" + string.Join("、", extra));
    }

    /// <summary>抽出源码里的 <c>app.MapXxxApi()</c> 调用名（行首锚定：**注释掉的也算没挂**，必须真正在流水线上）。</summary>
    private static IReadOnlySet<string> MappedApis(string source)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m
                 in System.Text.RegularExpressions.Regex.Matches(source, @"^\s*app\.(Map[A-Za-z]+Api)\(\)",
                     System.Text.RegularExpressions.RegexOptions.Multiline))
            set.Add(m.Groups[1].Value);
        Assert.NotEmpty(set);
        return set;
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AguiGroupChat.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public async Task ImageLibrary_CreateAndList_WorkOnDesktopHost()
    {
        // 端到端：注册 → 建库 → 列表能看到。这是用户在桌面版点「创建图库」的真实路径。
        // 用户名每轮唯一：桌面宿主是**持久化**的（SQLite 落在测试输出目录），固定名字重跑会 409。
        var user = "desk_lib_" + Guid.NewGuid().ToString("N")[..8];
        var reg = await _client.PostAsJsonAsync("/ag-ui/user/register",
            new { username = user, password = "secret1", nickname = "desk" });
        Assert.True(reg.IsSuccessStatusCode, $"桌面宿主注册失败：{reg.StatusCode}");
        var token = (await reg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        using var auth = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/image-libs");
        auth.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        auth.Content = JsonContent.Create(new { name = "桌面图库验证", description = "e2e" });

        var create = await _client.SendAsync(auth);
        var body = await create.Content.ReadAsStringAsync();
        Assert.True(create.IsSuccessStatusCode, $"桌面宿主创建图库失败：{create.StatusCode} {body}");

        using var list = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/image-libs");
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var listed = await _client.SendAsync(list);
        Assert.True(listed.IsSuccessStatusCode, $"桌面宿主列图库失败：{listed.StatusCode}");
        Assert.Contains("桌面图库验证", await listed.Content.ReadAsStringAsync());
    }
}
