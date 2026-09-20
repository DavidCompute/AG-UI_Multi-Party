using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 办公文档「在线查看」的转换器逻辑：缓存、失效、失败分类。
///
/// <para>
/// 用假转换器（<see cref="FakeSofficeRunner"/>）而不是真 LibreOffice：真实转换依赖服务端是否装了
/// LibreOffice（开发者本机可能没装、CI 也没有），把缓存 / 失效 / 错误分类这些**我们自己的逻辑**
/// 绑到它身上，测试就会变成“看环境脸色”。真机的端到端转换另由容器内实测覆盖。
/// </para>
/// </summary>
public sealed class DocPreviewConverterTests : IDisposable
{
    /// <summary>假转换器：把请求记下来，往输出目录写一份最小 PDF（可切换为失败 / 不可用 / 不产出）。</summary>
    public sealed class FakeSofficeRunner : ISofficeRunner
    {
        public static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%agui-fake\n%%EOF\n");

        public int Calls;
        public bool Fail;
        public bool Unavailable;
        public bool ProduceNothing;
        public string Detail = "转换器炸了";

        public Task<SofficeResult> ConvertToPdfAsync(string sourcePath, string outDir,
            CancellationToken ct = default)
        {
            Calls++;
            if (Unavailable) return Task.FromResult(new SofficeResult(false, "未安装转换组件", Unavailable: true));
            if (Fail) return Task.FromResult(new SofficeResult(false, Detail));
            if (!ProduceNothing)
                File.WriteAllBytes(Path.Combine(outDir, Path.GetFileNameWithoutExtension(sourcePath) + ".pdf"), PdfBytes);
            return Task.FromResult(new SofficeResult(true, "converted"));
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "agui-pv-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeSofficeRunner _runner = new();

    private OfficePreviewConverter NewConverter()
    {
        Directory.CreateDirectory(_root);
        return new OfficePreviewConverter(Path.Combine(_root, "cache"), _runner, NullLogger.Instance);
    }

    private string WriteSource(string fileName, string content = "hello")
    {
        var dir = Path.Combine(_root, "src");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    [Theory]
    [InlineData("a.zip")]
    [InlineData("a.txt")]
    [InlineData("a.bin")]
    [InlineData("noext")]
    public async Task NonPreviewableExtension_IsRejectedWithUnsupported(string fileName)
    {
        var outcome = await NewConverter().GetOrCreateAsync("att_x", WriteSource(fileName));

        Assert.False(outcome.Ok);
        Assert.Equal(PreviewFailure.Unsupported, outcome.Failure);
        Assert.Equal(0, _runner.Calls); // 不支持的类型不该白跑一次转换
    }

    [Fact]
    public async Task Pdf_PassesThroughWithoutConversion()
    {
        var path = WriteSource("doc.pdf", "%PDF-1.4 real");
        var outcome = await NewConverter().GetOrCreateAsync("att_x", path);

        Assert.True(outcome.Ok);
        Assert.Equal(path, outcome.PdfPath);   // 原样回源文件：PDF 浏览器本就能内联渲染
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task MissingSource_SurfacesMissingSource()
    {
        var outcome = await NewConverter().GetOrCreateAsync("att_x", Path.Combine(_root, "nope.docx"));

        Assert.False(outcome.Ok);
        Assert.Equal(PreviewFailure.MissingSource, outcome.Failure);
    }

    [Fact]
    public async Task Office_PreviewsOnce_ThenServesFromCache()
    {
        var converter = NewConverter();
        var path = WriteSource("报表.docx");

        var first = await converter.GetOrCreateAsync("att_a", path);
        var second = await converter.GetOrCreateAsync("att_a", path);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(first.PdfPath, second.PdfPath);
        Assert.Equal(FakeSofficeRunner.PdfBytes, await File.ReadAllBytesAsync(first.PdfPath!));
        Assert.Equal(1, _runner.Calls); // 第二次必须命中缓存（首次转换实测要好几秒）
    }

    [Fact]
    public async Task DifferentAttachments_DoNotShareCache()
    {
        var converter = NewConverter();
        var a = await converter.GetOrCreateAsync("att_a", WriteSource("a.docx", "甲"));
        var b = await converter.GetOrCreateAsync("att_b", WriteSource("b.docx", "乙"));

        Assert.True(a.Ok && b.Ok);
        Assert.NotEqual(a.PdfPath, b.PdfPath);
        Assert.Equal(2, _runner.Calls);
    }

    [Fact]
    public async Task SourceChanged_InvalidatesCache()
    {
        var converter = NewConverter();
        var path = WriteSource("会变.docx", "第一版");

        Assert.True((await converter.GetOrCreateAsync("att_a", path)).Ok);
        Assert.Equal(1, _runner.Calls);

        // 内容 + mtime 都变（模拟附件被替换）→ 指纹失效，必须重转
        await Task.Delay(20);
        File.WriteAllText(path, "第二版更长一点");
        Assert.True((await converter.GetOrCreateAsync("att_a", path)).Ok);

        Assert.Equal(2, _runner.Calls);
    }

    [Fact]
    public async Task ConcurrentRequests_ConvertOnlyOnce()
    {
        var converter = NewConverter();
        var path = WriteSource("并发.docx");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => converter.GetOrCreateAsync("att_a", path)));

        Assert.All(results, r => Assert.True(r.Ok));
        // 串行门 + 双检：8 个并发只转 1 次（LibreOffice 并发跑会互相踩）
        Assert.Equal(1, _runner.Calls);
    }

    [Fact]
    public async Task RunnerUnavailable_IsReportedAsUnavailable()
    {
        _runner.Unavailable = true;

        var outcome = await NewConverter().GetOrCreateAsync("att_a", WriteSource("x.docx"));

        Assert.False(outcome.Ok);
        Assert.Equal(PreviewFailure.Unavailable, outcome.Failure);
        Assert.Contains("未安装", outcome.Message);
    }

    [Fact]
    public async Task RunnerFailure_CarriesConverterDetail()
    {
        _runner.Fail = true;
        _runner.Detail = "Error: source file could not be loaded";

        var outcome = await NewConverter().GetOrCreateAsync("att_a", WriteSource("坏.docx"));

        Assert.False(outcome.Ok);
        Assert.Equal(PreviewFailure.ConversionFailed, outcome.Failure);
        Assert.Contains("source file could not be loaded", outcome.Message);
    }

    [Fact]
    public async Task RunnerProducesNoPdf_IsReportedAsFailure()
    {
        _runner.ProduceNothing = true;

        var outcome = await NewConverter().GetOrCreateAsync("att_a", WriteSource("空.docx"));

        Assert.False(outcome.Ok);
        Assert.Equal(PreviewFailure.ConversionFailed, outcome.Failure);
    }

    [Fact]
    public async Task FailedConversion_LeavesNoCache_SoNextTryReconverts()
    {
        var converter = NewConverter();
        var path = WriteSource("先坏后好.docx");

        _runner.Fail = true;
        Assert.False((await converter.GetOrCreateAsync("att_a", path)).Ok);
        _runner.Fail = false;
        Assert.True((await converter.GetOrCreateAsync("att_a", path)).Ok);
        Assert.Equal(2, _runner.Calls);
    }

    [Fact]
    public async Task ClearAll_DropsCachedPdfs()
    {
        var converter = NewConverter();
        var path = WriteSource("清理.docx");
        var outcome = await converter.GetOrCreateAsync("att_a", path);
        Assert.True(File.Exists(outcome.PdfPath!));

        converter.ClearAll();

        Assert.False(File.Exists(outcome.PdfPath!));
    }
}

/// <summary>
/// 未注册预览转换服务的宿主（只 MapAttachmentApi 的精简组合根，例如第三方只取附件功能）：
/// 在线查看是**可选能力**，必须降级为 503，而不能因为一处漏注册把整个应用的路由都拖垮。
///
/// <para>
/// 这个用例是真实事故的回放：最初端点把 <c>IDocPreviewConverter</c> 写成构造注入，
/// 于是一个没注册它的测试夹具（以及任何同类宿主）会让 minimal API 把该参数当**请求体**推断，
/// 结果整应用每条路由都 500 —— 一次性挂了 63 个无关用例。
/// </para>
/// </summary>
public sealed class DocPreviewUnwiredServerFixture : IAsyncLifetime
{
    public WebApplication App { get; private set; } = null!;
    public string HttpBase { get; private set; } = null!;
    public DocPreviewApiServerFixture.CapturingLoggerProvider Logs { get; } = new();

