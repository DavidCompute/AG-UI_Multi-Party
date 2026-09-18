using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Agents.BuiltinSkills;
using AguiGroupChat.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf.IO;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 文档技能的「imageQuery → 平台图库」链路（Word / PDF 侧）。
///
/// <para>
/// 为什么值得单独钉住：这条链路横跨三处 —— ① 平台往入参注入检索范围句柄；
/// ② 技能回环 HTTP 回调 <c>/ag-ui/images/search</c>（带自令牌）；
/// ③ 平台只对自令牌返回<b>服务器本地路径</b>。任何一处断了，用户看到的现象都只是“图没配上”，
/// 不会有异常 —— 静默失效是最难查的一类问题，所以这里用一个最小 HTTP 服务器冒充平台端点，
/// 把整条链路真跑一遍（含“没有图库时跳过图但稿子照样生成”的降级）。
/// </para>
///
/// <para>
/// 跑<b>内置副本</b>（BuiltinSkills 下的 <c>*.skill.txt</c>）而不是 tools/ 下的源文件：
/// 这样顺带验证「改完生成器 → sync-builtin」这一步没漏（漏了就会静默跑旧正文）。
/// </para>
/// </summary>
[Collection(EnvVarCollection.Name)] // 改 AGUI_SELF_* / AGUI_DOCX_OUT / AGUI_PDF_OUT（进程级）→ 串行
public sealed class ImageQueryResolveTests
{
    /// <summary>4×4 PNG（内嵌 base64，避免测试依赖仓库里的二进制资产）。</summary>
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAANElEQVR42hXIIQEAMAhFQZKQhBDoaUIsyc+EphB7E2fO/PYeCAPzIiBM/UgCwuSPICBM9D59ZymRzPwZugAAAABJRU5ErkJggg==";

    private const string TestToken = "self-token-for-tests";

    // ===================== 冒充平台自调用端点 =====================

    /// <summary>
    /// 极简 HTTP 服务器：只回一个 JSON body 给任意请求，并记下请求行 / 自令牌头 / 请求体。
    ///
    /// <para>用裸 TcpListener 而不是 HttpListener：后者在 Windows 上需要 URL 预留（管理员），
    /// 而这里只需要“收一个 POST、回一段 JSON”，手写几十行反而没有权限与平台差异问题。</para>
    /// </summary>
    private sealed class FakeSelfApi : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private readonly string _response;

        private volatile string? _path;
        private volatile string? _tokenHeader;
        private volatile string? _body;

        public string BaseUrl { get; }
        public string? LastPath => _path;
        public string? LastTokenHeader => _tokenHeader;
        public string? LastBody => _body;

        public FakeSelfApi(string response)
        {
            _response = response;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        private void Loop()
        {
            while (true)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { return; }   // Stop() 关掉监听 → 退出
                try { Handle(client); }
                catch { /* 单个连接出错不影响后续 */ }
                finally { client.Dispose(); }
            }
        }

