using System.IO.Compression;
using System.Text;
using AguiGroupChat.Web;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>演示文稿「演讲者备注」抽取：按幻灯片顺序、只取备注正文占位符、异常类型一律退化为“无备注”。</summary>
public sealed class PresentationNotesTests
{
    private const string PNs = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>造一个最小 pptx：slides = [(notesPath or null)]，按顺序对应 slide1.xml…，rId 从 rId1 起。</summary>
    internal static string BuildPptx(params string?[] slideNotes)
    {
        var path = Path.Combine(Path.GetTempPath(), "agui-notes-" + Guid.NewGuid().ToString("N")[..8] + ".pptx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var sldIds = new StringBuilder();
        var presRels = new StringBuilder();
        for (var i = 0; i < slideNotes.Length; i++)
        {
            var n = i + 1;
            sldIds.Append($"<p:sldId id=\"{255 + n}\" r:id=\"rId{n}\"/>");
            presRels.Append($"<Relationship Id=\"rId{n}\" Type=\".../slide\" Target=\"slides/slide{n}.xml\"/>");
            Add(zip, $"ppt/slides/slide{n}.xml", "<p:sld xmlns:p=\"x\"/>");
        }
        Add(zip, "ppt/presentation.xml",
            $"<p:presentation xmlns:p=\"{PNs}\" xmlns:r=\"{RNs}\"><p:sldIdLst>{sldIds}</p:sldIdLst></p:presentation>");
        Add(zip, "ppt/_rels/presentation.xml.rels",
            $"<Relationships xmlns=\"{RelNs}\">{presRels}</Relationships>");

        for (var i = 0; i < slideNotes.Length; i++)
        {
            var n = i + 1;
            if (slideNotes[i] is not { } _) continue;
            Add(zip, $"ppt/slides/_rels/slide{n}.xml.rels",
                $"<Relationships xmlns=\"{RelNs}\"><Relationship Id=\"rId1\" Type=\".../notesSlide\" Target=\"../notesSlides/notesSlide{n}.xml\"/></Relationships>");
            Add(zip, $"ppt/notesSlides/notesSlide{n}.xml", NotesXml(slideNotes[i]!));
        }
        return path;
    }

    /// <summary>备注页：一个「页码」占位符（必须被忽略）+ 一个「备注正文」占位符。</summary>
    private static string NotesXml(string body)
        => $"<p:notes xmlns:p=\"{PNs}\" xmlns:a=\"{ANs}\"><p:cSld><p:spTree>" +
           $"<p:sp><p:nvSpPr><p:nvPr><p:ph type=\"sldNum\"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>{body.Length}</a:t></a:r></a:p></p:txBody></p:sp>" +
           $"<p:sp><p:nvSpPr><p:nvPr><p:ph type=\"body\"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>{body}</a:t></a:r></a:p><a:p><a:r><a:t>次段</a:t></a:r></a:p></p:txBody></p:sp>" +
           "</p:spTree></p:cSld></p:notes>";

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var s = zip.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public void Extract_ReturnsNotesInSlideOrderAndSkipsPlaceholders()
    {
        var path = BuildPptx("第一页备注", "第二页备注");
        try
        {
            var notes = PresentationNotes.TryExtract(path);

            Assert.NotNull(notes);
            Assert.Equal(2, notes!.Count);
            Assert.Equal("第一页备注\n次段", notes[0]);   // 页码占位符的数字没有被带上
            Assert.Equal("第二页备注\n次段", notes[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Extract_SlideWithoutNotes_ReturnsEmptyString()
    {
        var path = BuildPptx("有备注", null); // 第二页没有备注页
        try
        {
            var notes = PresentationNotes.TryExtract(path);

            Assert.NotNull(notes);
            Assert.Equal(2, notes!.Count);
            Assert.Equal("有备注\n次段", notes[0]);
            Assert.Equal("", notes[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Extract_NonPptx_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), "agui-" + Guid.NewGuid().ToString("N")[..8] + ".docx");
        File.WriteAllText(path, "not a zip");
        try { Assert.Null(PresentationNotes.TryExtract(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Extract_CorruptPptx_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), "agui-" + Guid.NewGuid().ToString("N")[..8] + ".pptx");
        File.WriteAllText(path, "definitely not a zip");
        try { Assert.Null(PresentationNotes.TryExtract(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Supports_OnlyPptx()
    {
        Assert.True(PresentationNotes.Supports("a.pptx"));
        Assert.False(PresentationNotes.Supports("a.ppt"));
        Assert.False(PresentationNotes.Supports("a.odp"));
        Assert.False(PresentationNotes.Supports("a.pdf"));
    }
}