    public string RecentLogs(int take = 6)
    {
        lock (Logs.Lines) return string.Join("\n", Logs.Lines.TakeLast(take));
    }

    public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "agui-pvunwired-" + Guid.NewGuid().ToString("N")[..8]);

    public async Task InitializeAsync()
    {
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Agents:Provider"] = "mock",
            ["Persistence:Enabled"] = "false",
            ["Auth:RequireTokenOnRealTime"] = "true",
            ["Auth:FirstUserIsAdmin"] = "false",
        });
        HubApp.ConfigureServices(builder);
        // AttachmentApi 自身的 /files 端点依赖 AgentCatalog（由 AddAgentFramework 注册）——本用例
        // 只想验证「预览转换服务」这一项缺失时的降级，其它依赖照常满足
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(Logs);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        builder.Services.AddSingleton(new AttachmentStore(Path.Combine(TempRoot, "uploads")));
        // 故意不调 AddDocumentPreview

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapAttachmentApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
        try { Directory.Delete(TempRoot, recursive: true); } catch { /* 忽略 */ }
    }
}

public sealed class DocPreviewUnwiredTests : IClassFixture<DocPreviewUnwiredServerFixture>
{
    private readonly DocPreviewUnwiredServerFixture _fixture;
    private readonly HttpClient _client;

