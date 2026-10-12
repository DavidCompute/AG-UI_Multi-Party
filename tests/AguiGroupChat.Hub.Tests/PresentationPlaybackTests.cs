using System.Text.Json;
using AguiGroupChat.Agents.Tools;
using AguiGroupChat.Web;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 演示文稿「在线播放」的展开与清单：用内置 pptx 技能生成一份**真实带转场/动画**的稿子，
/// 再跑 <see cref="PresentationPlayback.TryBuild"/>，断言清单页序与展开稿结构。
/// 渲染层面的验证（LibreOffice 能否打开展开稿、页数对不对）由手动 E2E 覆盖——这里只保证结构正确。
/// </summary>
[Collection(EnvVarCollection.Name)] // 改 AGUI_PPTX_OUT（进程级）→ 与其它改环境变量的用例串行
public sealed class PresentationPlaybackTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AguiGroupChat.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string SkillSource()
    {
        var path = Path.Combine(RepoRoot(), "tools", "pptx-skills", "pptx_deck.cs");
        Assert.True(File.Exists(path), "找不到技能源文件：" + path);
        return File.ReadAllText(path);
    }

    private static DotnetSkillHost NewHost()
        => new(NullLogger<DotnetSkillHost>.Instance,
            Path.Combine(Path.GetTempPath(), "agui-pptx-skill-nuget-cache")); // 全进程共用一个 NuGet 缓存根

    private static string AnimatedDeckJson() => JsonSerializer.Serialize(new
    {
        title = "播放测试",
        transition = new { preset = "fade", duration = 0.4 },
        slides = new object[]
        {
            new { type = "cover", title = "封面", subtitle = "副标题", animate = new { preset = "flyIn", direction = "bottom" } },
            new { type = "content", title = "要点", bullets = new[] { "甲", "乙", "丙" }, animate = new { preset = "fade", byParagraph = true } },
            new { type = "end", title = "谢谢", subtitle = "完", transition = new { preset = "push", direction = "left" }, animate = "fadeOut" },
        },
    });

    private static string RenderAnimatedDeck()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "agui-play-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var result = NewHost().Run(SkillSource(), AnimatedDeckJson(), CancellationToken.None);
            Assert.DoesNotContain("编译失败", result);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            return doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    [Fact]
    public void Expansion_ProducesMultiPageManifest_AndParseablePackage()
    {
        var deckPath = RenderAnimatedDeck();
        // 先留一份原始稿供人工核对（即使后续断言失败也能拿到）
        try { var art0 = Path.Combine(RepoRoot(), "artifacts", "pptx-play"); Directory.CreateDirectory(art0); File.Copy(deckPath, Path.Combine(art0, "deck.pptx"), true); } catch { /* 忽略 */ }
        var outDir = Path.Combine(Path.GetTempPath(), "agui-play-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);

        var built = PresentationPlayback.TryBuild(deckPath, outDir);
        Assert.NotNull(built);
        var (expandedPath, manifest) = built!.Value;

        // 清单：3 张幻灯片；转场解析到（首张继承全局 fade，末张 push）
        Assert.True(manifest.Expanded, "应生成播放稿（精灵模式底图）");
        Assert.Equal(3, manifest.SlideCount);
        Assert.Equal(3, manifest.Transitions.Count);
        Assert.Equal("fade", manifest.Transitions[0]!.Preset);
        Assert.Equal("push", manifest.Transitions[2]!.Preset);
        Assert.Equal("l", manifest.Transitions[2]!.Direction);

        // 三张幻灯片全部走「精灵模式」→ 播放稿里每张幻灯片恰好 1 页（步数记录在 Steps 里，由前端逐步播）
        Assert.Equal(manifest.SlideCount, manifest.Pages.Count);
        Assert.Equal(Enumerable.Range(0, manifest.SlideCount), manifest.Pages.Select(p => p.Slide));
        Assert.All(manifest.Pages, p => Assert.Equal(0, p.Step));
        // 封面 1 步、要点页 4 步（标题 1 + 正文逐段 3）、结束页 1 步
        Assert.Equal(new[] { 1, 4, 1 }, manifest.Pages.Select(p => p.Steps));

        // 播放稿存在且是结构完好的 pptx：幻灯片部件数 == 清单页数
        Assert.NotNull(expandedPath);
        Assert.True(File.Exists(expandedPath), "播放稿未生成");
        using var pres = PresentationDocument.Open(expandedPath!, false);
        Assert.Equal(manifest.Pages.Count, pres.PresentationPart!.SlideParts.Count());

        // 留一份到 artifacts 供手动渲染核对（LibreOffice 打开 / 转 PDF 页数）
        try
        {
            var art = Path.Combine(RepoRoot(), "artifacts", "pptx-play");
            Directory.CreateDirectory(art);
            File.Copy(deckPath, Path.Combine(art, "deck.pptx"), true);
            File.Copy(expandedPath!, Path.Combine(art, "deck-expanded.pptx"), true);
            File.WriteAllText(Path.Combine(art, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
        }
        catch { /* 仅用于人工核对，失败不影响用例 */ }

        // 方案 C：每张幻灯片都走「精灵模式」（清单带形状几何 + 动画规格），逐段构建也纳入（ParaIndex > 0）
        Assert.NotNull(manifest.Animations);
        Assert.Equal(3, manifest.Animations.Count);
        // 封面：flyIn 标题 → 精灵模式
        Assert.NotNull(manifest.Animations[0]);
        Assert.Contains(manifest.Animations[0]!, (s) => s.Effect == "entr" && s.Preset == "flyIn");
        Assert.All(manifest.Animations[0]!, (s) => Assert.True(s.W > 0 && s.H > 0 && s.W <= 1 && s.H <= 1, "几何应为比例"));
        // 要点页：byParagraph 也走精灵模式——标题是整形状（ParaIndex=0），正文 3 段各带 ParaIndex 1..3
        Assert.NotNull(manifest.Animations[1]);
        var bullets = manifest.Animations[1]!.Where((s) => s.ParaIndex > 0).ToList();
        Assert.Equal(3, bullets.Count);
        Assert.Equal(new[] { 1, 2, 3 }, bullets.Select((s) => s.ParaIndex).OrderBy((i) => i));
        Assert.Contains(manifest.Animations[1]!, (s) => s.ParaIndex == 0);
        // 4 个互不相同的入场步（标题 1 + 正文逐段 3）
        Assert.Equal(4, manifest.Animations[1]!.Where((s) => s.Effect == "entr")
            .Select((s) => s.RevealStep).Distinct().Count());
        // 结束页：只有退出动画 → 精灵模式，且形状一开场就可见（RevealStep=0）
        Assert.NotNull(manifest.Animations[2]);
        Assert.Contains(manifest.Animations[2]!, (s) => s.Effect == "exit" && s.RevealStep == 0);
    }

    [Fact]
    public void WithoutAnimation_NoExpansion_AndSinglePagePerSlide()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "agui-play-plain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        string deckPath;
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "无动画",
                slides = new object[] { new { type = "cover", title = "封面" }, new { type = "content", title = "要点", bullets = new[] { "甲" } } },
            });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            deckPath = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }

        var built = PresentationPlayback.TryBuild(deckPath, outDir);
        Assert.NotNull(built);
        var (expandedPath, manifest) = built!.Value;

        Assert.False(manifest.Expanded);
        Assert.Null(expandedPath); // 无需展开 → 直接转原件
        Assert.Equal(2, manifest.Pages.Count);
        Assert.All(manifest.Pages, p => Assert.Equal(1, p.Steps));
        Assert.Equal(new[] { 0, 1 }, manifest.Pages.Select(p => p.Slide));
    }
}
