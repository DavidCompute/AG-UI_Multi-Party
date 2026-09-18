using System.IO.Compression;
using System.Text.Json;
using AguiGroupChat.Agents.BuiltinSkills;
using AguiGroupChat.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// PPT 产物「带插图也不会大到挂不上对话」。
///
/// <para>
/// 实测踩到：图库里的图是<b>原图</b>，一张 12MP 手机照 3~12MB，四张就把 .pptx 顶到 21.3MB / 31.2MB，
/// 而平台对附件有大小上限 —— 超了的产物不会挂到对话里，用户看到的现象是
/// “回复说文件生成了、对话里却没有下载入口”（而且平台那处判定原本只打 Debug 日志，线上查不到）。
/// 所以技能侧要在嵌入前把图瘦身：幻灯片根本用不到 4000px 宽的照片。
/// </para>
///
/// <para>本用例用一张真·大图（4000×3000 噪点 JPEG）跑真实执行链路，断言产物里的媒体件确实变小了。</para>
/// </summary>
[Collection(EnvVarCollection.Name)]
public sealed class PptxImageSlimTests
{
    private static string SkillSource()
        => BuiltinPptxSkills.Build("pptx_deck", "pptx_deck.skill.txt", "x", "x").Body!;

    private static DotnetSkillHost NewHost()
        => new(NullLogger<DotnetSkillHost>.Instance,
            Path.Combine(Path.GetTempPath(), "agui-pptx-slim-nuget-cache"));

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "agui-pptx-slim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>造一张“大照片”：4000×3000 噪点。
    /// 注：噪点是 JPEG 最难压的极端情况（真实照片 1920px q85 只有几百 KB），
    /// 所以断言只钉“像素被压到上限以内 + 比原图小”，不钉绝对字节数。</summary>
    private static string WriteBigPhoto(string dir)
    {
        var path = Path.Combine(dir, "big-photo.jpg");
        using var img = new Image<Rgba32>(4000, 3000);
        var rng = new Random(1234);
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
            }
        });
        img.SaveAsJpeg(path, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 92 });
        return path;
    }

    private static (string Path, long Bytes) Run(string json)
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None, 240_000);
            Assert.DoesNotContain("编译失败", result);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;
            return (path, new FileInfo(path).Length);
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    /// <summary>产物里每个媒体件的字节数（ppt/media/*）。</summary>
    private static List<(string Name, long Bytes)> MediaEntries(string pptx)
    {
        using var zip = ZipFile.OpenRead(pptx);
        return zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal))
            .Select(e => (e.FullName, e.Length))
            .ToList();
    }

    [Fact]
    public void HugePhoto_IsSlimmedBeforeEmbedding()
    {
        var dir = TempDir();
        var photo = WriteBigPhoto(dir);
        var srcBytes = new FileInfo(photo).Length;
        Assert.True(srcBytes > 1_200_000,
            $"fixture 应该是一张“大照片”（>1.2MB 才触发瘦身），实际 {srcBytes} 字节");

        // 三页各配同一张大图：以前是原图嵌三份，产物会直接顶到附件上限以上
        var json = JsonSerializer.Serialize(new
        {
            title = "大图瘦身",
            slides = new object[]
            {
                new { type = "image", title = "配图一", path = photo },
                new { type = "image", title = "配图二", path = photo },
                new { type = "content", title = "配图三", bullets = new[] { "要点" }, imageQuery = "" , path = photo },
            },
        });
        var (pptx, total) = Run(json);

        var media = MediaEntries(pptx);
        Assert.True(media.Count >= 1, "产物里应有媒体件");
        var biggest = media.Max(m => m.Bytes);
        // ① 比原图小很多（没瘦身的话会原样嵌进去）
        Assert.True(biggest < srcBytes / 2,
            $"嵌入的大照片没被瘦身：最大媒体件 {biggest} 字节（原图 {srcBytes} 字节）");
        // ② 像素被压到上限以内（这才是“瘦身”的真正判据，与图片内容好不好压无关）
        using (var zip = ZipFile.OpenRead(pptx))
        {
            var entry = zip.Entries.First(e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal) && e.Length == biggest);
            using var s = entry.Open();
            var info = Image.Identify(s);
            var longSide = Math.Max(info.Width, info.Height);
            Assert.True(longSide <= 1920,
                $"嵌入的图没被降到 1920px 以内：{info.Width}×{info.Height}（{entry.FullName}）");
        }
        // ③ 整份稿子不该比一张原图还大
        Assert.True(total < srcBytes,
            $"三页大图产物体积仍然过大：{total} 字节（原图 {srcBytes}；媒体件：{string.Join(", ", media.Select(m => m.Name + "=" + m.Bytes))}）");
        // 仍然要是能打开的文件（媒体件还在，不是被丢掉换成了色块）
        Assert.Contains("ppt/presentation.xml", ZipEntries(pptx));
    }

    [Fact]
    public void SmallImage_IsEmbeddedUnchanged()
    {
        var dir = TempDir();
        var png = Path.Combine(dir, "small.png");
        using (var img = new Image<Rgba32>(64, 64, new Rgba32(200, 30, 30)))
            img.SaveAsPng(png);
        var srcBytes = new FileInfo(png).Length;

        var json = JsonSerializer.Serialize(new
        {
            title = "小图原样",
            slides = new object[] { new { type = "image", title = "小图", path = png } },
        });
        var (pptx, _) = Run(json);
        var media = MediaEntries(pptx);
        Assert.Single(media);
        // 小图不该被重编（重编只会让它变大、还会丢原始像素）
        Assert.Equal(srcBytes, media[0].Bytes);
    }

    private static List<string> ZipEntries(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Select(e => e.FullName).ToList();
    }
}