    public DocPreviewUnwiredTests(DocPreviewUnwiredServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    [Fact]
    public async Task WithoutConverterRegistration_OtherRoutesStillWork_AndPreviewDegradesTo503()
    {
        // 关键：整个应用的路由必须照常工作（曾经因为端点参数被当请求体推断，这里会 500）
        var reg = await _client.PostAsJsonAsync("/ag-ui/user/register",
            new { username = "unwired_u1", password = "secret1", nickname = "unwired" });
        Assert.True(reg.IsSuccessStatusCode,
            $"漏注册预览服务不应影响其它路由（实际 {reg.StatusCode}）：{await reg.Content.ReadAsStringAsync()}\n--- 服务端日志 ---\n{_fixture.RecentLogs(8)}");
        var token = (await reg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        foreach (var path in new[] { "/ag-ui/health", "/ag-ui/user/me" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var res = await _client.SendAsync(req);
            Assert.True(res.IsSuccessStatusCode, $"{path} 应正常（实际 {res.StatusCode}）");
        }

        // 预览单独降级为 503 + DOCUMENT_PREVIEW_FAILED（而不是 500）。
        // 需要先有一个**真实存在**的附件，否则会先撞上 404（附件不存在）。
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("预览降级用例"u8.ToArray()), "file", "报告.docx");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/upload") { Content = form };
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var up = await _client.SendAsync(upload);
        Assert.True(up.IsSuccessStatusCode, $"上传附件应正常（实际 {up.StatusCode}）");
        var att = (await up.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("attachments")[0];
        var attId = att.GetProperty("attachmentId").GetString()!;
        // 把附件设为自己的头像：走「头像附件放行」分支让权限校验通过，才能验证到转换服务缺失那一步
        // （未挂到任何消息、也未作头像的附件本来就该 403）
        using var profile = new HttpRequestMessage(HttpMethod.Put, "/ag-ui/user/profile");
        profile.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        profile.Content = JsonContent.Create(new { nickname = "unwired", avatar = att.GetProperty("url").GetString() });
        Assert.True((await _client.SendAsync(profile)).IsSuccessStatusCode, "设为头像应成功");

        using var pv = new HttpRequestMessage(HttpMethod.Get, $"/ag-ui/preview/{attId}");
        pv.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var preview = await _client.SendAsync(pv);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, preview.StatusCode);
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ErrorCodes.DocumentPreviewFailed, body.GetProperty("code").GetString());
    }
}

/// <summary>
/// 办公文档「在线查看」的 HTTP 端点：权限、状态码、缓存。自托管 Kestrel + 临时目录（不写进仓库数据目录）。
/// </summary>
public sealed class DocPreviewApiServerFixture : IAsyncLifetime
{
    /// <summary>把服务端 Warning+ 日志收下来，失败时能直接看到真实异常（不带它只能看到一个空的 500）。</summary>
    public sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public readonly List<string> Lines = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose() { }