        private void Handle(TcpClient client)
        {
            using var stream = client.GetStream();
            using var all = new MemoryStream();
            var buf = new byte[4096];
            var headEnd = -1;
            var need = -1;
            while (true)
            {
                var n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                all.Write(buf, 0, n);
                var data = all.GetBuffer();
                var len = (int)all.Length;
                if (headEnd < 0)
                {
                    headEnd = FindCrlfCrlf(data, len);
                    if (headEnd < 0) continue;
                    headEnd += 4;
                    ParseHead(Encoding.UTF8.GetString(data, 0, headEnd), out need);
                }
                if (need < 0 || len - headEnd >= need) break;
            }
            var bytes = all.ToArray();
            if (headEnd > 0 && need > 0 && bytes.Length - headEnd >= need)
                _body = Encoding.UTF8.GetString(bytes, headEnd, need);

            var payload = Encoding.UTF8.GetBytes(_response);
            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: "
                + payload.Length + "\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private static int FindCrlfCrlf(byte[] b, int len)
        {
            for (var i = 0; i + 3 < len; i++)
                if (b[i] == (byte)'\r' && b[i + 1] == (byte)'\n' && b[i + 2] == (byte)'\r' && b[i + 3] == (byte)'\n')
                    return i;
            return -1;
        }

        private void ParseHead(string head, out int contentLength)
        {
            contentLength = -1;
            var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
            {
                var parts = lines[0].Split(' ');
                if (parts.Length >= 2) _path = parts[1];
            }
            for (var i = 1; i < lines.Length; i++)
            {
                var c = lines[i].IndexOf(':');
                if (c <= 0) continue;
                var k = lines[i].Substring(0, c).Trim();
                var v = lines[i].Substring(c + 1).Trim();
                if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out var cl)) contentLength = cl;
                if (k.Equals("AGUI_SELF_TOKEN", StringComparison.OrdinalIgnoreCase)) _tokenHeader = v;
            }
        }

        public void Dispose() => _listener.Stop();
    }

    /// <summary>平台检索接口的成功响应（只有自令牌才拿得到的 path 字段在这里一定有）。</summary>
    private static string ImagesJson(string path, string contentType, string caption = "团队会议 白板 讨论")
        => JsonSerializer.Serialize(new
        {
            query = "团队会议",
            count = 1,
            images = new[] { new { assetId = "asset_1", libId = "lib_1", libName = "宣传图库", fileName = Path.GetFileName(path), caption, contentType, score = 0.71, width = 4, height = 4, path } },
        });

    private static string EmptyImagesJson() => JsonSerializer.Serialize(new { query = "x", count = 0, images = Array.Empty<object>() });

    // ===================== 跑技能 =====================

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "agui-imgq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string WriteTinyPng(string dir, string name = "figure.png")
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, Convert.FromBase64String(TinyPngBase64));
        return p;
    }

    private static DotnetSkillHost NewHost(string tag)
        => new(NullLogger<DotnetSkillHost>.Instance,
            // NuGet 缓存根全进程共用一个：否则每个用例都会把 OpenXml / ImageSharp / PDFsharp 全量重下一次
            Path.Combine(Path.GetTempPath(), "agui-imgq-nuget-cache-" + tag));

    private static string DocxSource() => BuiltinDocxSkills.Build("docx_report", "docx_report.skill.txt", "x", "x").Body!;

    private static string PdfSource() => BuiltinPdfSkills.Build("pdf_doc", "pdf_doc.skill.txt", "x", "x").Body!;

    private static JsonDocument ParseResult(string result)
    {
        try { return JsonDocument.Parse(result); }
        catch (JsonException ex) { throw new InvalidOperationException("技能返回不是合法 JSON：" + ex.Message + "\nRAW=" + result); }
    }

    private static JsonDocument RunDocx(object payload, out string path)
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", outDir);
        try
        {
            var result = NewHost("docx").Run(DocxSource(), JsonSerializer.Serialize(payload), CancellationToken.None, 240_000);
            Assert.DoesNotContain("编译失败", result);
            var doc = ParseResult(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;
            return doc;
        }
        finally { Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", null); }
    }

    private static JsonDocument RunPdf(object payload, out string path)
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PDF_OUT", outDir);
        try
        {
            var result = NewHost("pdf").Run(PdfSource(), JsonSerializer.Serialize(payload), CancellationToken.None, 240_000);
            Assert.DoesNotContain("编译失败", result);
            var doc = ParseResult(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;
            return doc;
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PDF_OUT", null); }
    }

    private static void WithSelfEndpoint(FakeSelfApi api, Action body)
    {
        Environment.SetEnvironmentVariable("AGUI_SELF_BASE", api.BaseUrl);
        Environment.SetEnvironmentVariable("AGUI_SELF_TOKEN", TestToken);
        try { body(); }
        finally
        {
            Environment.SetEnvironmentVariable("AGUI_SELF_BASE", null);
            Environment.SetEnvironmentVariable("AGUI_SELF_TOKEN", null);
        }
    }

    private static void WithoutSelfEndpoint(Action body)
    {
        Environment.SetEnvironmentVariable("AGUI_SELF_BASE", null);
        Environment.SetEnvironmentVariable("AGUI_SELF_TOKEN", null);
        body();
    }

    private static string[] Warnings(JsonDocument doc)
        => doc.RootElement.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
            ? w.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
            : [];

    /// <summary>只看配图相关的告警：PDF 技能的 warnings 里还会带字体选择说明，不能整体断言。</summary>
    private static string[] PhotoWarnings(JsonDocument doc)
        => Warnings(doc).Where(w => w.Contains("配图", StringComparison.Ordinal)).ToArray();

    private static List<string> ZipEntries(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    /// <summary>包里是否真有一份嵌入的位图（OpenXml 的部件路径在不同写法下可能是 media/ 或 word/media/）。</summary>
    private static bool HasEmbeddedImage(string docxPath)
        => ZipEntries(docxPath).Any(e => e.Contains("media/", StringComparison.Ordinal)
            && (e.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || e.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)));

    private static string DocumentXml(string docxPath)
    {
        using var zip = ZipFile.OpenRead(docxPath);
        using var sr = new StreamReader(zip.Entries.First(e => e.FullName == "word/document.xml").Open());
        return sr.ReadToEnd();
    }

    /// <summary>PDF 里是否真的嵌了位图（PDFsharp 写的是未压缩对象字典，找 /Subtype/Image ——
    /// 实测两种写法都可能出现：带空格与不带空格）。</summary>
    private static bool PdfHasImage(string pdfPath)
    {
        var latin = Encoding.Latin1.GetString(File.ReadAllBytes(pdfPath));
        return latin.Contains("/Subtype/Image", StringComparison.Ordinal)
            || latin.Contains("/Subtype /Image", StringComparison.Ordinal);
    }

    /// <summary>本机/容器是否有中文字体（PDF 渲染中文需要；没有就跳过而不是失败）。</summary>
    private static bool HasChineseFont()
        => new[]
        {
            "/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf",
            "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc",
            "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc",
            @"C:\Windows\Fonts\simhei.ttf",
            @"C:\Windows\Fonts\Deng.ttf",
            @"C:\Windows\Fonts\simsun.ttc",
            @"/System/Library/Fonts/PingFang.ttc",
        }.Any(File.Exists);

    private sealed class ChineseFontFactAttribute : FactAttribute
    {
        public ChineseFontFactAttribute()
        {
            if (!HasChineseFont()) Skip = "本机/容器未找到可用的中文字体，跳过依赖真实字体渲染的断言。";
        }
    }

    // ===================== Word =====================

    [Fact]
    public void Docx_ImageQuery_EmbedsLibraryImage()
    {
        var dir = TempDir();
        var png = WriteTinyPng(dir);
        using var api = new FakeSelfApi(ImagesJson(png, "image/png"));
        WithSelfEndpoint(api, () =>
        {
            var payload = new
            {
                title = "带配图的报告",
                imageScopeId = "iscope_docx_test",
                sections = new object[]
                {
                    new { paragraph = "第一段正文。" },
                    new { image = new { imageQuery = "团队会议 白板", caption = "图 1 团队研讨" } },
                },
            };
            var doc = RunDocx(payload, out var path);
            using (doc)
            {
                Assert.Empty(PhotoWarnings(doc));
                Assert.Contains("<w:drawing", DocumentXml(path));
                Assert.True(HasEmbeddedImage(path), "包里应有嵌入的位图：" + string.Join(",", ZipEntries(path)));
            }
            // 回环回调确实打到平台内部接口，并带上了句柄与自令牌
            Assert.Equal("/ag-ui/images/search", api.LastPath);
            Assert.Equal(TestToken, api.LastTokenHeader);
            Assert.Contains("iscope_docx_test", api.LastBody!);
        });
    }

    [Fact]
    public void Docx_ImageQuery_WithoutLibrary_SkipsImageButStillProduces()
    {
        var payload = new
        {
            title = "没有图库的报告",
            sections = new object[]
            {
                new { paragraph = "第一段正文。" },
                new { image = new { imageQuery = "团队会议 白板", caption = "图 1" } },
            },
        };
        JsonDocument? doc = null;
        var path = "";
        WithoutSelfEndpoint(() => doc = RunDocx(payload, out path));
        using (doc)
        {
            // 配图失败不该把稿子带坏：正文照出，只是少一张图 + 一条可读原因
            var warnings = PhotoWarnings(doc!);
            Assert.Single(warnings);
            Assert.Contains("团队会议 白板", warnings[0]);
            Assert.Contains("图库", warnings[0]);
            Assert.DoesNotContain("<w:drawing", DocumentXml(path));
            Assert.False(HasEmbeddedImage(path), "没有图库时不应有嵌入图片");
        }
    }

    /// <summary>图库里允许上传 WebP，而 Word 的 ImagePartType 不认它 —— 必须靠文件头重编成 PNG，
    /// 而不是信扩展名（这里给的就是「名字是 .webp、内容其实是 PNG」的改过名文件）。</summary>
    [Fact]
    public void Docx_ImageQuery_MisnamedImage_IsSniffedNotTrusted()
    {
        var dir = TempDir();
        var odd = WriteTinyPng(dir, "shot.webp");
        using var api = new FakeSelfApi(ImagesJson(odd, "image/webp"));
        WithSelfEndpoint(api, () =>
        {
            var payload = new
            {
                title = "改过名的图",
                imageScopeId = "iscope_docx_test",
                sections = new object[] { new { image = new { imageQuery = "任意关键词" } } },
            };
            RunDocx(payload, out _).Dispose();
        });
    }

    // ===================== PDF =====================

    [ChineseFontFact]
    public void Pdf_ImageQuery_EmbedsLibraryImage()
    {
        var dir = TempDir();
        var png = WriteTinyPng(dir);
        using var api = new FakeSelfApi(ImagesJson(png, "image/png"));
        WithSelfEndpoint(api, () =>
        {
            var payload = new
            {
                title = "带配图的 PDF",
                imageScopeId = "iscope_pdf_test",
                blocks = new object[]
                {
                    new { type = "h1", text = "一、背景" },
                    new { type = "p", text = "正文段落，说明背景。" },
                    new { type = "image", imageQuery = "团队会议 白板", caption = "图 1 团队研讨" },
                },
            };
            var doc = RunPdf(payload, out var path);
            using (doc)
            {
                Assert.Empty(PhotoWarnings(doc));
                Assert.True(PdfHasImage(path), "PDF 里应嵌入位图");
                using var reopen = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                Assert.True(reopen.PageCount >= 2, "封面 + 正文页，实际 " + reopen.PageCount);
            }
            Assert.Equal("/ag-ui/images/search", api.LastPath);
            Assert.Equal(TestToken, api.LastTokenHeader);
            Assert.Contains("iscope_pdf_test", api.LastBody!);
        });
    }

    [ChineseFontFact]
    public void Pdf_ImageQuery_WithoutLibrary_SkipsBlockButStillProduces()
    {
        var payload = new
        {
            title = "没有图库的 PDF",
            blocks = new object[]
            {
                new { type = "p", text = "正文段落。" },
                new { type = "image", imageQuery = "团队会议 白板" },
            },
        };
        JsonDocument? doc = null;
        var path = "";
        WithoutSelfEndpoint(() => doc = RunPdf(payload, out path));
        using (doc)
        {
            var warnings = PhotoWarnings(doc!);
            Assert.Single(warnings);
            Assert.Contains("团队会议 白板", warnings[0]);
            Assert.Contains("图库", warnings[0]);
            Assert.False(PdfHasImage(path), "没有图库时不应有图");
        }
    }

    /// <summary>PDFsharp 只能嵌 PNG/JPEG：图库只有 WebP 时不该硬塞（那会生成损坏的 PDF），
    /// 而应跳过并在 warnings 里说清楚。</summary>
    [ChineseFontFact]
    public void Pdf_ImageQuery_NonPngJpegCandidate_IsReportedNotEmbedded()
    {
        var dir = TempDir();
        var webpish = WriteTinyPng(dir, "shot.webp");
        using var api = new FakeSelfApi(ImagesJson(webpish, "image/webp"));
        WithSelfEndpoint(api, () =>
        {
            var payload = new
            {
                title = "只有 WebP 的图库",
                imageScopeId = "iscope_pdf_test",
                blocks = new object[]
                {
                    new { type = "p", text = "正文段落。" },
                    new { type = "image", imageQuery = "团队会议 白板" },
                },
            };
            var doc = RunPdf(payload, out var path);
            using (doc)
            {
                var warnings = PhotoWarnings(doc);
                Assert.Single(warnings);
                Assert.Contains("PNG/JPEG", warnings[0]);
                Assert.False(PdfHasImage(path));
            }
        });
    }

    /// <summary>检索接口返回空列表（库里有图但都不匹配）→ 同样是“跳过 + 可读原因”。</summary>
    [Fact]
    public void Docx_ImageQuery_NoMatch_SkipsWithReason()
    {
        using var api = new FakeSelfApi(EmptyImagesJson());
        WithSelfEndpoint(api, () =>
        {
            var payload = new
            {
                title = "库里没匹配",
                imageScopeId = "iscope_docx_test",
                sections = new object[] { new { image = new { imageQuery = "不存在的主题" } } },
            };
            var doc = RunDocx(payload, out _);
            using (doc)
            {
                var warnings = PhotoWarnings(doc);
                Assert.Single(warnings);
                Assert.Contains("没有匹配的图片", warnings[0]);
            }
        });
    }
}
