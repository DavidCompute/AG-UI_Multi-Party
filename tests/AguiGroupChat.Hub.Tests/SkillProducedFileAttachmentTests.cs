using System.IO.Compression;
using System.Text;
using AguiGroupChat.Agents;
using AguiGroupChat.Agents.Tools;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 技能产物 → 对话附件（可下载）：验证「技能写出文件 → 标记提取 → 入库 AttachmentStore →
/// 前端按 URL 下载」这条链路的每一环都能对上真实路由与真实存储。
///
/// 为什么不是纯单测拼装：回档逻辑（<c>AttachSkillProducedFilesAsync</c>）依赖 GroupHub 的
/// 流式消息上下文，构造成本高且与生产装配差异大；这里改为<b>复刻其判定顺序</b>（存在性 →
/// 尺寸 → 白名单 → Save），并<b>直接跑真技能</b>产出真文件，保证不是自说自话。
/// 标记解析本身由 <see cref="SkillProducedFileMarkerTests"/> 覆盖生产实现。
/// </summary>
public sealed class SkillProducedFileAttachmentTests
{
    /// <summary>与 AgentGateway.AttachSkillProducedFilesAsync 一致的候选提取（复用生产解析）。</summary>
    private static List<string> ExtractPaths(string content)
    {
        var found = new List<string>();
        foreach (var json in AgentGateway.ExtractProduceFileObjects(content))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("path", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String)
                    found.Add(p.GetString()!);
            }
            catch { /* 原样/还原两份候选，解析不出的跳过 */ }
        }
        return found.Distinct().ToList();
    }

    /// <summary>复刻网关的入库判定：跳过不存在 / 非白名单 / 空 / 超限，返回已入库附件。</summary>
    private static List<AttachmentInfo> Harvest(string content, AttachmentStore store)
    {
        var saved = new List<AttachmentInfo>();
        foreach (var path in ExtractPaths(content))
        {
            if (!File.Exists(path)) continue;
            if (!AttachmentStore.IsAllowedUploadExtension(path)) continue;
            var fi = new FileInfo(path);
            if (fi.Length <= 0 || fi.Length > AttachmentStore.MaxFileBytes) continue;
            using var fs = File.OpenRead(path);
            saved.Add(store.Save(fi.Name, GuessContentType(fi.Name), fs, fi.Length));
        }
        return saved;
    }

    /// <summary>构造一条带 produce_file 标记的结果文本（与技能返回体同形）。</summary>
    private static string MarkerFor(string path)
        => System.Text.Json.JsonSerializer.Serialize(new { ok = true, produce_file = new { path, bytes = 1 } });

    private static string GuessContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".txt" => "text/plain",
        _ => "application/octet-stream",
    };

    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agui-{tag}-{Guid.NewGuid():N}"[..40]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void RealSkillOutput_IsSavedAsDownloadableAttachment()
    {
        var outDir = TempDir("prod");
        Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", outDir);
        try
        {
            var body = new AgentSkillCatalog(NullLoggerFactory.Instance, new AgentOptions()).Get("docx_report")!.Body;
            var host = new DotnetSkillHost(NullLogger<DotnetSkillHost>.Instance, TempDir("nuget"));
            var json = """{"title":"产物回档验证","sections":[{"paragraph":"内容"}]}""";
            var result = host.Run(body, json, CancellationToken.None);

            Assert.Contains("produce_file", result);

            var store = new AttachmentStore(Path.Combine(outDir, "store"));
            var saved = Harvest(result, store);

            var att = Assert.Single(saved);
            Assert.Equal("docx", Path.GetExtension(att.Name).TrimStart('.').ToLowerInvariant());
            Assert.Contains("产物回档验证", att.Name);
            Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", att.ContentType);
            Assert.True(att.Size > 0);

            // 下游：前端 <a href="{Url}"> 必须能被下载路由匹配，且磁盘文件真实存在。
            // 注意 URL 里的文件名是 percent-encoded（中文 docx 名必走这层），路由按解码后取值。
            var m = System.Text.RegularExpressions.Regex.Match(att.Url, @"^/ag-ui/files/(?<id>att_[A-Za-z0-9]+)/(?<name>.+)$");
            Assert.True(m.Success, "URL 形态不符合下载路由：" + att.Url);
            Assert.Equal(att.AttachmentId, m.Groups["id"].Value);
            Assert.Equal(att.Name, Uri.UnescapeDataString(m.Groups["name"].Value));
            Assert.DoesNotContain(" ", att.Url[..att.Url.IndexOf('/')]);
            Assert.NotNull(store.GetAttachmentInfo(att.AttachmentId));
            var disk = store.ResolvePath(att.AttachmentId);
            Assert.NotNull(disk);
            Assert.True(File.Exists(disk), "入库文件在磁盘上不存在：" + disk);
            Assert.Equal(att.Size, new FileInfo(disk!).Length);

            // 内容确实是可打开的 docx（zip 容器 + document.xml）
            using var zip = ZipFile.OpenRead(disk!);
            Assert.Contains(zip.Entries, e => e.FullName == "word/document.xml");
        }
        finally { Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", null); }
    }

    [Fact]
    public void RepeatedRuns_ProduceDistinctAttachments()
    {
        // 同一标题连跑两次：文件名按标题命名会碰撞，但附件 ID 必须各自唯一（否则前端两条链接指向同一文件）
        var outDir = TempDir("prod-dup");
        Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", outDir);
        try
        {
            var body = new AgentSkillCatalog(NullLoggerFactory.Instance, new AgentOptions()).Get("docx_notice")!.Body;
            var host = new DotnetSkillHost(NullLogger<DotnetSkillHost>.Instance, TempDir("nuget2"));
            var store = new AttachmentStore(Path.Combine(outDir, "store"));
            var json = """{"title":"同名公告","content":"正文"}""";

            var first = Assert.Single(Harvest(host.Run(body, json, CancellationToken.None), store));
            var second = Assert.Single(Harvest(host.Run(body, json, CancellationToken.None), store));

            Assert.NotEqual(first.AttachmentId, second.AttachmentId);
            Assert.NotNull(store.ResolvePath(first.AttachmentId));
            Assert.NotNull(store.ResolvePath(second.AttachmentId));
        }
        finally { Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", null); }
    }

    [Fact]
    public void MissingEmptyAndOversizeFiles_AreSkippedWithoutThrowing()
    {
        // 回档是增强路径：单文件异常不得冒泡（否则会把整条正常消息打成失败）
        var dir = TempDir("prod-edge");
        var empty = Path.Combine(dir, "empty.docx");
        File.WriteAllBytes(empty, []);
        var content = MarkerFor(Path.Combine(dir, "nope.docx")) + "\n" + MarkerFor(empty);

        var store = new AttachmentStore(Path.Combine(dir, "store"));
        Assert.Empty(Harvest(content, store));
    }

    [Fact]
    public void UnsafeExtension_IsSkippedByHarvest()
    {
        // 技能（尤其 LLM 生成的）可能写出任意文件。上传端点按白名单拦截；
        // 回档路径必须同样拦截，否则 .html/.svg 可直接在浏览器内联渲染 → 存储型 XSS。
        Assert.DoesNotContain(".html", AttachmentStore.AllowedUploadExtensions);
        Assert.DoesNotContain(".svg", AttachmentStore.AllowedUploadExtensions);
        Assert.DoesNotContain(".js", AttachmentStore.AllowedUploadExtensions);
        Assert.False(AttachmentStore.IsAllowedUploadExtension("payload.html"));
        Assert.True(AttachmentStore.IsAllowedUploadExtension("报告.docx"));

        var dir = TempDir("prod-ext");
        var evil = Path.Combine(dir, "payload.html");
        File.WriteAllBytes(evil, "<script>alert(1)</script>"u8.ToArray());
        var safe = Path.Combine(dir, "正常.docx");
        File.WriteAllBytes(safe, MakeMinimalDocxBytes());
        var store = new AttachmentStore(Path.Combine(dir, "store"));

        // 同一批产物里，危险文件被丢弃、合法文件照常入库
        var saved = Harvest(MarkerFor(evil) + "\n" + MarkerFor(safe), store);
        var only = Assert.Single(saved);
        Assert.Equal("正常.docx", only.Name);
    }

    [Fact]
    public void SanitizedFileName_KeepsChineseAndStripsTraversal()
    {
        // 标题来自用户输入 → 可能带路径分隔符 / 上跳；入库名必须被净化，且中文可保留（前端 URL 要能显示）
        var dir = TempDir("prod-name");
        var src = Path.Combine(dir, "报告.docx");
        File.WriteAllBytes(src, MakeMinimalDocxBytes());
        var store = new AttachmentStore(Path.Combine(dir, "store"));
        using var fs = File.OpenRead(src);
        var info = store.Save("../../逃逸/九月 报告.docx", "application/octet-stream", fs, fs.Length);

        Assert.DoesNotContain("..", info.Name);
        Assert.DoesNotContain("/", info.Name);
        Assert.DoesNotContain("\\", info.Name);
        Assert.Contains("九月", info.Name);
        var disk = store.ResolvePath(info.AttachmentId);
        Assert.NotNull(disk);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(dir, "store")), Path.GetFullPath(disk!));
    }

    /// <summary>构造最小但合法的 docx（zip）字节，避免依赖外部素材。</summary>
    private static byte[] MakeMinimalDocxBytes()
    {
        var doc = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body><w:p><w:r><w:t>测试</w:t></w:r></w:p></w:body>
            </w:document>
            """;
        var ct = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="xml" ContentType="application/xml"/>
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            </Types>
            """;
        var rels = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
            </Relationships>
            """;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                using var w = new StreamWriter(s, Encoding.UTF8);
                w.Write(content);
            }
            Add("[Content_Types].xml", ct);
            Add("_rels/.rels", rels);
            Add("word/document.xml", doc);
        }
        return ms.ToArray();
    }
}