        private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Lines)
                    owner.Lines.Add($"[{logLevel}] {category}: {formatter(state, exception)} {exception}");
            }
        }
    }

    public WebApplication App { get; private set; } = null!;
    public string HttpBase { get; private set; } = null!;
    public DocPreviewConverterTests.FakeSofficeRunner Runner { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();

    public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "agui-pvapi-" + Guid.NewGuid().ToString("N")[..8]);

    public string RecentLogs(int take = 8)
    {
        lock (Logs.Lines) return string.Join("\n", Logs.Lines.TakeLast(take));
    }

    public async Task InitializeAsync()
    {
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Agents:Provider"] = "mock",
            ["Persistence:Enabled"] = "false",
            ["Auth:RequireTokenOnRealTime"] = "true",
            ["Auth:FirstUserIsAdmin"] = "false",
        });
        HubApp.ConfigureServices(builder);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(Logs);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        // 附件放临时目录（不污染仓库 / 不依赖上一次运行的残留）。
        // 预览链路用组合根同一个注册入口，随后用假执行器覆盖真实 LibreOffice 进程。
        builder.Services.AddSingleton(new AttachmentStore(Path.Combine(TempRoot, "uploads")));
        builder.Services.AddDocumentPreview(Path.Combine(TempRoot, "preview"));
        builder.Services.AddSingleton<ISofficeRunner>(Runner);
        builder.Services.AddSingleton<SkillRunArtifactStore>(); // 技能试运行产物归属（附件访问校验的一路放行）

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapAttachmentApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
        try { Directory.Delete(TempRoot, recursive: true); } catch { /* 忽略 */ }
    }
}

public sealed class DocPreviewApiTests : IClassFixture<DocPreviewApiServerFixture>
{
    private readonly DocPreviewApiServerFixture _fixture;
    private readonly HttpClient _client;

