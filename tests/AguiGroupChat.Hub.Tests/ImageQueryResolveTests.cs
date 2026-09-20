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
        private readonly Func<string, string> _respond;

        private volatile string? _path;
        private volatile string? _tokenHeader;
        private volatile string? _body;

        public string BaseUrl { get; }
        public string? LastPath => _path;
        public string? LastTokenHeader => _tokenHeader;
        public string? LastBody => _body;
        /// <summary>按时间顺序记下每一次请求体（验证“二次尝试”这类多次调用）。</summary>
        public List<string> Bodies { get; } = [];

        public FakeSelfApi(string response) : this(_ => response) { }

        /// <summary>可依据请求体决定响应（验证“不同关键词给不同结果”这类场景）。</summary>
        public FakeSelfApi(Func<string, string> respond)
        {
            _respond = respond;
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
            if (_body is { } captured) lock (Bodies) Bodies.Add(captured);

            var payload = Encoding.UTF8.GetBytes(_respond(_body ?? ""));
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

    /// <summary>
    /// 平台检索接口的成功响应（只有自令牌才拿得到的 path 字段在这里一定有）。
    ///
    /// <para>默认 score 用“可信命中”的量级（实测真实命中 0.84~0.88），
    /// 这样默认路径只需一次检索；要验证“低分命中会被本页文字顶掉”时显式传低分。</para>
    /// </summary>
    private static string ImagesJson(string path, string contentType, string caption = "团队会议 白板 讨论", double score = 0.87,
        string? fileName = null)
        => JsonSerializer.Serialize(new
        {
            query = "团队会议",
            count = 1,
            // fileName 默认取路径名；需要验证“回显的是图库原始名（而不是服务器存储名）”时显式传
            images = new[] { new { assetId = "asset_1", libId = "lib_1", libName = "宣传图库", fileName = fileName ?? Path.GetFileName(path), caption, contentType, score, width = 4, height = 4, path } },
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

    /// <summary>
    /// 与 <see cref="WriteTinyPng"/> 同内容但**字节长度不同**（尾部多一段）的 PNG。
    ///
    /// <para>
    /// 用途：验证“最终嵌进去的是哪一张”。docx 的 <c>NormalizeImage</c> 对 PNG **原样嵌入**，
    /// 所以可以拿包里的字节与源文件字节比对 —— 两个候选必须能区分开（长度不同就够）。
    /// 这张只是当“落败候选”，永远不会被嵌入，所以尾部多出的字节不影响解码。
    /// </para>
    /// </summary>
    private static string WriteTinyPngWithPadding(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        var bytes = Convert.FromBase64String(TinyPngBase64);
        var padded = new byte[bytes.Length + 64];
        Array.Copy(bytes, padded, bytes.Length);
        File.WriteAllBytes(p, padded);
        return p;
    }

    /// <summary>包里嵌入的位图字节（docx/pptx 都是 zip，取第一个 media 项）。</summary>
    private static byte[]? EmbeddedImageBytes(string packagePath)
    {
        using var zip = ZipFile.OpenRead(packagePath);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Contains("media/", StringComparison.Ordinal)
            && (e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || e.FullName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)));
        if (entry is null) return null;
        using var ms = new MemoryStream();
        using (var s = entry.Open()) s.CopyTo(ms);
        return ms.ToArray();
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

    private static string PptxSource() => BuiltinPptxSkills.Build("pptx_deck", "pptx_deck.skill.txt", "x", "x").Body!;

    private static JsonDocument RunPptx(object payload, out string path)
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var result = NewHost("pptx").Run(PptxSource(), JsonSerializer.Serialize(payload), CancellationToken.None, 240_000);
            Assert.DoesNotContain("编译失败", result);
            var doc = ParseResult(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;
            return doc;
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
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

    /// <summary>
    /// PPT：模型关键词取不到图时，要用**本页自己的文字**再查一次图库。
    ///
    /// <para>
    /// 实测场景：图库里的描述就是人名（“刘佳俊”），而模型只知道“这页讲颁奖”，写的是
    /// <c>imageQuery:"员工 颁奖 舞台"</c> → 不命中 → 以前直接降级成题图，用户看到的就是
    /// “明明库里有这个人的图、PPT 上也写着他的名字，却没配上”。
    /// </para>
    ///
    /// <para>这里用假平台端点模拟“通用词不命中、人名命中”，并断言两次请求真的都发了。</para>
    /// </summary>
    [Fact]
    public void Pptx_ImageQuery_FallsBackToPageText()
    {
        var dir = TempDir();
        var portrait = WriteTinyPng(dir, "liujiajun.png");
        using var api = new FakeSelfApi(body =>
            body.Contains("刘佳俊", StringComparison.Ordinal)
                ? ImagesJson(portrait, "image/png", "刘佳俊")
                : EmptyImagesJson());
        WithSelfEndpoint(api, () =>
        {
            var payload = new
            {
                title = "颁奖典礼",
                imageScopeId = "iscope_pptx_test",
                slides = new object[]
                {
                    new { type = "image", title = "高效习惯优秀进步奖 · 刘佳俊", imageQuery = "员工 颁奖 舞台" },
                },
            };
            var doc = RunPptx(payload, out var path);
            using (doc)
            {
                // 第一次用模型给的关键词（不命中），第二次用本页文字（带人名，命中）
                lock (api.Bodies)
                {
                    Assert.Equal(2, api.Bodies.Count);
                    Assert.Contains("员工 颁奖 舞台", api.Bodies[0]);
                    Assert.Contains("刘佳俊", api.Bodies[1]);
                }
                // 结果：真的嵌进去了，而且 images[] 里能看到用的是本页文字这条
                using (var zip = ZipFile.OpenRead(path))
                    Assert.Contains(zip.Entries, e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal));
                var used = doc.RootElement.GetProperty("images");
                Assert.Equal(1, used.GetArrayLength());
                Assert.Equal("library", used[0].GetProperty("source").GetString());
                Assert.Contains("刘佳俊", used[0].GetProperty("query").GetString());
                // 配上图了就不能再报“已改用题图”（那是假消息）
                var warnings = doc.RootElement.GetProperty("warnings").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                Assert.DoesNotContain(warnings, w => w.Contains("已改用自动生成的题图", StringComparison.Ordinal));
            }
        });
    }

    /// <summary>
    /// PPT：图库命中的分很低时（只是刚刚过门槛），不该就这么算数 —— 要用<b>本页文字</b>再比一次，谁分高用谁。
    ///
    /// <para>
    /// 实测就是这么翻车的：模型写“员工 颁奖 舞台”，图库那张合影靠描述里的“舞台/宴会厅”蹭到 0.6~0.7，
    /// 于是配了一张不相干的合影；而本页写着“· 刘佳俊”，用它能直查到 0.88。
    /// </para>
    /// </summary>
    [Fact]
    public void Pptx_ImageQuery_LowScoreLibraryHit_LosesToPageText()
    {
        var dir = TempDir();
        var wrong = WriteTinyPng(dir, "family.png");
        var right = WriteTinyPng(dir, "liujiajun.png");
        using var api = new FakeSelfApi(body =>
            body.Contains("刘佳俊", StringComparison.Ordinal)
                ? ImagesJson(right, "image/png", "刘佳俊", 0.88)
                : ImagesJson(wrong, "image/png", "一家三口在宴会厅合影，背景有舞台", 0.68));
        WithSelfEndpoint(api, () =>
        {
            var payload = new
            {
                title = "颁奖典礼",
                imageScopeId = "iscope_pptx_test",
                slides = new object[]
                {
                    new { type = "image", title = "高效习惯优秀进步奖 · 刘佳俊", imageQuery = "员工 颁奖 舞台" },
                },
            };
            var doc = RunPptx(payload, out var path);
            using (doc)
            {
                // 两次检索：低分关键词一次、本页文字一次
                lock (api.Bodies) Assert.Equal(2, api.Bodies.Count);
                // 只用高分那张：落选的那张不该留在“用到的照片”清单里（否则清单在说谎）
                var used = doc.RootElement.GetProperty("images");
                Assert.Equal(1, used.GetArrayLength());
                Assert.Equal("liujiajun.png", used[0].GetProperty("title").GetString());
                Assert.Equal("刘佳俊", used[0].GetProperty("caption").GetString());
                Assert.Contains("刘佳俊", used[0].GetProperty("query").GetString());
                using var zip = ZipFile.OpenRead(path);
                Assert.Contains(zip.Entries, e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal));
            }
        });
    }

    // ===================== Word / PDF：上下文二次尝试 + 显式门槛 =====================

    /// <summary>
    /// Word：图库命中分不高时，要用**该图上下文文字**（图片自己的 caption + 前面最近的文字块）再查一次，谁分高用谁。
    ///
    /// <para>
    /// 实测场景：图库描述就是人名（“刘佳俊”），模型写的是“员工 颁奖 舞台” —— 那张合影靠描述里的
    /// “舞台/宴会厅”蹭到 0.68，而正文里写着“· 刘佳俊”，用它能直查到 0.88。以前只会用 0.68 那张。
    /// </para>
    /// </summary>
    [Fact]
    public void Docx_ImageQuery_LowScoreHit_LosesToContextText()
    {
        var dir = TempDir();
        var wrong = WriteTinyPngWithPadding(dir, "family.png");
        // 右图故意用一个“服务器存储名”当实际路径：回显应给出图库里的原始名 liujiajun.png
        var right = WriteTinyPng(dir, "asset_deadbeef01.png");
        using var api = new FakeSelfApi(body =>
            body.Contains("刘佳俊", StringComparison.Ordinal)
                ? ImagesJson(right, "image/png", "刘佳俊", 0.88, "liujiajun.png")
                : ImagesJson(wrong, "image/png", "一家三口在宴会厅合影，背景有舞台", 0.68));
        WithSelfEndpoint(api, () =>
        {
            var payload = new
            {
                title = "颁奖典礼",
                imageScopeId = "iscope_docx_test",
                sections = new object[]
                {
                    new { heading = "高效习惯优秀进步奖 · 刘佳俊" },          // 上下文文字（人名在这里）
                    new { image = new { imageQuery = "员工 颁奖 舞台" } },   // 模型只给了通用词
                },
            };
            var doc = RunDocx(payload, out var path);
            using (doc)
            {
                // 两次检索：#1 关键词（低分）、#2 上下文文字（带人名）
                lock (api.Bodies)
                {
                    Assert.Equal(2, api.Bodies.Count);
                    Assert.Contains("员工 颁奖 舞台", api.Bodies[0]);
                    Assert.Contains("刘佳俊", api.Bodies[1]);
                    // 显式门槛：不传就是平台默认 0.25（等于不筛）
                    Assert.Contains("\"minScore\":0.6", api.Bodies[0]);
                }
                Assert.Empty(PhotoWarnings(doc));
                // 按字节确认嵌进去的是高分那张（docx 对 PNG 原样嵌入），不是它自己“报”的
                Assert.Equal(File.ReadAllBytes(right), EmbeddedImageBytes(path));
                // 并回显“用了哪条检索词、哪张图（图库原始名）、多少分”
                var used = doc.RootElement.GetProperty("images")[0];
                Assert.Contains("刘佳俊", used.GetProperty("query").GetString());
                Assert.Equal("liujiajun.png", used.GetProperty("fileName").GetString());
                Assert.Equal(0.88, used.GetProperty("score").GetDouble(), 3);
            }
        });
    }

    /// <summary>
    /// PDF：同上。不用字节比对 —— PDFsharp 会把 PNG 重编进 PDF 内容流，字节不再等同源文件，
    /// 所以用“回显 + 确实嵌了图 + 无告警”来判定。
    /// </summary>
    [Fact]
    public void Pdf_ImageQuery_LowScoreHit_LosesToContextText()
    {
        var dir = TempDir();
        var wrong = WriteTinyPng(dir, "family.png");
        var right = WriteTinyPng(dir, "liujiajun.png");
        using var api = new FakeSelfApi(body =>
            body.Contains("刘佳俊", StringComparison.Ordinal)
                ? ImagesJson(right, "image/png", "刘佳俊", 0.88)
                : ImagesJson(wrong, "image/png", "一家三口在宴会厅合影，背景有舞台", 0.68));
        WithSelfEndpoint(api, () =>
        {
            var payload = new
            {
                title = "颁奖典礼",
                imageScopeId = "iscope_pdf_test",
                blocks = new object[]
                {
                    new { type = "h1", text = "高效习惯优秀进步奖 · 刘佳俊" },
                    new { type = "image", imageQuery = "员工 颁奖 舞台" },
                },
            };
            var doc = RunPdf(payload, out var path);
            using (doc)
            {
                lock (api.Bodies)
                {
                    Assert.Equal(2, api.Bodies.Count);
                    Assert.Contains("员工 颁奖 舞台", api.Bodies[0]);
                    Assert.Contains("刘佳俊", api.Bodies[1]);
                    Assert.Contains("\"minScore\":0.6", api.Bodies[0]);
                }
                Assert.Empty(PhotoWarnings(doc));
                Assert.True(PdfHasImage(path), "PDF 里应嵌入位图");
                var used = doc.RootElement.GetProperty("images")[0];
                Assert.Contains("刘佳俊", used.GetProperty("query").GetString());
                Assert.Equal(0.88, used.GetProperty("score").GetDouble(), 3);
            }
        });
    }

    /// <summary>
    /// 关键词已经**高分命中**时不该再多查一次：白耗时间，也会给“分低但看着更亲”的候选翻盘机会。
    /// （图库那次命中 0.88 ≥ 可信线，于是只有 1 次请求。）
    /// </summary>
    [Fact]
    public void Docx_ImageQuery_ConfidentHit_SkipsContextRetry()
    {
        var dir = TempDir();
        var png = WriteTinyPng(dir, "team.png");
        using var api = new FakeSelfApi(ImagesJson(png, "image/png", "团队会议", 0.88));
        WithSelfEndpoint(api, () =>
        {
            var payload = new
            {
                title = "季度报告",
                imageScopeId = "iscope_docx_test",
                sections = new object[]
                {
                    new { heading = "团队协作与会议纪要" },
                    new { image = new { imageQuery = "团队会议 白板" } },
                },
            };
            var doc = RunDocx(payload, out _);
            using (doc)
            {
                Assert.Empty(PhotoWarnings(doc));
                lock (api.Bodies) Assert.Single(api.Bodies);
            }
        });
    }
}
