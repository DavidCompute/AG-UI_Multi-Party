using System.IO.Compression;
using System.Xml.Linq;

namespace AguiGroupChat.Web;

/// <summary>一张幻灯片的切换（转场）信息；无 &lt;p:transition&gt; 时为 null（等价于「无切换」，前端按硬切处理）。</summary>
public sealed record SlideTransition(string Preset, string? Direction, string? Orientation, string? Spokes, string? Speed, int AdvanceAfterMs, bool AdvanceOnClick);

/// <summary>播放 PDF 的第 i 页归属于哪张幻灯片（<paramref name="Slide"/>，0 基）、是该页的第几个动画步骤（<paramref name="Step"/>，0 基）、该幻灯片共几步（<paramref name="Steps"/>）。</summary>
public sealed record PlayPage(int Slide, int Step, int Steps);

/// <summary>演示文稿在线播放清单：PDF 页 → 幻灯片/步骤 的映射 + 每页备注 + 每张幻灯片的切换。</summary>
public sealed record PlayManifest(
    bool Expanded,
    int SlideCount,
    IReadOnlyList<PlayPage> Pages,
    IReadOnlyList<SlideTransition?> Transitions,
    IReadOnlyList<string> Notes);

/// <summary>播放构建结果：展开后的 pptx 路径（无需展开时为 null——直接转原件）+ 清单 + 失败原因。</summary>
public sealed record PlayDeckOutcome(string? PdfPath, PlayManifest? Manifest, PreviewFailure Failure, string Message)
{
    public bool Ok => PdfPath is not null;
    public static PlayDeckOutcome Success(string pdfPath, PlayManifest manifest) => new(pdfPath, manifest, PreviewFailure.None, "");
    public static PlayDeckOutcome Fail(PreviewFailure failure, string message) => new(null, null, failure, message);
}