    public DocPreviewApiTests(DocPreviewApiServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    // ================= 权限 =================

    [Fact]
    public async Task Preview_WithoutToken_Returns401()
    {
        var (_, _, attId) = await SeedGroupWithAttachmentAsync("pv401", "稿子.docx");

        var res = await _client.GetAsync($"/ag-ui/preview/{attId}");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Preview_UnknownAttachment_Returns404()
    {
        var (token, _, _) = await SeedGroupWithAttachmentAsync("pv404", "稿子.docx");

        var res = await _client.SendAsync(Authed(HttpMethod.Get, "/ag-ui/preview/att_doesnotexist", token));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Preview_NonMember_Returns403()
    {
        // 甲建群带附件，乙不在群里 → 无权预览（与下载同一套校验，不能出现绕过权限的读取口子）
        var (_, _, attId) = await SeedGroupWithAttachmentAsync("pv403a", "机密.docx");
        var outsider = await RegisterAsync("pv403b");
        var callsBefore = _fixture.Runner.Calls;

        var res = await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/preview/{attId}", outsider.Token));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        // 未过权限就不该触发转换（别替越权请求白干活）
        Assert.Equal(callsBefore, _fixture.Runner.Calls);
    }

    [Fact]
    public async Task Preview_RecalledMessage_Returns403()
    {
        var (token, groupId, attId) = await SeedGroupWithAttachmentAsync("pvrecall", "撤回稿.docx");
        var recall = await _client.SendAsync(Authed(HttpMethod.Post, "/ag-ui/group/message/recall", token,
            new { groupId, messageId = await LastMessageIdAsync(token, groupId), operatorId = (string?)null }));
        recall.EnsureSuccessStatusCode();

        var res = await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/preview/{attId}", token));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ================= 转换与状态码 =================

    [Fact]
    public async Task Preview_OfficeDocument_ReturnsInlinePdf_AndCaches()
    {
        var (token, _, attId) = await SeedGroupWithAttachmentAsync("pvok", "季度报告.docx");
        var callsBefore = _fixture.Runner.Calls;

        var res = await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/preview/{attId}", token));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
        // 内联渲染：不能带 Content-Disposition: attachment（否则浏览器会去下载，而不是在弹窗里看）
        Assert.Null(res.Content.Headers.ContentDisposition);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(callsBefore + 1, _fixture.Runner.Calls);

        // 再取一次：命中缓存，不再转换（大 PPT 每次重转要好几秒）
        var again = await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/preview/{attId}", token));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(callsBefore + 1, _fixture.Runner.Calls);
    }

    [Fact]
    public async Task Preview_TokenViaQueryString_Works()
    {
        // iframe / 新窗口拿不到 Authorization 头，只能用 ?token=（前端 authedAssetUrl 的机制）
        var (token, _, attId) = await SeedGroupWithAttachmentAsync("pvqs", "查询令牌.docx");

        var res = await _client.GetAsync($"/ag-ui/preview/{attId}?token={Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Preview_UnsupportedType_Returns400()
    {
        var (token, _, attId) = await SeedGroupWithAttachmentAsync("pvbadext", "归档.zip");

        var res = await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/preview/{attId}", token));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ErrorCodes.BadRequest, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Preview_ConverterUnavailable_Returns503()
    {
        var (token, _, attId) = await SeedGroupWithAttachmentAsync("pv503", "无组件.docx");
        _fixture.Runner.Unavailable = true;
        try
        {
            var res = await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/preview/{attId}", token));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(ErrorCodes.DocumentPreviewFailed, body.GetProperty("code").GetString());
        }
        finally { _fixture.Runner.Unavailable = false; }
    }

    [Fact]
    public async Task Preview_ConversionFailure_Returns500WithReason()
    {
        var (token, _, attId) = await SeedGroupWithAttachmentAsync("pv500", "转不动.docx");
        _fixture.Runner.Fail = true;
        try
        {
            var res = await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/preview/{attId}", token));

            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(ErrorCodes.DocumentPreviewFailed, body.GetProperty("code").GetString());
        }
        finally { _fixture.Runner.Fail = false; }
    }

    [Fact]
    public async Task Preview_PdfAttachment_PassesThroughWithoutConversion()
    {
        var (token, _, attId) = await SeedGroupWithAttachmentAsync("pvpdf", "原件.pdf",
            Encoding.ASCII.GetBytes("%PDF-1.4\n%uploaded\n%%EOF\n"));
        var callsBefore = _fixture.Runner.Calls;

        var res = await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/preview/{attId}", token));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(callsBefore, _fixture.Runner.Calls); // PDF 不必过转换器
    }

    [Fact]
    public async Task Preview_TrialRunArtifact_IsReadableOnlyByItsProducer()
    {
        // 技能库试运行产出的稿子不属于任何知聚消息：登记归属前谁都无权（403），
        // 登记后**产出者本人**可读 / 可下载（否则用户自己刚生成的稿子点开就是无权访问），他人仍不可读。
        var owner = await RegisterAsync("pv_art_owner");
        var other = await RegisterAsync("pv_art_other");
        var att = await UploadAsync(owner.Token, "试运行稿子.docx");
        var attId = att.GetProperty("attachmentId").GetString()!;
        var artifacts = _fixture.App.Services.GetRequiredService<SkillRunArtifactStore>();

        var before = await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/preview/{attId}", owner.Token));
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);

        artifacts.Register(attId, owner.UserId);

        var asOwner = await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/preview/{attId}", owner.Token));
        Assert.Equal(HttpStatusCode.OK, asOwner.StatusCode);
        Assert.Equal("application/pdf", asOwner.Content.Headers.ContentType?.MediaType);

        var asOther = await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/preview/{attId}", other.Token));
        Assert.Equal(HttpStatusCode.Forbidden, asOther.StatusCode);

        // 下载端点共用同一段校验：他人下载同样 403
        var name = Uri.EscapeDataString("试运行稿子.docx");
        var dlOther = await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/files/{attId}/{name}", other.Token));
        Assert.Equal(HttpStatusCode.Forbidden, dlOther.StatusCode);
        var dlOwner = await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/files/{attId}/{name}", owner.Token));
        Assert.Equal(HttpStatusCode.OK, dlOwner.StatusCode);
    }

    // ================= 辅助 =================

    private static HttpRequestMessage Authed(HttpMethod method, string path, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private async Task<(string Token, string UserId)> RegisterAsync(string username)
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register",
            new { username, password = "secret1", nickname = username });
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"注册失败 {res.StatusCode}：{body}\n--- 服务端日志 ---\n{_fixture.RecentLogs(12)}");
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        return (json.GetProperty("token").GetString()!, json.GetProperty("userId").GetString()!);
    }

    private async Task<string> LastMessageIdAsync(string token, string groupId)
    {
        var res = await _client.SendAsync(Authed(HttpMethod.Get,
            $"/ag-ui/group/{groupId}/messages?limit=1", token));
        res.EnsureSuccessStatusCode();
        var list = await res.Content.ReadFromJsonAsync<JsonElement[]>() ?? [];
        return list[^1].GetProperty("messageId").GetString()!;
    }

    /// <summary>建群 → 上传附件 → 发一条带该附件的消息，返回 (令牌, 群 ID, 附件 ID)。</summary>
    private async Task<(string Token, string GroupId, string AttachmentId)> SeedGroupWithAttachmentAsync(
        string username, string fileName, byte[]? content = null)
    {
        var (token, userId) = await RegisterAsync(username);
        var create = await _client.SendAsync(Authed(HttpMethod.Post, "/ag-ui/group/create", token,
            new { groupName = "预览测试群", ownerId = userId, memberIds = Array.Empty<string>(), members = Array.Empty<object>() }));
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        var attachment = await UploadAsync(token, fileName, content);

        var send = await _client.SendAsync(Authed(HttpMethod.Post, "/ag-ui/group/message/send", token,
            new { groupId, userId, content = "带附件的消息", attachments = new[] { attachment } }));
        send.EnsureSuccessStatusCode();

        return (token, groupId, attachment.GetProperty("attachmentId").GetString()!);
    }

    /// <summary>只上传一个附件（不建群、不发消息）——用于「不属于任何消息的试运行产物」用例。</summary>
    private async Task<JsonElement> UploadAsync(string token, string fileName, byte[]? content = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(content ?? Encoding.UTF8.GetBytes("预览用的假文档内容")), "file", fileName);
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/upload") { Content = form };
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var up = await _client.SendAsync(upload);
        up.EnsureSuccessStatusCode();
        return (await up.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("attachments")[0];
    }
}
