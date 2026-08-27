using System.Text;
using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;
using WText = DocumentFormat.OpenXml.Wordprocessing.Text;
using WParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using SText = DocumentFormat.OpenXml.Spreadsheet.Text;
using DText = DocumentFormat.OpenXml.Drawing.Text;

namespace AguiGroupChat.Hub.Storage;

/// <summary>
/// 常用办公文档文本提取（Hub 扩展）：docx / xlsx / pptx（Office Open XML）与 pdf（PDFsharp，底层为 PdfPig 文本引擎）。
/// 供附件智能体上下文注入使用；任何解析失败均返回 null，不阻断消息链路。
/// </summary>
public static class OfficeTextExtractor
{
    /// <summary>按扩展名分派提取；不支持的扩展名或解析失败返回 null。</summary>
    public static string? Extract(string filePath, string extension)
    {
        try
        {
            // 部分生成器（如 .NET ZipFile.CreateFromDirectory 在 Windows 上）会写出反斜杠分隔的 zip 条目，
            // 违反 OPC 规范（须为正斜杠），导致 OpenXml SDK 认不出任何 part。先做包健康检查并自动修复。
            var path = NormalizeOpcPackage(filePath);
            try
            {
                return extension.ToLowerInvariant() switch
                {
                    ".docx" => ExtractDocx(path),
                    ".xlsx" => ExtractXlsx(path),
                    ".pptx" => ExtractPptx(path),
                    ".pdf" => ExtractPdf(path),
                    _ => null,
                };
            }
            finally
            {
                if (!string.Equals(path, filePath, StringComparison.Ordinal))
                {
                    try { File.Delete(path); } catch { /* 临时副本删除失败不影响结果 */ }
                }
            }
        }
        catch
        {
            // 损坏 / 加密 / 编码异常一律按「无可提取文本」处理
            return null;
        }
    }

    /// <summary>
    /// 检查 OPC 包（zip）是否有反斜杠分隔的条目；有则重建为规范（正斜杠）的临时副本并返回其路径，
    /// 否则返回原路径。只处理 .docx/.xlsx/.pptx（zip 容器），其余直接原样返回。
    /// </summary>
    private static string NormalizeOpcPackage(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is not (".docx" or ".xlsx" or ".pptx")) return filePath;

        using var src = File.OpenRead(filePath);
        using var srcZip = new ZipArchive(src, ZipArchiveMode.Read, leaveOpen: false);
        var hasBackslash = srcZip.Entries.Any(e => e.FullName.Contains('\\'));
        if (!hasBackslash) return filePath;

        var tmp = Path.Combine(Path.GetTempPath(), "opcfix-" + Guid.NewGuid().ToString("N") + ext);
        using (var dst = File.Create(tmp))
        using (var dstZip = new ZipArchive(dst, ZipArchiveMode.Create))
        {
            foreach (var entry in srcZip.Entries)
            {
                var fixedName = entry.FullName.Replace('\\', '/');
                var newEntry = dstZip.CreateEntry(fixedName);
                using var inStream = entry.Open();
                using var outStream = newEntry.Open();
                inStream.CopyTo(outStream);
            }
        }
        return tmp;
    }

    /// <summary>Word：正文与表格内所有段落文本，段落间换行。</summary>
    private static string ExtractDocx(string path)
    {
        var sb = new StringBuilder();
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return sb.ToString();
        foreach (var para in body.Descendants<WParagraph>())
        {
            var line = string.Concat(para.Descendants<WText>().Select(t => t.Text));
            if (line.Length > 0) sb.AppendLine(line);
        }
        return sb.ToString();
    }

    /// <summary>Excel：按工作表输出单元格文本（支持共享字符串 / 内联字符串 / 数值）。</summary>
    private static string ExtractXlsx(string path)
    {
        var sb = new StringBuilder();
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart;
        if (wb is null) return sb.ToString();

        var shared = wb.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>()
            .Select(s => string.Concat(s.Descendants<SText>().Select(t => t.Text)))
            .ToList() ?? [];

        foreach (var sheet in wb.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            sb.AppendLine($"【工作表：{sheet.Name}】");
            if (wb.GetPartById(sheet.Id!) is not WorksheetPart wsPart) continue;
            foreach (var row in wsPart.Worksheet?.Descendants<Row>() ?? [])
            {
                var cells = row.Elements<Cell>().Select(c => CellText(c, shared)).ToList();
                sb.AppendLine(string.Join(" | ", cells));
            }
        }
        return sb.ToString();
    }

    private static string CellText(Cell cell, IReadOnlyList<string> shared)
    {
        var value = cell.InnerText;
        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(value, out var idx) && idx >= 0 && idx < shared.Count)
            return shared[idx];
        return value;
    }

    /// <summary>PowerPoint：所有幻灯片文本框文本。</summary>
    private static string ExtractPptx(string path)
    {
        var sb = new StringBuilder();
        using var doc = PresentationDocument.Open(path, false);
        foreach (var slidePart in doc.PresentationPart?.SlideParts ?? [])
        {
            foreach (var t in slidePart.Slide?.Descendants<DText>() ?? [])
            {
                if (!string.IsNullOrEmpty(t.Text)) sb.AppendLine(t.Text);
            }
        }
        return sb.ToString();
    }

    /// <summary>PDF：逐页提取显示文本（PDFsharp 6 基于 PdfPig 文本引擎）。</summary>
    private static string ExtractPdf(string path)
    {
        var sb = new StringBuilder();
        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        foreach (var page in pdf.Pages)
        {
            var text = ExtractPageText(ContentReader.ReadContent(page));
            if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(text);
        }
        return sb.ToString();
    }

    /// <summary>遍历内容流操作符，收集 Tj / TJ 字符串操作数（含跳格符处理）。</summary>
    private static string ExtractPageText(IEnumerable<CObject> objects)
    {
        var sb = new StringBuilder();
        foreach (var obj in objects)
        {
            if (obj is not COperator cOp) continue;
            if (cOp.Name == "Tj")
            {
                if (cOp.Operands.FirstOrDefault() is CString s) sb.Append(s.Value);
            }
            else if (cOp.Name == "TJ")
            {
                if (cOp.Operands.FirstOrDefault() is CArray arr)
                {
                    foreach (var item in arr)
                    {
                        if (item is CString str) sb.Append(str.Value);
                    }
                }
            }
            else if (cOp.Name == "Td" || cOp.Name == "TD")
            {
                sb.Append(' '); // 位置移动近似为空格，避免相邻文本粘连
            }
        }
        return sb.ToString();
    }
}
