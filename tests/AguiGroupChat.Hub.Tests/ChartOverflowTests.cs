using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Agents.BuiltinSkills;
using AguiGroupChat.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 图表渲染器的「内容太多不越界」回归（pptx 与 docx 两侧）。
///
/// <para>
/// 把压力输入（200 字标题、30 个 20 字分类、4 个长名系列、40 项饼图图例）经真实
/// <see cref="DotnetSkillHost"/> 各跑一遍，再从产物里抠出图表 PNG，
/// <b>逐像素求墨迹包围盒</b>，断言墨迹四周仍留有空边——即没有任何内容被画到画布外。
/// </para>
///
/// <para>
/// 判据：白底图上任何非白像素都算墨迹；只要包围盒贴到画布边缘，就说明有内容越界
/// （ImageSharp 会静默裁到边界，恰好留下“贴边”的痕迹）。
/// 未修复时实测：docx 柱/折线图右边界贴到 x=899，饼图图例同时贴到右边与底边。
/// </para>
///
/// <para>
/// 本测试项目没有 ImageSharp，故这里手写一个最小 PNG 解码器（IDAT 是 zlib 流，
/// 用 <see cref="ZLibStream"/> 解压 + 逐行反滤波）。报告写在临时目录，便于失败时看实际数值。
/// </para>
///
/// <para>
/// 注意：两侧都必须用<b>浅色主题</b>——本判据把“非白”当墨迹，深色底图整张都算墨迹。
/// </para>
/// </summary>
[Collection(EnvVarCollection.Name)] // 改 AGUI_*_OUT（进程级）→ 串行
public sealed class ChartOverflowTests
{
    private const int Margin = 2;

    // ==== docx 侧 ====

    private static string DocxSkillSource()
        => BuiltinDocxSkills.Build("docx_report", "docx_report.skill.txt", "x", "x").Body!;

