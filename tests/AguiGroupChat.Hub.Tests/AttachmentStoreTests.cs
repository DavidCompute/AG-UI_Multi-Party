using System.Text;
using AguiGroupChat.Hub.Storage;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Xunit;
using WText = DocumentFormat.OpenXml.Wordprocessing.Text;
using WRun = DocumentFormat.OpenXml.Wordprocessing.Run;
using SText = DocumentFormat.OpenXml.Spreadsheet.Text;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 附件存储与办公文档文本提取测试：用 OpenXml / PDFsharp 现场生成 docx / xlsx / pdf，
/// 验证分类（document）与 TryReadTextAsync 的文本提取链路。
/// </summary>
public sealed class AttachmentStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly AttachmentStore _store;

    public AttachmentStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "agui-att-" + Guid.NewGuid().ToString("N")[..8]);
        _store = new AttachmentStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<string?> ExtractAsync(byte[] bytes, string name, string contentType)
    {
        using var ms = new MemoryStream(bytes);
        var info = _store.Save(name, contentType, ms, bytes.Length);
        return await _store.TryReadTextAsync(info.AttachmentId);
    }

    [Fact]
    public void Classify_OfficeDocuments_AreDocumentKindAndExtractable()
    {
        using var ms = new MemoryStream([0x50, 0x4B]); // 占位字节，仅测分类
        foreach (var (name, mime) in new[]
        {
            ("报告.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            ("表格.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            ("演示.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
            ("文档.pdf", "application/pdf"),
        })
        {
            ms.Position = 0;
            var info = _store.Save(name, mime, ms, 2);
            Assert.Equal("document", info.Kind);
            Assert.True(AttachmentStore.IsExtractable(info), $"{name} 应可提取文本");
        }
    }

    [Fact]
    public async Task ExtractText_Txt_StillWorks()
    {
        var bytes = Encoding.UTF8.GetBytes("纯文本内容\n第二行 SKY-2026");
        var text = await ExtractAsync(bytes, "note.txt", "text/plain");
        Assert.Contains("纯文本内容", text);
        Assert.Contains("SKY-2026", text);
    }

    [Fact]
    public async Task ExtractText_Docx_ReturnsParagraphText()
    {
        var bytes = CreateDocx();
        var text = await ExtractAsync(bytes, "需求.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        Assert.Contains("附件测试文档", text);
        Assert.Contains("项目代号：SKY-2026", text);
    }

    [Fact]
    public async Task ExtractText_Docx_BackslashZipEntries_StillExtracts()
    {
        // 模拟 .NET ZipFile.CreateFromDirectory 在 Windows 上的产物：zip 条目用反斜杠分隔，
        // 违反 OPC 规范，OpenXml SDK 无法识别 part——提取器应自动修复后正常提取。
        var bytes = CreateDocxWithBackslashEntries();
        var text = await ExtractAsync(bytes, "生成器.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        Assert.Contains("反斜杠条目测试", text);
    }

    [Fact]
    public async Task ExtractText_Xlsx_ReturnsSharedStringCells()
    {
        var bytes = CreateXlsx();
        var text = await ExtractAsync(bytes, "明细.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        Assert.Contains("项目代号", text);
        Assert.Contains("SKY-2026", text);
    }

    [Fact]
    public async Task ExtractText_Pdf_ReturnsText()
    {
        var bytes = CreatePdf();
        var text = await ExtractAsync(bytes, "简报.pdf", "application/pdf");
        Assert.Contains("AGUI PDF Attachment Test", text);
    }

    [Fact]
    public async Task ExtractText_Pptx_ReturnsText()
    {
        var bytes = CreatePptx();
        var text = await ExtractAsync(bytes, "演示.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation");
        Assert.Contains("AGUI PPTX 附件测试", text);
        Assert.Contains("第二页内容", text);
    }

    private static byte[] CreateDocx()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(new WRun(new WText("附件测试文档"))),
                new Paragraph(new WRun(new WText("项目代号：SKY-2026")))));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static byte[] CreateXlsx()
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new Workbook();
            wb.Workbook.Save();

            var ssp = wb.AddNewPart<SharedStringTablePart>();
            ssp.SharedStringTable = new SharedStringTable(
                new SharedStringItem(new SText("项目代号")),
                new SharedStringItem(new SText("SKY-2026")));
            ssp.SharedStringTable.Save();

            var wsPart = wb.AddNewPart<WorksheetPart>();
            wsPart.Worksheet = new Worksheet(new SheetData(
                new Row(new Cell { DataType = CellValues.SharedString, CellValue = new CellValue("0") }),
                new Row(new Cell { DataType = CellValues.SharedString, CellValue = new CellValue("1") })));
            wsPart.Worksheet.Save();

            wb.Workbook.AppendChild(new Sheets(new Sheet { Id = wb.GetIdOfPart(wsPart), SheetId = 1, Name = "Sheet1" }));
            wb.Workbook.Save();
        }
        return ms.ToArray();
    }

    private static byte[] CreateDocxWithBackslashEntries()
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            void Add(string name, string content)
            {
                var e = zip.CreateEntry(name);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }

            Add(@"[Content_Types].xml", @"<?xml version=""1.0""?><Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types""><Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/><Default Extension=""xml"" ContentType=""application/xml""/><Override PartName=""/word/document.xml"" ContentType=""application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml""/></Types>");
            Add(@"_rels\.rels", @"<?xml version=""1.0""?><Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships""><Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""word/document.xml""/></Relationships>");
            Add(@"word\document.xml", @"<?xml version=""1.0""?><w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main""><w:body><w:p><w:r><w:t>反斜杠条目测试</w:t></w:r></w:p></w:body></w:document>");
        }
        return ms.ToArray();
    }

    private static byte[] CreatePptx()
    {
        using var ms = new MemoryStream();
        using (var doc = PresentationDocument.Create(ms, PresentationDocumentType.Presentation))
        {
            var pres = doc.AddPresentationPart();
            pres.Presentation = new DocumentFormat.OpenXml.Presentation.Presentation();
            pres.Presentation.Save();

            // 第一张：标题 + 正文
            var slide1 = pres.AddNewPart<SlidePart>();
            slide1.Slide = new DocumentFormat.OpenXml.Presentation.Slide(
                new DocumentFormat.OpenXml.Presentation.CommonSlideData(
                    new DocumentFormat.OpenXml.Presentation.ShapeTree(
                        new DocumentFormat.OpenXml.Presentation.Shape(
                            new DocumentFormat.OpenXml.Presentation.NonVisualShapeProperties(
                                new DocumentFormat.OpenXml.Presentation.NonVisualDrawingProperties { Id = 1, Name = "Title" },
                                new DocumentFormat.OpenXml.Presentation.NonVisualShapeDrawingProperties()),
                            new DocumentFormat.OpenXml.Presentation.ShapeProperties(),
                            new DocumentFormat.OpenXml.Presentation.TextBody(
                                new DocumentFormat.OpenXml.Drawing.Paragraph(
                                    new DocumentFormat.OpenXml.Drawing.Run(
                                        new DocumentFormat.OpenXml.Drawing.Text("AGUI PPTX 附件测试"))))))));
            slide1.Slide.Save();

            // 第二张：正文
            var slide2 = pres.AddNewPart<SlidePart>();
            slide2.Slide = new DocumentFormat.OpenXml.Presentation.Slide(
                new DocumentFormat.OpenXml.Presentation.CommonSlideData(
                    new DocumentFormat.OpenXml.Presentation.ShapeTree(
                        new DocumentFormat.OpenXml.Presentation.Shape(
                            new DocumentFormat.OpenXml.Presentation.NonVisualShapeProperties(
                                new DocumentFormat.OpenXml.Presentation.NonVisualDrawingProperties { Id = 1, Name = "Body" },
                                new DocumentFormat.OpenXml.Presentation.NonVisualShapeDrawingProperties()),
                            new DocumentFormat.OpenXml.Presentation.ShapeProperties(),
                            new DocumentFormat.OpenXml.Presentation.TextBody(
                                new DocumentFormat.OpenXml.Drawing.Paragraph(
                                    new DocumentFormat.OpenXml.Drawing.Run(
                                        new DocumentFormat.OpenXml.Drawing.Text("第二页内容"))))))));
            slide2.Slide.Save();
        }
        return ms.ToArray();
    }

    private static byte[] CreatePdf()
    {
        GlobalFontSettings.UseWindowsFontsUnderWindows = true; // PDFsharp 6 跨平台默认无字体解析器，Windows 下启用系统字体
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            using (var gfx = XGraphics.FromPdfPage(page))
            {
                gfx.DrawString("AGUI PDF Attachment Test 2026", new XFont("Arial", 14), XBrushes.Black, new XPoint(72, 100));
            }
            doc.Save(ms);
        }
        return ms.ToArray();
    }
}
