using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>复现桌面版知识库上传：SQLite + 图谱启用 + 真实 HTTP 上传/入库路径（与桌面组合接近），
/// 覆盖 AddDocumentAsync（附件）经 KnowledgeBaseCatalog 的向量 + 图谱入库。用桩 embedding 替代桌面本地模型。</summary>
public sealed class DesktopKbUploadIntegrationTests : IAsyncLifetime
{
    private WebApplication? _app;
    public string HttpBase { get; private set; } = ""!;
    public async Task InitializeAsync()
    {
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var dbFile = Path.Combine(Path.GetTempPath(), $"agui-desktopkb-{Guid.NewGuid():N}.sqlite");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // 与桌面一致：SQLite 存储 + 语义记忆开启 + 图谱开启；embedding 用桩（避免拉起本地模型）
            ["Storage:Provider"] = "sqlite",
            ["Storage:ConnectionString"] = $"Data Source={dbFile}",
            ["Agents:Provider"] = "mock",
            ["Agents:Memory:Enabled"] = "true",
            ["Agents:Memory:Provider"] = "http",
            ["Agents:Memory:EmbeddingDimensions"] = "8",
            ["Agents:Memory:GraphEnabled"] = "true",
            ["Persistence:Enabled"] = "false",
            ["Auth:RequireTokenOnRealTime"] = "true",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        // 用桩 embedding 覆盖（last-wins），使其与桌面本地 Llama 之外的其余全链路一致
        builder.Services.AddSingleton<AguiGroupChat.Agents.IEmbeddingProvider>(new StubEmb());
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        _app = builder.Build();
        HubApp.MapEndpoints(_app);
        _app.MapAgentApi();
        _app.MapAttachmentApi();
        _app.MapKnowledgeBaseApi();
        await _app.StartAsync();
        HttpBase = _app.Urls.First();
    }

    public async Task DisposeAsync() { if (_app is not null) await _app.DisposeAsync(); }

    private sealed class StubEmb : AguiGroupChat.Agents.IEmbeddingProvider
    {
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
        {
            var v = new float[8]; v[(text?.Length ?? 0) % 8] = 1f; return Task.FromResult<float[]?>(v);
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task UploadAttachment_AddToKb_EndsReady_OnSqliteWithGraph()
    {
        using var client = new HttpClient { BaseAddress = new Uri(HttpBase) };
        // 注册（首个即管理员，无演示账号，与桌面一致）
        var reg = await client.PostAsJsonAsync("/ag-ui/user/register", new { username = "kbuser", password = "123456", nickname = "KB" });
        reg.EnsureSuccessStatusCode();
        var regBody = await reg.Content.ReadFromJsonAsync<JsonElement>();
        var token = regBody.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // 建知识库
        var mk = await client.PostAsJsonAsync("/ag-ui/kb/", new { name = "Desktop KB", description = "d" });
        mk.EnsureSuccessStatusCode();
        var kbId = (await mk.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("kbId").GetString()!;

        // 上传 .txt 附件（真实 multipart）
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("报销制度：员工需在 7 个工作日内提交发票。DeepSeek 开发 V3模型，V3模型 应用 MoE架构")), "file", "制度.txt");
        var up = await client.PostAsync("/ag-ui/upload", form);
        up.EnsureSuccessStatusCode();
        var upBody = await up.Content.ReadFromJsonAsync<JsonElement>();
        var attId = upBody.GetProperty("attachments")[0].GetProperty("attachmentId").GetString()!;

        // 添加入知识库
        var add = await client.PostAsJsonAsync($"/ag-ui/kb/{kbId}/documents", new { attachmentId = attId });
        if (!add.IsSuccessStatusCode)
        {
            var eb = await add.Content.ReadAsStringAsync();
            Assert.Fail($"添加文档失败 HTTP {(int)add.StatusCode}: {eb}");
        }
        var addBody = await add.Content.ReadFromJsonAsync<JsonElement>();
        var docId = addBody.GetProperty("docId").GetString()!;

        // 轮询文档状态直到 ready / error / 超时
        string? status = null, err = null;
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(150);
            var list = await (await client.GetAsync("/ag-ui/kb/")).Content.ReadFromJsonAsync<JsonElement[]>();
            var kb = (list ?? []).First(x => x.GetProperty("kbId").GetString() == kbId);
            var docs = kb.GetProperty("documents");
            var doc = docs.EnumerateArray().FirstOrDefault(d => d.GetProperty("docId").GetString() == docId);
            status = doc.GetProperty("status").GetString();
            err = doc.GetProperty("error").GetString();
            if (status == "ready" || status == "error") break;
        }

        Assert.True(status == "ready", $"文档应 ready，实际 status={status} error={err}");
    }
}
