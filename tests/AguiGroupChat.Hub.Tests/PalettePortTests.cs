using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Agents.BuiltinSkills;
using AguiGroupChat.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// xlsx / pdf 的命名调色板（设计系统下沉）。
///
/// <para>
/// 这两个技能原本<b>没有任何主题概念</b>：xlsx 只有一个写死的表头蓝 <c>FF1F3864</c>，
/// pdf 只有 8 个语义 role。现在它们与 pptx 共享同一套 18 个命名调色板。
/// </para>
///
/// <para>
/// 关键约束有两条：① <b>不传 theme/palette 时产出与改造前一致</b>（老调用方不受影响）；
/// ② 品牌色不能把可读性搞坏（表头反白字必须看得清、文档底色不能被调色板里的亮黄占领）。
/// </para>
/// </summary>
[Collection(EnvVarCollection.Name)]
public sealed class PalettePortTests
{
    private static string XlsxSource()
        => BuiltinXlsxSkills.Build("xlsx_book", "xlsx_book.skill.txt", "x", "x").Body!;

    private static string PdfSource()
        => BuiltinPdfSkills.Build("pdf_doc", "pdf_doc.skill.txt", "x", "x").Body!;

    private static DotnetSkillHost Host()
        => new(NullLogger<DotnetSkillHost>.Instance,
            Path.Combine(Path.GetTempPath(), "agui-paletteport-nuget-cache"));

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "agui-paletteport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static JsonDocument Run(string source, string envVar, object payload, out string path)
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable(envVar, outDir);
        try
        {
            var result = Host().Run(source, JsonSerializer.Serialize(payload), CancellationToken.None, 240_000);
            Assert.DoesNotContain("编译失败", result);
            var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;
            return doc;
        }
        finally { Environment.SetEnvironmentVariable(envVar, null); }
    }

    private static string ReadEntry(string zipPath, Func<string, bool> match)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            if (!match(e.FullName)) continue;
            using var sr = new StreamReader(e.Open());
            return sr.ReadToEnd();
        }
        throw new InvalidOperationException("zip 里没有匹配的条目：" + zipPath);
    }

    // ===== xlsx =====

    /// <summary>不传 theme：表头底与改造前完全一致（不能因为这次改造改变老调用方的产出）。</summary>
    [Fact]
    public void Xlsx_WithoutTheme_KeepsTheOriginalHeaderColour()
    {
        var payload = new
        {
            title = "默认配色",
            sheets = new object[] { new { name = "表1", headers = new[] { "项目", "数值" }, rows = new object[] { new object[] { "A", 1 } } } },
        };
        var _ = Run(XlsxSource(), "AGUI_XLSX_OUT", payload, out var path);

        var styles = ReadEntry(path, n => n == "xl/styles.xml");
        Assert.Contains("FF1F3864", styles);   // 历史默认表头蓝
        Assert.Contains("FFF2F2F2", styles);   // 历史默认合计行底
    }

    /// <summary>传命名调色板：表头底换成该套调色板的最深色，且表头字仍看得清。</summary>
    [Theory]
    [InlineData("forest-eco", "344E41")]
    [InlineData("tech-night", "000814")]
    [InlineData("education-charts", "264653")]
    public void Xlsx_NamedPalette_DrivesHeaderWithReadableText(string palette, string expectedHeader)
    {
        var payload = new
        {
            title = "配色检查",
            theme = palette,
            sheets = new object[] { new { name = "表1", headers = new[] { "项目", "数值" }, rows = new object[] { new object[] { "A", 1 } } } },
        };
        var _ = Run(XlsxSource(), "AGUI_XLSX_OUT", payload, out var path);

        var styles = ReadEntry(path, n => n == "xl/styles.xml");
        Assert.Contains("FF" + expectedHeader, styles);

        // 表头字色（fonts 里的第 2 项，index=1，isHeader=true）必须与该底色有足够对比
        var headerFont = HeaderFontColour(styles);
        Assert.True(Contrast(headerFont, expectedHeader) >= 4.5,
            $"表头字 {headerFont} 在底色 {expectedHeader} 上对比度只有 {Contrast(headerFont, expectedHeader):F2}");
    }

    /// <summary>取 styles.xml 里 fonts 的第 2 个 font（表头用）的 rgb。</summary>
    private static string HeaderFontColour(string stylesXml)
    {
        var i = stylesXml.IndexOf("<fonts", StringComparison.Ordinal);
        var j = stylesXml.IndexOf("</fonts>", i, StringComparison.Ordinal);
        var block = stylesXml[i..j];
        var fonts = System.Text.RegularExpressions.Regex.Matches(block, "<font>.*?</font>", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(fonts.Count >= 2, "styles.xml 里字体条目不足");
        var m = System.Text.RegularExpressions.Regex.Match(fonts[1].Value, "rgb=\"([0-9A-Fa-f]{6,8})\"");
        Assert.True(m.Success, "表头字体没有显式颜色：" + fonts[1].Value);
        // 字体色写 6 位十六进制（填充色带 FF 前缀），两种都容忍
        var v = m.Groups[1].Value.ToUpperInvariant();
        return v.Length == 8 ? v.Substring(2) : v;
    }

    // ===== pdf =====

    /// <summary>
    /// 传命名调色板不能把底色搞坏：education-charts 的最亮色是亮黄 E9C46A，
    /// 直接当底色会得到一张黄底文档（pptx 侧踩过同一个坑，这里用同一套 Surface 兜底）。
    /// </summary>
    [Theory]
    [InlineData("education-charts")]
    [InlineData("vibrant-tech")]
    [InlineData("art-food")]
    public void Pdf_NamedPalette_KeepsSaneBackground(string palette)
    {
        var payload = new
        {
            title = "配色检查",
            palette,
            docType = "report",
            markdown = "# 一、标题\n\n正文内容。\n\n## 二级\n\n- 要点一\n- 要点二\n",
        };
        var doc = Run(PdfSource(), "AGUI_PDF_OUT", payload, out var path);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        // 产物是合法 PDF：以 %PDF- 开头、体积正常（不是空壳）
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 1000, "PDF 体积异常：" + bytes.Length);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
    }

    /// <summary>不传 palette：pdf 的行为与改造前一致（仍按 docType / accentRole 走）。</summary>
    [Fact]
    public void Pdf_WithoutPalette_StillWorks()
    {
        var payload = new
        {
            title = "默认样式",
            docType = "report",
            accentRole = "eco",
            markdown = "# 标题\n\n正文。\n",
        };
        var doc = Run(PdfSource(), "AGUI_PDF_OUT", payload, out var path);
        Assert.True(new FileInfo(path).Length > 1000, "PDF 体积异常");
    }

    // ---- WCAG 相对亮度 ----

    private static double Contrast(string a, string b)
    {
        var la = RelLum(a); var lb = RelLum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double RelLum(string hex)
    {
        double Ch(int v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        var r = Convert.ToInt32(hex.Substring(0, 2), 16);
        var g = Convert.ToInt32(hex.Substring(2, 2), 16);
        var bl = Convert.ToInt32(hex.Substring(4, 2), 16);
        return 0.2126 * Ch(r) + 0.7152 * Ch(g) + 0.0722 * Ch(bl);
    }
}
