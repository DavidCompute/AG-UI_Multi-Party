using System.IO.Compression;
using System.Text;
using AguiGroupChat.Web;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 回归（F1）：<c>bldLst</c> 里**残留**的 <c>bldP build="p"</c>（PowerPoint 删除动画后常见）不得让文本框整段被清空。
/// 只有「真的出现在 timing 里」的逐段形状才允许按段留白；否则该形状既不留白也不移除，文字保持可见。
///
/// <para>
/// 用极简手工 pptx：形状 4 被一个 fade 入场动画指向（应被移出底图）、形状 5 只出现在 <c>bldLst</c>（无动画）。
/// 修复前形状 5 会以 <c>paraReveal = 0</c> 被整段留白 → 播放稿里文字消失；修复后应原样保留。
/// </para>
/// </summary>
public sealed class PresentationPlaybackStaleBuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agui-stale-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void StaleBldP_DoesNotEraseTextBoxText()
    {
        Directory.CreateDirectory(_root);
        var pptx = Path.Combine(_root, "stale.pptx");
        WriteMinimalDeck(pptx);
        var outDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(outDir);

        var built = PresentationPlayback.TryBuild(pptx, outDir);
        Assert.NotNull(built);
        var (expandedPath, manifest) = built!.Value;
        Assert.NotNull(expandedPath);

        using var zip = ZipFile.OpenRead(expandedPath!);
        var entry = zip.GetEntry("ppt/slides/slide1.xml");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        var xml = reader.ReadToEnd();

        // 残留 bldLst 指向的文本框：文字必须还在（修复前会被整段清空）
        Assert.Contains("KEEP-可见", xml);
        // 真的被动画的形状：仍被移出底图（精灵模式：底图不含它，由前端精灵补上）
        Assert.DoesNotContain("ANIM-隐藏", xml);
        // 该页仍走精灵模式（有动画；残留的逐段不参与留白也不影响适配性判定）
        Assert.NotNull(manifest.Animations[0]);
    }

    /// <summary>写一份最小可解析的 pptx：一张幻灯片、两个文本框、一段 timing（仅指向形状 4），bldLst 残留指向形状 5。</summary>
    private static void WriteMinimalDeck(string path)
    {
        var entries = new (string Name, string Content)[]
        {
            ("[Content_Types].xml",
             "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
             "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
             "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
             "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
             "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>" +
             "<Override PartName=\"/ppt/slides/slide1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>" +
             "</Types>"),
            ("_rels/.rels",
             "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
             "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
             "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/>" +
             "</Relationships>"),
            ("ppt/presentation.xml",
             "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
             "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
             "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
             "<p:sldSz cx=\"12192000\" cy=\"6858000\"/>" +
             "<p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/></p:sldIdLst>" +
             "</p:presentation>"),
            ("ppt/_rels/presentation.xml.rels",
             "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
             "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
             "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide1.xml\"/>" +
             "</Relationships>"),
            ("ppt/slides/slide1.xml",
             "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
             "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
             "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree>" +
             "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
             "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>" +
             // 形状 4：被动画指向（入场 fade）——底图里应被移除
             "<p:sp><p:nvSpPr><p:cNvPr id=\"4\" name=\"Anim\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
             "<p:spPr><a:xfrm><a:off x=\"1000000\" y=\"1000000\"/><a:ext cx=\"2000000\" cy=\"1000000\"/></a:xfrm></p:spPr>" +
             "<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>ANIM-隐藏</a:t></a:r></a:p></p:txBody></p:sp>" +
             // 形状 5：只在 bldLst 里（无对应 timing）——文字必须保留
             "<p:sp><p:nvSpPr><p:cNvPr id=\"5\" name=\"Stale\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
             "<p:spPr><a:xfrm><a:off x=\"1000000\" y=\"3000000\"/><a:ext cx=\"2000000\" cy=\"1000000\"/></a:xfrm></p:spPr>" +
             "<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>KEEP-可见</a:t></a:r></a:p></p:txBody></p:sp>" +
             "</p:spTree></p:cSld>" +
             "<p:timing>" +
             "<p:bldLst><p:bldP build=\"p\" spid=\"5\"/></p:bldLst>" +
             "<p:tnLst><p:par><p:cTn id=\"1\" nodeType=\"tmRoot\"><p:childTnLst>" +
             "<p:seq><p:cTn id=\"2\" nodeType=\"mainSeq\"><p:childTnLst>" +
             "<p:par><p:cTn id=\"3\" nodeType=\"clickEffect\" presetClass=\"entr\" presetID=\"10\" presetSubtype=\"0\" dur=\"500\"><p:childTnLst>" +
             "<p:animEffect filter=\"fade\"><p:cBhvr><p:cTn id=\"4\" dur=\"500\"/><p:tgtEl><p:spTgt spid=\"4\"/></p:tgtEl></p:cBhvr></p:animEffect>" +
             "</p:childTnLst></p:cTn></p:par>" +
             "</p:childTnLst></p:cTn></p:seq>" +
             "</p:childTnLst></p:cTn></p:par></p:tnLst>" +
             "</p:timing></p:sld>"),
        };

        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var s = e.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            s.Write(bytes, 0, bytes.Length);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }
}