    [Fact]
    public void Docx_StressCharts_KeepAllInkInsideCanvas()
    {
        var stress = BuildStress();
        var payload = new
        {
            title = "图表越界压力测试",
            sections = new object[]
            {
                new { heading = "一、柱状图", level = 1 },
                new { chart = new { type = "bar", title = stress.LongTitle, yLabel = "单位：这是一个很长的纵轴单位说明文字", categories = stress.Cats, series = stress.Series } },
                new { chart = new { type = "line", title = stress.LongTitle, yLabel = "纵轴单位", categories = stress.Cats, series = stress.Series } },
                new { chart = new { type = "pie", title = stress.LongTitle, categories = stress.PieCats, values = stress.PieValues } },
            },
        };

        var outDir = TempDir("agui-docxchart-out-");
        Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", outDir);
        try
        {
            var result = NewHost().Run(DocxSkillSource(), JsonSerializer.Serialize(payload), CancellationToken.None, 180_000);
            AssertNoInkOutsideCanvas(result, "docx-chart-bbox.txt", expectCharts: 3);
        }
        finally { Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", null); }
    }

    // ==== pptx 侧 ====

    private static string PptxSkillSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AguiGroupChat.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "tools", "pptx-skills", "pptx_deck.cs");
        Assert.True(File.Exists(path), "找不到技能源文件：" + path);
        return File.ReadAllText(path);
    }

    [Fact]
    public void Pptx_StressCharts_KeepAllInkInsideCanvas()
    {
        var stress = BuildStress();
        // 不传 theme：用默认浅色主题（深色底图会让“非白即墨”的判据整张命中）
        var payload = new
        {
            title = "图表越界压力测试",
            slides = new object[]
            {
                new { type = "cover", title = "图表越界压力测试" },
                new { type = "chart", title = stress.LongTitle, chartType = "bar", yLabel = "单位：这是一个很长的纵轴单位说明文字", categories = stress.Cats, series = stress.Series },
                new { type = "chart", title = stress.LongTitle, chartType = "line", yLabel = "纵轴单位", categories = stress.Cats, series = stress.Series },
                new { type = "chart", title = stress.LongTitle, chartType = "pie", categories = stress.PieCats, series = new[] { new { name = "占比", values = stress.PieValues } } },
            },
        };

        var outDir = TempDir("agui-pptxchart-out-");
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var result = NewHost().Run(PptxSkillSource(), JsonSerializer.Serialize(payload), CancellationToken.None, 180_000);
            AssertNoInkOutsideCanvas(result, "pptx-chart-bbox.txt", expectCharts: 3);
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    // ==== 饼图必须真的画成实心扇形 ====

    /// <summary>
    /// 饼图不能只看“有没有越界”——实测踩到过更隐蔽的失败：扇区被填成「弧 + 弦」的弓形，
    /// 面积几乎为 0，整张图只剩贴外缘的一圈发丝线，却完全在画布内，越界断言一律通过。
    /// 所以这里单独断言<b>彩色像素占比</b>：真圆盘应占画布约 27%，坏掉时只有 1% 上下。
    /// </summary>
    private const double MinPieFill = 0.20;

    [Fact]
    public void Pptx_PieChart_DrawsFilledDisk()
    {
        var payload = new
        {
            title = "饼图填充检查",
            slides = new object[] { PieSlide() },
        };

        var outDir = TempDir("agui-pptxpie-out-");
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var result = NewHost().Run(PptxSkillSource(), JsonSerializer.Serialize(payload), CancellationToken.None, 180_000);
            AssertChartDiskIsFilled(result, "pptx-pie-fill.txt");
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    [Fact]
    public void Docx_PieChart_DrawsFilledDisk()
    {
        var payload = new
        {
            title = "饼图填充检查",
            sections = new object[] { PieSection() },
        };

        var outDir = TempDir("agui-docxpie-out-");
        Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", outDir);
        try
        {
            var result = NewHost().Run(DocxSkillSource(), JsonSerializer.Serialize(payload), CancellationToken.None, 180_000);
            AssertChartDiskIsFilled(result, "docx-pie-fill.txt");
        }
        finally { Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", null); }
    }

    private static object PieSlide() => new
    {
        type = "chart",
        title = "饼图填充检查",
        chartType = "pie",
        categories = new[] { "甲", "乙", "丙", "丁" },
        series = new[] { new { name = "占比", values = new[] { 40.0, 30.0, 20.0, 10.0 } } },
    };

    private static object PieSection() => new
    {
        chart = new
        {
            type = "pie",
            title = "饼图填充检查",
            categories = new[] { "甲", "乙", "丙", "丁" },
            values = new[] { 40.0, 30.0, 20.0, 10.0 },
        },
    };

    private static void AssertChartDiskIsFilled(string skillResult, string reportName)
    {
        using var doc = JsonDocument.Parse(skillResult);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), skillResult);
        var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

        using var zip = ZipFile.OpenRead(path);
        var media = zip.Entries.Where(e => e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(media);

        using var s = media[0].Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var img = DecodePng(ms.ToArray());
        var ratio = ColouredRatio(img);

        var reportPath = Path.Combine(Path.GetTempPath(), reportName);
        File.WriteAllText(reportPath,
            $"canvas={img.Width}x{img.Height} coloured={ratio:P2} threshold={MinPieFill:P0}"
            + Environment.NewLine + (ratio >= MinPieFill ? "RESULT=PASS" : "RESULT=FAIL"));

        Assert.True(ratio >= MinPieFill,
            $"饼图圆盘没有被填成实心扇形：彩色像素占比 {ratio:P2}，期望 ≥ {MinPieFill:P0}。"
            + "（典型成因：PathBuilder 只 AddArc，得到的是弓形而非扇形）报告见 " + reportPath);
    }

    /// <summary>“明显带色”的像素占比：最大通道与最小通道相差 &gt; 40，用以把调色板扇区/柱体与灰阶文字区分开。</summary>
    private static double ColouredRatio(Raster r)
    {
        long coloured = 0;
        for (var i = 0; i < r.Rgba.Length; i += 4)
        {
            var mx = Math.Max(r.Rgba[i], Math.Max(r.Rgba[i + 1], r.Rgba[i + 2]));
            var mn = Math.Min(r.Rgba[i], Math.Min(r.Rgba[i + 1], r.Rgba[i + 2]));
            if (mx - mn > 40) coloured++;
        }
        return (double)coloured / (r.Width * r.Height);
    }

    // ==== 共用 ====

    private sealed record Stress(
        string LongTitle, string[] Cats,
        object[] Series, string[] PieCats, double[] PieValues);

    /// <summary>压力输入：标题 200 字；30 个分类各 20 字；4 个系列名 20+ 字且数值量级很大（考验刻度左边距）；饼图 40 项。</summary>
    private static Stress BuildStress()
    {
        var longTitle = new string('长', 200);
        var cats = Enumerable.Range(0, 30).Select(i => "分类" + i.ToString("00") + new string('字', 18)).ToArray();
        var names = Enumerable.Range(0, 4).Select(i => "系列名称很长需要换行排版的第" + i + "组数据").ToArray();
        var series = Enumerable.Range(0, 4).Select(s => new
        {
            name = names[s],
            values = Enumerable.Range(0, 30).Select(c => 1234567.0 + s * 1000 + c * 37).ToArray(),
        }).Cast<object>().ToArray();
        var pieCats = Enumerable.Range(0, 40).Select(i => "饼图分类项第" + i + "个很长很长的名字").ToArray();
        var pieValues = Enumerable.Range(0, 40).Select(i => (double)(i + 1)).ToArray();
        return new Stress(longTitle, cats, series, pieCats, pieValues);
    }

    /// <summary>跑完技能后：抠出产物里的图表 PNG，断言墨迹没贴到画布边缘。</summary>
    private static void AssertNoInkOutsideCanvas(string skillResult, string reportName, int expectCharts)
    {
        using var doc = JsonDocument.Parse(skillResult);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), skillResult);
        var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

        using var zip = ZipFile.OpenRead(path);
        // docx 的图片部件路径是 media/imageN.png（不是 word/media/），故按后缀匹配
        var media = zip.Entries.Where(e => e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.Ordinal).ToList();
        Assert.Equal(expectCharts, media.Count);

        var report = new StringBuilder();
        report.AppendLine("file=" + Path.GetFileName(path));
        report.AppendLine("chartFont=" + doc.RootElement.GetProperty("chartFont").GetString()
            + " cjk=" + doc.RootElement.GetProperty("chartFontCjk").GetBoolean());
        var failures = new List<string>();
        foreach (var entry in media)
        {
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var img = DecodePng(ms.ToArray());
            var (minX, minY, maxX, maxY) = InkBounds(img);
            var line = $"{entry.FullName}: canvas={img.Width}x{img.Height} ink=({minX},{minY})-({maxX},{maxY})"
                + $" margins=L{minX} T{minY} R{img.Width - 1 - maxX} B{img.Height - 1 - maxY}"
                + $" coloured={ColouredRatio(img):P2}";
            report.AppendLine(line);

            if (minX < Margin || minY < Margin || maxX > img.Width - 1 - Margin || maxY > img.Height - 1 - Margin)
                failures.Add(line);
        }
        report.AppendLine(failures.Count == 0 ? "RESULT=PASS" : "RESULT=FAIL");
        var reportPath = Path.Combine(Path.GetTempPath(), reportName);
        File.WriteAllText(reportPath, report.ToString());

        Assert.True(failures.Count == 0,
            "图表墨迹越出画布（贴边）：\n" + string.Join("\n", failures) + "\n完整报告见 " + reportPath);
    }

    /// <summary>NuGet 缓存根<b>全进程共用一个</b>：按用例新建会把依赖全量重下，跑久了吃满磁盘。</summary>
    private static DotnetSkillHost NewHost()
        => new(NullLogger<DotnetSkillHost>.Instance,
            Path.Combine(Path.GetTempPath(), "agui-chartoverflow-nuget-cache"));

    private static string TempDir(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // ===== 最小 PNG 解码（8bit，非隔行；ImageSharp 默认输出）=====

    private sealed record Raster(int Width, int Height, byte[] Rgba);

    private static (int MinX, int MinY, int MaxX, int MaxY) InkBounds(Raster r)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < r.Height; y++)
            for (var x = 0; x < r.Width; x++)
            {
                var i = (y * r.Width + x) * 4;
                // 白底之外的一切都算墨迹（含抗锯齿灰边）
                if (r.Rgba[i] >= 250 && r.Rgba[i + 1] >= 250 && r.Rgba[i + 2] >= 250) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        Assert.True(maxX >= 0, "图表整张是纯白，没画出任何内容");
        return (minX, minY, maxX, maxY);
    }

    private static Raster DecodePng(byte[] png)
    {
        Assert.Equal(0x89, png[0]); // PNG 签名
        int pos = 8, width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        var idat = new MemoryStream();
        var typeBuf = new byte[4];
        while (pos + 8 <= png.Length)
        {
            var len = BeInt(png, pos);
            Array.Copy(png, pos + 4, typeBuf, 0, 4);
            var type = Encoding.ASCII.GetString(typeBuf);
            var data = pos + 8;
            if (type == "IHDR")
            {
                width = BeInt(png, data);
                height = BeInt(png, data + 4);
                bitDepth = png[data + 8];
                colorType = png[data + 9];
                interlace = png[data + 12];
            }
            else if (type == "IDAT") idat.Write(png, data, len);
            else if (type == "IEND") break;
            pos = data + len + 4;
        }
        Assert.Equal(8, bitDepth);
        Assert.Equal(0, interlace); // 隔行扫描不支持（我们只处理自己生成的图）
        var channels = colorType switch
        {
            0 => 1, 2 => 3, 4 => 2, 6 => 4,
            _ => throw new NotSupportedException("unexpected PNG colorType=" + colorType),
        };

        idat.Position = 0;
        using var inflated = new ZLibStream(idat, CompressionMode.Decompress);
        using var rawMs = new MemoryStream();
        inflated.CopyTo(rawMs);
        var raw = rawMs.ToArray();

        var stride = width * channels;
        var px = new byte[width * height * 4];
        var prev = new byte[stride];
        var cur = new byte[stride];
        var p = 0;
        for (var y = 0; y < height; y++)
        {
            var filter = raw[p++];
            Array.Copy(raw, p, cur, 0, stride);
            p += stride;
            for (var i = 0; i < stride; i++)
            {
                int a = i >= channels ? cur[i - channels] : 0;
                int b = prev[i];
                int c = i >= channels ? prev[i - channels] : 0;
                cur[i] = filter switch
                {
                    0 => cur[i],
                    1 => (byte)(cur[i] + a),
                    2 => (byte)(cur[i] + b),
                    3 => (byte)(cur[i] + (a + b) / 2),
                    4 => (byte)(cur[i] + Paeth(a, b, c)),
                    _ => throw new NotSupportedException("unexpected PNG filter=" + filter),
                };
            }
            for (var x = 0; x < width; x++)
            {
                int si = x * channels, di = (y * width + x) * 4;
                switch (channels)
                {
                    case 1: px[di] = px[di + 1] = px[di + 2] = cur[si]; px[di + 3] = 255; break;
                    case 2: px[di] = px[di + 1] = px[di + 2] = cur[si]; px[di + 3] = cur[si + 1]; break;
                    case 3: px[di] = cur[si]; px[di + 1] = cur[si + 1]; px[di + 2] = cur[si + 2]; px[di + 3] = 255; break;
                    default: Array.Copy(cur, si, px, di, 4); break;
                }
            }
            Array.Copy(cur, prev, stride);
        }
        return new Raster(width, height, px);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static int BeInt(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
}