/// <summary>
/// 演示文稿「在线播放」支持：
///
/// <para><b>A. 转场</b>——解析每张幻灯片的 <c>&lt;p:transition&gt;</c>（preset / 方向 / 速度 / 自动前进毫秒），
/// 前端据此在两页位图之间做 CSS 过渡。</para>
///
/// <para><b>B. 入场分步</b>——解析 <c>&lt;p:timing&gt;</c> 的 mainSeq：把每个「点击」当成一步；
/// 生成一份「展开稿」= 每张有分步动画的幻灯片被复制成 k 张（第 j 张只显现前 j 步的形状、标题外的段落）。
/// 展开稿由 LibreOffice <b>一次</b>转成多页 PDF —— 前端「页内翻页 = 分步出现」。位移/强调/退出无法用位图表达，退化为「出现」。</para>
///
/// <para>只支持 .pptx（OOXML zip）；解析失败一律降级（不展开 / 无转场），绝不影响普通预览。</para>
/// </summary>
public static class PresentationPlayback
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string SlideContentType = "application/vnd.openxmlformats-officedocument.presentationml.slide+xml";
    private const string SlideRelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide";

    /// <summary>该文件是否支持播放增强（仅 .pptx）。</summary>
    public static bool Supports(string? path)
        => !string.IsNullOrEmpty(path) && string.Equals(Path.GetExtension(path), ".pptx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 构建播放产物：写出「展开稿」到 <paramref name="outDir"/>（无需展开则返回 null）并给出清单。
    /// 非 .pptx / 解析异常 → 返回 null（调用方直接用原件转 PDF）。
    /// </summary>
    public static (string? ExpandedPath, PlayManifest Manifest)? TryBuild(string sourcePath, string outDir)
    {
        if (!Supports(sourcePath)) return null;
        try
        {
            var entries = ReadEntries(sourcePath);
            var slideOrder = ReadSlideOrder(entries);
            if (slideOrder.Count == 0) return null;

            var notes = PresentationNotes.TryExtract(sourcePath) is { } extracted ? new List<string>(extracted) : new List<string>();
            var transitions = new List<SlideTransition?>(slideOrder.Count);
            var variants = new List<(string OrigPath, List<List<string>> Steps, HashSet<string> Para)>(slideOrder.Count);
            foreach (var target in slideOrder)
            {
                var xml = LoadXml(entries, target);
                transitions.Add(xml is null ? null : ParseTransition(xml));
                var (steps, para) = xml is null
                    ? (new List<List<string>>(), new HashSet<string>(StringComparer.Ordinal))
                    : ParseTiming(xml);
                variants.Add((target, steps, para));
            }

            var pages = new List<PlayPage>(slideOrder.Count);
            for (var i = 0; i < variants.Count; i++)
            {
                var stepCount = Math.Max(1, variants[i].Steps.Count);
                for (var s = 0; s < stepCount; s++) pages.Add(new PlayPage(i, s, stepCount));
            }
            NormalizeNotes(notes, slideOrder.Count);

            var expanded = pages.Count > slideOrder.Count || variants.Any(v => v.Steps.Count > 1);
            string? expandedPath = null;
            if (expanded)
                expandedPath = WriteExpanded(sourcePath, outDir, entries, slideOrder, variants);

            var manifest = new PlayManifest(expanded, slideOrder.Count, pages, transitions, notes);
            return (expandedPath, manifest);
        }
        catch
        {
            return null; // 任何异常都降级为「普通预览」——播放增强不该拖垮查看
        }
    }

    private static void NormalizeNotes(List<string> notes, int slideCount)
    {
        while (notes.Count < slideCount) notes.Add("");
        while (notes.Count > slideCount && notes.Count > 0) notes.RemoveAt(notes.Count - 1);
    }

    // ================= 解析：转场 =================

    private static SlideTransition? ParseTransition(XDocument slideXml)
    {
        var tr = slideXml.Root?.Element(P + "transition");
        if (tr is null) return null;
        // preset = 第一个子元素的本地名（fade / push / wipe / cover / dissolve / cut / …）；
        // 方向 / 方向轴 / 轮辐都写在**子元素**上（<p:push dir="l"/>），不是 <p:transition>
        var child = tr.Elements().FirstOrDefault();
        var preset = child?.Name.LocalName ?? "cut";
        string? Attr(string n) => child?.Attribute(n)?.Value ?? tr.Attribute(n)?.Value;
        var advMs = int.TryParse(tr.Attribute("advTm")?.Value, out var ms) ? ms : 0;
        var advClick = !string.Equals(tr.Attribute("advClick")?.Value, "0", StringComparison.Ordinal);
        return new SlideTransition(preset, Attr("dir"), Attr("orient"), Attr("spokes"), tr.Attribute("spd")?.Value, advMs, advClick);
    }

    // ================= 解析：分步（p:timing） =================

    /// <summary>
    /// 解析入场分步：沿 mainSeq 下的效果节点（nodeType=clickEffect/afterEffect/withEffect）按文档顺序收集，
    /// 每一个「入场」效果视为一步（withEffect 并入上一步）；退出 / 强调不产生可见状态变化，不产生步骤。
    /// 返回：步骤列表（每步显现的 spid 集合）+ 逐段构建（build=p）的 spid 集合（来自 timing 根的 p:bldLst）。
    /// </summary>
    private static (List<List<string>> Steps, HashSet<string> ParaSpids) ParseTiming(XDocument slideXml)
    {
        var result = new List<List<string>>();
        var para = new HashSet<string>(StringComparer.Ordinal);
        var timing = slideXml.Root?.Element(P + "timing");
        if (timing is null) return (result, para);

        // 逐段构建声明在 timing 根的 <p:bldLst> 上（不在各步里）
        foreach (var b in timing.Element(P + "bldLst")?.Descendants(P + "bldP") ?? Enumerable.Empty<XElement>())
            if (string.Equals(b.Attribute("build")?.Value, "p", StringComparison.Ordinal)
                && b.Attribute("spid")?.Value is { Length: > 0 } pid)
                para.Add(pid);

        var mainSeq = timing.Descendants(P + "cTn")
            .FirstOrDefault(c => string.Equals(c.Attribute("nodeType")?.Value, "mainSeq", StringComparison.Ordinal));
        if (mainSeq is null) return (result, para);

        // 入场效果 = 一步（退出 / 强调不产生可见状态变化，不计入）
        foreach (var ctn in mainSeq.Descendants(P + "cTn").ToList())
        {
            var nodeType = ctn.Attribute("nodeType")?.Value;
            if (nodeType != "clickEffect" && nodeType != "afterEffect" && nodeType != "withEffect") continue;
            if (!string.Equals(ctn.Attribute("presetClass")?.Value, "entr", StringComparison.Ordinal)) continue;
            var ids = new List<string>();
            foreach (var t in ctn.Descendants(P + "spTgt"))
            {
                var v = t.Attribute("spid")?.Value;
                if (!string.IsNullOrEmpty(v) && !ids.Contains(v)) ids.Add(v);
            }
            if (ids.Count == 0) continue;
            result.Add(ids);
        }
        return (result, para);
    }

    // ================= 展开稿写出 =================

    private static string WriteExpanded(string sourcePath, string outDir, Dictionary<string, byte[]> entries,
        List<string> slideOrder, List<(string OrigPath, List<List<string>> Steps, HashSet<string> Para)> variants)
    {
        Directory.CreateDirectory(outDir);

        // presentation.xml / rels / content-types 都要改：这里读成 XDocument 便于插节点
        var presPath = FindEntry(entries, "ppt/presentation.xml") ?? throw new InvalidOperationException("缺少 presentation.xml");
        var presRelsPath = FindEntry(entries, "ppt/_rels/presentation.xml.rels") ?? throw new InvalidOperationException("缺少 presentation rels");
        var ctPath = FindEntry(entries, "[Content_Types].xml") ?? throw new InvalidOperationException("缺少 [Content_Types].xml");

        var pres = LoadXml(entries, presPath)!;
        var presRels = LoadXml(entries, presRelsPath)!;
        var ct = LoadXml(entries, ctPath)!;
        var sldIdLst = pres.Root?.Element(P + "sldIdLst") ?? throw new InvalidOperationException("缺少 sldIdLst");

        // 每张幻灯片的 rId / sldId 锚点：先快照，避免边插边枚举导致位置错位
        var anchors = sldIdLst.Elements(P + "sldId").ToList();

        var usedSldIds = anchors
            .Select(e => int.TryParse(e.Attribute("id")?.Value, out var v) ? v : 255)
            .DefaultIfEmpty(255).Max();
        var relSeq = 1;
        var partSeq = 1;

        // 逐张幻灯片生成「第 2..k 步」的额外部件（第 1 步复用原部件，只改其 XML）
        for (var i = 0; i < variants.Count; i++)
        {
            var (origPath, steps, para) = variants[i];
            var stepCount = Math.Max(1, steps.Count);
            if (stepCount <= 1) continue;

            var anchor = anchors.ElementAtOrDefault(i);

            for (var s = 1; s < stepCount; s++)
            {
                var variantXml = BuildVariantXml(entries, origPath, steps, para, s + 1); // 第 s+1 步（1 基）
                var partName = $"ppt/slides/slidePlay{partSeq}.xml";
                var relsName = $"ppt/slides/_rels/slidePlay{partSeq}.xml.rels";
                entries[partName] = Serialize(variantXml);
                var relsBytes = BuildVariantRels(entries, origPath);
                if (relsBytes is not null) entries[relsName] = relsBytes;

                var rid = $"rIdPlay{relSeq++}";
                presRels.Root?.Add(new XElement(Rel + "Relationship",
                    new XAttribute("Id", rid), new XAttribute("Type", SlideRelType), new XAttribute("Target", $"slides/slidePlay{partSeq}.xml")));
                ct.Root?.Add(new XElement(Ct + "Override",
                    new XAttribute("PartName", "/" + partName), new XAttribute("ContentType", SlideContentType)));

                var sldId = new XElement(P + "sldId", new XAttribute("id", ++usedSldIds), new XAttribute(R + "id", rid));
                if (anchor is not null) anchor.AddAfterSelf(sldId); else sldIdLst.Add(sldId);

                partSeq++;
            }

            // 第 1 步：原部件改成「只显现第 1 步」的形态
            entries[origPath] = Serialize(BuildVariantXml(entries, origPath, steps, para, 1));
        }

        entries[presPath] = Serialize(pres);
        entries[presRelsPath] = Serialize(presRels);
        entries[ctPath] = Serialize(ct);

        var outPath = Path.Combine(outDir, "play-" + Guid.NewGuid().ToString("N")[..8] + ".pptx");
        using (var fs = File.Create(outPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var es = entry.Open();
                es.Write(bytes, 0, bytes.Length);
            }
        }
        return outPath;
    }

    /// <summary>生成某张幻灯片「第 stepNo 步」（1 基）的 XML：未显现的形状移除；逐段形状按已显现段数留白。</summary>
    private static XDocument BuildVariantXml(Dictionary<string, byte[]> entries, string slidePath,
        List<List<string>> steps, HashSet<string> paraSpids, int stepNo)
    {
        var doc = LoadXml(entries, slidePath)!;
        var spTree = doc.Descendants(P + "spTree").FirstOrDefault();
        if (spTree is null) return doc;

        // 全体动画目标（未揭露的才需要隐藏；未被动画的形状永远可见）
        var allTargets = new HashSet<string>(steps.SelectMany(s => s), StringComparer.Ordinal);
        if (allTargets.Count == 0) return doc;

        // 累计：第 stepNo 步前已显现的形状，以及逐段形状已显现到第几段
        var revealed = new HashSet<string>(StringComparer.Ordinal);
        var paraReveal = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var s = 0; s < Math.Min(stepNo, steps.Count); s++)
            foreach (var id in steps[s])
            {
                revealed.Add(id);
                if (paraSpids.Contains(id)) paraReveal[id] = paraReveal.GetValueOrDefault(id) + 1;
            }

        foreach (var shape in spTree.Elements().ToList())
        {
            var id = ShapeId(shape);
            if (id is null) continue;
            if (paraSpids.Contains(id))
            {
                BlankParagraphsBeyond(shape, paraReveal.GetValueOrDefault(id)); // 逐段：保留形状，未显现的段留白占位
                continue;
            }
            if (allTargets.Contains(id) && !revealed.Contains(id)) shape.Remove(); // 未显现的形状：整块移除
        }
        return doc;
    }

    private static string? ShapeId(XElement shape)
    {
        if (shape.Name.Namespace != P) return null;
        // sp / pic / graphicFrame / grpSp / cxnSp：id 在其 nv*Pr/cNvPr 上
        var nv = shape.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith("Pr", StringComparison.Ordinal) && e.Name.LocalName.StartsWith("nv", StringComparison.Ordinal));
        return nv?.Element(P + "cNvPr")?.Attribute("id")?.Value;
    }

    /// <summary>逐段动画：把第 <paramref name="keep"/> 段之后的段落留白（清空文字 + 去项目符号，保留行高占位）。</summary>
    private static void BlankParagraphsBeyond(XElement shape, int keep)
    {
        var txBody = shape.Element(P + "txBody");
        if (txBody is null) return;
        var ps = txBody.Elements(A + "p").ToList();
        for (var i = keep; i < ps.Count; i++)
        {
            var p = ps[i];
            foreach (var r in p.Elements().Where(e => e.Name == A + "r" || e.Name == A + "br" || e.Name == A + "fld").ToList())
            {
                var t = r.Element(A + "t");
                if (t is not null) t.Value = ""; else r.Remove();
            }
            // 去项目符号：避免空段落仍画出圆点
            var pPr = p.Element(A + "pPr");
            if (pPr is null) { pPr = new XElement(A + "pPr"); p.AddFirst(pPr); }
            pPr.Elements(A + "buChar").Remove();
            pPr.Elements(A + "buAutoNum").Remove();
            if (pPr.Element(A + "buNone") is null) pPr.Add(new XElement(A + "buNone"));
        }
    }

    private static byte[]? BuildVariantRels(Dictionary<string, byte[]> entries, string slidePath)
    {
        var (dir, file) = SplitPath(slidePath);
        var relsPath = dir.Length == 0 ? "_rels/" + file + ".rels" : dir + "/_rels/" + file + ".rels";
        if (FindEntry(entries, relsPath) is null) return null;
        var rels = LoadXml(entries, relsPath)!;
        // 去掉 notesSlide 关系：多张展开页不应共享同一备注页
        foreach (var r in rels.Root?.Elements(Rel + "Relationship").ToList() ?? new List<XElement>())
            if (r.Attribute("Type")?.Value?.EndsWith("/notesSlide", StringComparison.Ordinal) == true) r.Remove();
        return Serialize(rels);
    }

    // ================= zip 读写辅助 =================

    private static Dictionary<string, byte[]> ReadEntries(string path)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(path);
        foreach (var e in zip.Entries)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            map[e.FullName] = ms.ToArray();
        }
        return map;
    }

    private static List<string> ReadSlideOrder(Dictionary<string, byte[]> entries)
    {
        var presPath = FindEntry(entries, "ppt/presentation.xml");
        var relsPath = FindEntry(entries, "ppt/_rels/presentation.xml.rels");
        if (presPath is null || relsPath is null) return new List<string>();
        var relById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in LoadXml(entries, relsPath)!.Root?.Elements(Rel + "Relationship") ?? Enumerable.Empty<XElement>())
        {
            var id = r.Attribute("Id")?.Value; var tgt = r.Attribute("Target")?.Value;
            if (id is not null && tgt is not null) relById[id] = ResolveTarget("ppt", tgt);
        }
        var order = new List<string>();
        foreach (var sldId in LoadXml(entries, presPath)!.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? Enumerable.Empty<XElement>())
        {
            var rid = sldId.Attribute(R + "id")?.Value;
            if (rid is not null && relById.TryGetValue(rid, out var t) && FindEntry(entries, t) is not null) order.Add(t);
        }
        return order;
    }

    private static XDocument? LoadXml(Dictionary<string, byte[]> entries, string path)
    {
        var actual = FindEntry(entries, path);
        if (actual is null) return null;
        using var ms = new MemoryStream(entries[actual]);
        return XDocument.Load(ms);
    }

    private static string? FindEntry(Dictionary<string, byte[]> entries, string path)
        => entries.ContainsKey(path) ? path : entries.Keys.FirstOrDefault(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase));

    private static byte[] Serialize(XDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, SaveOptions.DisableFormatting);
        return ms.ToArray();
    }

    private static (string Dir, string File) SplitPath(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? ("", path) : (path[..i], path[(i + 1)..]);
    }

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
