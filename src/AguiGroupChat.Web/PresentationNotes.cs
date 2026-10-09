using System.IO.Compression;
using System.Xml.Linq;

namespace AguiGroupChat.Web;

/// <summary>
/// 从演示文稿里按**幻灯片顺序**抽取演讲者备注文本，供「在线查看」在每页下方显示。
///
/// <para>
/// 只支持 <c>.pptx</c>（OOXML，本质是 zip）：在 <c>ppt/notesSlides/notesSlideN.xml</c> 里，
/// 通过 <c>ppt/slides/_rels/slideN.xml.rels</c> 的 <c>notesSlide</c> 关系映射到对应幻灯片。
/// <c>.ppt</c>（二进制 OLE）/ <c>.odp</c>（ODF 结构不同）返回 <c>null</c>——前端退化为「只播放幻灯片、不显示备注」，
/// 不影响播放本身。
/// </para>
///
/// <para>
/// 只取「备注正文占位符」（<c>p:sp/p:nvSpPr/p:nvPr/p:ph[@type='body']</c>）里的文字，
/// 避开同页的幻灯片缩略图 / 页码字段（它们也是 <c>p:sp</c>，混进来会显示多余的页码数字）。
/// </para>
/// </summary>
public static class PresentationNotes
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>该文件是否支持抽取备注（按扩展名：仅 .pptx）。</summary>
    public static bool Supports(string path)
        => string.Equals(Path.GetExtension(path), ".pptx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 按幻灯片顺序抽取备注：第 i 项 = 第 i 张幻灯片的备注（无备注为 ""）。
    /// 不支持的类型 / 文件损坏 / 解析失败一律返回 <c>null</c>（调用方据此退化为「不含备注」）。
    /// </summary>
    public static IReadOnlyList<string>? TryExtract(string? sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath) || !Supports(sourcePath)) return null;
        try
        {
            using var zip = ZipFile.OpenRead(sourcePath);

            List<string> order;
            using (var presStream = OpenEntry(zip, "ppt/presentation.xml"))
            {
                if (presStream is null) return null;
                var relById = LoadRels(zip, "ppt/_rels/presentation.xml.rels")
                    .Where(r => r.Id is not null && r.Target is not null)
                    .GroupBy(r => r.Id!, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Target!, StringComparer.Ordinal);

                var doc = XDocument.Load(presStream);
                order = new List<string>();
                foreach (var sldId in doc.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId")
                                        ?? Enumerable.Empty<XElement>())
                {
                    var rid = sldId.Attribute(R + "id")?.Value;
                    if (rid is not null && relById.TryGetValue(rid, out var target))
                        order.Add(target);
                }
            }

            var notes = new List<string>(order.Count);
            foreach (var slideTarget in order)
                notes.Add(ReadSlideNotes(zip, slideTarget));
            return notes;
        }
        catch
        {
            return null; // 损坏 / 非标准包：退化为「无备注」，绝不让备注解析拖垮预览本身
        }
    }

    /// <summary>读某张幻灯片的备注正文（找不到备注页 / 无正文占位符 → ""）。</summary>
    private static string ReadSlideNotes(ZipArchive zip, string slideTarget)
    {
        var (dir, file) = SplitPath(slideTarget);
        var relsPath = dir.Length == 0 ? "_rels/" + file + ".rels" : dir + "/_rels/" + file + ".rels";
        string? notesTarget = null;
        foreach (var (_, type, target) in LoadRels(zip, relsPath))
        {
            if (type is not null && type.EndsWith("/notesSlide", StringComparison.Ordinal))
            {
                notesTarget = target;
                break;
            }
        }
        if (notesTarget is null) return "";

        using var notesStream = OpenEntry(zip, notesTarget);
        if (notesStream is null) return "";
        var doc = XDocument.Load(notesStream);
        foreach (var sp in doc.Descendants(P + "sp"))
        {
            var ph = sp.Descendants(P + "ph").FirstOrDefault();
            if (ph?.Attribute("type")?.Value != "body") continue;
            var paragraphs = sp.Descendants(A + "p")
                .Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value)));
            return string.Join("\n", paragraphs).Trim();
        }
        return "";
    }

    /// <summary>读一份 .rels 部件：返回 (Id, Type, 解析后的目标 zip 路径)。目标可为绝对（以 "/" 开头）或相对。</summary>
    private static List<(string? Id, string? Type, string? Target)> LoadRels(ZipArchive zip, string relsPath)
    {
        var result = new List<(string?, string?, string?)>();
        using var stream = OpenEntry(zip, relsPath);
        if (stream is null) return result;
        var doc = XDocument.Load(stream);
        // .rels 所在部件的目录：ppt/slides/_rels/x.rels → ppt/slides（目标相对该目录解析）
        var partDir = BaseDir(relsPath);
        foreach (var r in doc.Root?.Elements(Rel + "Relationship") ?? Enumerable.Empty<XElement>())
        {
            var target = r.Attribute("Target")?.Value;
            result.Add((r.Attribute("Id")?.Value, r.Attribute("Type")?.Value,
                target is null ? null : ResolveTarget(partDir, target)));
        }
        return result;
    }

    private static Stream? OpenEntry(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase));
        return entry?.Open();
    }

    private static (string Dir, string File) SplitPath(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? ("", path) : (path[..i], path[(i + 1)..]);
    }

    /// <summary>部件目录：去末段，再去 "_rels" 段（若有）。</summary>
    private static string BaseDir(string relsPath)
    {
        var (dir, _) = SplitPath(relsPath);
        var (parent, leaf) = SplitPath(dir);
        return leaf == "_rels" ? parent : dir;
    }

    /// <summary>把 rels 里的 Target 解析成 zip 内路径：绝对（前导 "/"）直接去斜杠；相对则拼接 + 规范化 ".."。</summary>
    private static string ResolveTarget(string baseDir, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var combined = baseDir.Length == 0 ? target : baseDir + "/" + target;
        var segments = new List<string>();
        foreach (var seg in combined.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
            else segments.Add(seg);
        }
        return string.Join("/", segments);
    }
}
