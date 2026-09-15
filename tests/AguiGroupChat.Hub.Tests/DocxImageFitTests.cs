using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Agents.BuiltinSkills;
using AguiGroupChat.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// Word 产物的「内容太多不越界」。
///
/// <para>
/// 与 PPT 的处置不同：PPT 的文本框是<b>固定高度</b>，内容多了会画出页面，所以需要估高缩字号；
/// 而 Word 的正文是<b>流式排版</b>，段落自然分页、表格行自动长高，不存在“撑出页面”这回事——
/// 所以 docx 侧<b>不该</b>给正文加缩字号（那只会让正文无谓变小）。
/// 真正会越界的是<b>图片</b>：它有一个固定的 cx/cy，写大了就画到页边距外。
/// </para>
/// </summary>
[Collection(EnvVarCollection.Name)] // 改 AGUI_*_OUT（进程级）→ 串行
public sealed class DocxImageFitTests
{
    private static string SkillSource()
        => BuiltinDocxSkills.Build("docx_report", "docx_report.skill.txt", "x", "x").Body!;

    private static DotnetSkillHost NewHost()
        => new(NullLogger<DotnetSkillHost>.Instance,
            Path.Combine(Path.GetTempPath(), "agui-docx-imgfit-nuget-cache"));

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "agui-docx-imgfit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>跑一遍技能，返回产物路径与 document.xml。</summary>
    private static (string Path, string Xml) RunDocx(object payload)
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", outDir);
        try
        {
            var result = NewHost().Run(SkillSource(), JsonSerializer.Serialize(payload), CancellationToken.None, 180_000);
            Assert.DoesNotContain("编译失败", result);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;
            using var zip = ZipFile.OpenRead(path);
            using var sr = new StreamReader(zip.Entries.First(e => e.FullName == "word/document.xml").Open());
            return (path, sr.ReadToEnd());
        }
        finally { Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", null); }
    }

    [Fact]
    public void OversizedImage_IsScaledIntoTheTextArea()
    {
        var png = Path.Combine(TempDir(), "wide.png");
        File.WriteAllBytes(png, MakePng(2000, 1000));   // 2:1 的宽图

        // widthCm 显式要 24cm —— 超过 A4 正文宽（约 15.9cm），必须被等比缩回正文区
        var (_, xml) = RunDocx(new
        {
            title = "图片越界检查",
            sections = new object[]
            {
                new { heading = "一、宽图" },
                new { image = new { path = png, widthCm = 24.0, caption = "超宽图片应被缩回正文区" } },
            },
        });

        var (textW, textH) = TextAreaEmu(xml);
        var extents = ExtentsEmu(xml);
        Assert.NotEmpty(extents);

        foreach (var (cx, cy) in extents)
        {
            Assert.True(cx <= textW, $"图片宽 {cx} EMU 超过了正文宽 {textW} EMU（会画到页边距外）");
            Assert.True(cy <= textH, $"图片高 {cy} EMU 超过了正文高 {textH} EMU");
        }

        // 等比缩放：宽高比应保持 2:1（2000×1000）
        var (w, h) = extents[0];
        Assert.InRange((double)w / h, 1.9, 2.1);

        // 缩到正文宽就停，不要顺手缩得更小
        Assert.InRange(w, (long)(textW * 0.98), textW);
    }

    /// <summary>
    /// 正文再长也不能被裁掉：Word 是流式排版，段落会自然流到下一页，所以内容必须<b>一字不少</b>。
    /// 这条同时是「docx 不需要正文缩字号」这个判断的凭据（PPT 才需要，因为它的文本框是固定高度）。
    /// </summary>
    [Fact]
    public void HugeBodyText_IsNotClipped()
    {
        const int n = 300;
        var (path, xml) = RunDocx(new
        {
            title = "长正文",
            sections = new object[]
            {
                new { heading = "一、很多段" },
                new { bullets = Enumerable.Range(1, n).Select(i => $"第{i}条要点：这是一段用于把正文拉长的文字。").ToArray() },
            },
        });

        for (var i = 1; i <= n; i++)
            Assert.Contains($"第{i}条要点", xml);

        // 产物仍然是合法 docx（结构完整）
        using var zip = ZipFile.OpenRead(path);
        Assert.Contains(zip.Entries, e => e.FullName == "[Content_Types].xml");
        Assert.Contains(zip.Entries, e => e.FullName == "word/document.xml");
    }

    // ---- 解析 ----

    private static System.Xml.Linq.XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static System.Xml.Linq.XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";

    /// <summary>正文可用宽/高（EMU）。twip → EMU：1 twip = 635 EMU。</summary>
    private static (long W, long H) TextAreaEmu(string documentXml)
    {
        var doc = System.Xml.Linq.XDocument.Parse(documentXml);
        var sz = doc.Descendants(W + "pgSz").First();
        var mar = doc.Descendants(W + "pgMar").First();
        long pageW = long.Parse(sz.Attribute(W + "w")!.Value);
        long pageH = long.Parse(sz.Attribute(W + "h")!.Value);
        long left = long.Parse(mar.Attribute(W + "left")!.Value);
        long right = long.Parse(mar.Attribute(W + "right")!.Value);
        long top = long.Parse(mar.Attribute(W + "top")!.Value);
        long bottom = long.Parse(mar.Attribute(W + "bottom")!.Value);
        return ((pageW - left - right) * 635L, (pageH - top - bottom) * 635L);
    }

    private static List<(long Cx, long Cy)> ExtentsEmu(string documentXml)
    {
        var doc = System.Xml.Linq.XDocument.Parse(documentXml);
        return doc.Descendants(WP + "extent")
            .Select(e => (long.Parse(e.Attribute("cx")!.Value), long.Parse(e.Attribute("cy")!.Value)))
            .ToList();
    }

    // ---- 手写一张合法 PNG（测试项目没有 ImageSharp）----

    private static byte[] MakePng(int w, int h)
    {
        var raw = new byte[h * (1 + w * 3)];
        var p = 0;
        for (var y = 0; y < h; y++)
        {
            raw[p++] = 0;                                   // filter: none
            for (var x = 0; x < w; x++) { raw[p++] = 0x40; raw[p++] = 0x80; raw[p++] = 0xC0; }
        }
        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            idat = ms.ToArray();
        }

        using var outMs = new MemoryStream();
        outMs.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        void Chunk(string type, byte[] data)
        {
            var len = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            outMs.Write(len);
            var t = Encoding.ASCII.GetBytes(type);
            outMs.Write(t);
            outMs.Write(data);
            var crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32([.. t, .. data]));
            outMs.Write(crc);
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), h);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 2;    // color type: truecolor
        Chunk("IHDR", ihdr);
        Chunk("IDAT", idat);
        Chunk("IEND", []);
        return outMs.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }
}
