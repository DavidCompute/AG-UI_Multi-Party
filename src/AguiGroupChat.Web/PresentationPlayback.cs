using System.IO.Compression;
using System.Xml.Linq;

namespace AguiGroupChat.Web;

/// <summary>一张幻灯片的切换（转场）信息；无 &lt;p:transition&gt; 时为 null（等价于「无切换」，前端按硬切处理）。</summary>
public sealed record SlideTransition(string Preset, string? Direction, string? Orientation, string? Spokes, string? Speed, int AdvanceAfterMs, bool AdvanceOnClick);

/// <summary>播放 PDF 的第 i 页归属于哪张幻灯片（<paramref name="Slide"/>，0 基）、是该页的第几个步骤（<paramref name="Step"/>，0 基）、该幻灯片共几步（<paramref name="Steps"/>）。</summary>
public sealed record PlayPage(int Slide, int Step, int Steps);

/// <summary>
/// 一张幻灯片里被动画的**形状**（方案 C：前端以「底图 + 形状精灵」播放真动画）。
/// <paramref name="X"/>/<paramref name="Y"/>/<paramref name="W"/>/<paramref name="H"/> 是相对幻灯片尺寸的比例（0..1），
/// 前端据此在底图上定位精灵（精灵像素由「普通页 ⊖ 底图页」在同一矩形内取差得到）。
/// <param name="RevealStep">：1 基的入场步序；0 = 一开场就可见（只有退出 / 强调动画）。
/// <paramref name="EffectStep"/>：该效果**本身**在第几步播放（入场 / 退出 / 强调都用它定时）。
/// <paramref name="ParaIndex"/>：逐段构建里的段序（1 基）；0 = 不是逐段（整形状）。
/// <paramref name="Effect"/>：entr / exit / emph；<paramref name="Preset"/>：flyIn/fade/wipe/spin/pulse/fillColor/appear…
/// </summary>
public sealed record AnimatedShape(
    int Spid, int RevealStep, int EffectStep, int ParaIndex,
    double X, double Y, double W, double H,
    string Kind, string Effect, string Preset, string? Direction,
    int DurationMs, int DelayMs);

/// <summary>
/// 演示文稿在线播放清单：PDF 页 → 幻灯片/步骤 的映射 + 每页备注 + 每张幻灯片的切换。
/// <paramref name="Animations"/>：第 i 张幻灯片的形状动画清单；**非 null ⇒ 该页为「精灵模式」**
/// （播放 PDF 里该幻灯片只有 1 页 = 移除全部被动画形状的底图，前端在底图上按步播放精灵动画）；
/// null ⇒ 「分步模式」（播放 PDF 已把该页展开成多页，逐页翻即分步出现，兼容逐段构建等无法精灵化的情形）。
/// </summary>
public sealed record PlayManifest(
    bool Expanded,
    int SlideCount,
    IReadOnlyList<PlayPage> Pages,
    IReadOnlyList<SlideTransition?> Transitions,
    IReadOnlyList<string> Notes,
    IReadOnlyList<IReadOnlyList<AnimatedShape>?> Animations);

/// <summary>播放构建结果：展开稿路径（无需变化时为 null——直接转原件）+ 清单 + 失败原因。</summary>
public sealed record PlayDeckOutcome(string? PdfPath, PlayManifest? Manifest, PreviewFailure Failure, string Message)
{
    public bool Ok => PdfPath is not null;
    public static PlayDeckOutcome Success(string pdfPath, PlayManifest manifest) => new(pdfPath, manifest, PreviewFailure.None, "");
    public static PlayDeckOutcome Fail(PreviewFailure failure, string message) => new(null, null, failure, message);
}

/// <summary>
/// 演示文稿「在线播放」支持（无需商业组件）：
///
/// <para><b>A. 转场</b>——解析每张幻灯片的 <c>&lt;p:transition&gt;</c>，前端在两页位图之间做 CSS 过渡。</para>
///
/// <para><b>B. 分步（逐段构建等）</b>——解析 <c>&lt;p:timing&gt;</c>，把一页复制成 k 张（第 j 张只显现前 j 步的形状、
/// 逐段文本框按已显现段数留白），LibreOffice 一次转成多页 PDF；前端「页内翻页 = 分步出现」。</para>
///
/// <para><b>C. 精灵（真动画）</b>——对只有「整形状」动画的页：只输出 1 页**底图**（移除全部被动画形状），
/// 并把被动画形状的几何与动画规格写进清单；前端用「普通页 ⊖ 底图页」取差得到**透明精灵**，
/// 再对精灵播真动画（位移 / 淡入淡出 / 旋转 / 缩放 / 变色）。因为精灵像素来自同一套渲染，
/// 静止时与底图**像素级对齐**，且不依赖前端字体度量。</para>
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
    /// 构建播放产物：写出「播放稿」到 <paramref name="outDir"/>（无需变化则返回 null）并给出清单。
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
            var (sldW, sldH) = ReadSlideSize(entries);

            var notes = PresentationNotes.TryExtract(sourcePath) is { } extracted ? new List<string>(extracted) : new List<string>();
            var transitions = new List<SlideTransition?>(slideOrder.Count);
            var pages = new List<PlayPage>();
            var animations = new List<IReadOnlyList<AnimatedShape>?>();
            var variants = new List<Variant>(slideOrder.Count);
            var changed = false;

            for (var i = 0; i < slideOrder.Count; i++)
            {
                var target = slideOrder[i];
                var xml = LoadXml(entries, target);
                transitions.Add(xml is null ? null : ParseTransition(xml));
                var anim = xml is null ? Anim.Empty : ParseAnimations(xml, sldW, sldH);
                // 精灵模式：有动画、几何可解析，且逐段形状均无填充无边线（可把它按行带切精灵）
                var sprite = anim.StepCount > 0 && anim.Shapes.Count > 0 && anim.ParaEligible;
                variants.Add(new Variant(target, anim.Steps, anim.ParaSpids, sprite));
                if (sprite)
                {
                    // 精灵模式：该页在播放 PDF 里只有 1 页（底图），但动画步数仍是 StepCount
                    pages.Add(new PlayPage(i, 0, Math.Max(1, anim.StepCount)));
                    animations.Add(anim.Shapes);
                    changed = true;
                }
                else
                {
                    var stepCount = Math.Max(1, anim.StepCount);
                    for (var s = 0; s < stepCount; s++) pages.Add(new PlayPage(i, s, stepCount));
                    animations.Add(null);
                    if (stepCount > 1) changed = true;
                }
            }
            NormalizeNotes(notes, slideOrder.Count);

            string? expandedPath = changed ? WriteDeck(sourcePath, outDir, entries, variants) : null;
            var manifest = new PlayManifest(changed, slideOrder.Count, pages, transitions, notes, animations);
            return (expandedPath, manifest);
        }
        catch
        {
            return null; // 任何异常都降级为「普通预览」——播放增强不该拖垮查看
        }
    }

    private readonly record struct Variant(string OrigPath, List<List<string>> Steps, HashSet<string> Para, bool Sprite);

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

    // ================= 解析：动画（p:timing） =================

    private readonly record struct Anim(List<List<string>> Steps, HashSet<string> ParaSpids, List<AnimatedShape> Shapes, int StepCount, bool ParaEligible)
    {
        public static readonly Anim Empty = new(new List<List<string>>(), new HashSet<string>(), new List<AnimatedShape>(), 0, true);
    }

    /// <summary>
    /// 解析动画：沿 mainSeq 下的效果节点（clickEffect 起新步 / afterEffect 起新步 / withEffect 并入上一步）收集。
    /// 同时给出：
    ///   - <c>Steps</c>：每步显现的 spid 集合（供「分步模式」构造展开稿与「底图」移除形状）；
    ///   - <c>ParaSpids</c>：逐段构建（build=p）的形状 id；
    ///   - <c>Shapes</c>：形状级动画清单（几何 + 效果 + 时长/延迟），供「精灵模式」；
    ///   - <c>HasParagraphBuild</c>：出现逐段构建 → 该页不做精灵模式。
    ///   - <c>ParaEligible</c>：逐段构建的形状是否适合「行带切精灵」（均无填充无边线）；false → 该页不做精灵模式。
    /// </summary>
    private static Anim ParseAnimations(XDocument slideXml, double sldW, double sldH)
    {
        var steps = new List<List<string>>();
        var para = new HashSet<string>(StringComparer.Ordinal);
        var shapes = new List<AnimatedShape>();
        var timing = slideXml.Root?.Element(P + "timing");
        if (timing is null) return Anim.Empty;

        foreach (var b in timing.Element(P + "bldLst")?.Descendants(P + "bldP") ?? Enumerable.Empty<XElement>())
            if (string.Equals(b.Attribute("build")?.Value, "p", StringComparison.Ordinal)
                && b.Attribute("spid")?.Value is { Length: > 0 } pid)
                para.Add(pid);

        var mainSeq = timing.Descendants(P + "cTn")
            .FirstOrDefault(c => string.Equals(c.Attribute("nodeType")?.Value, "mainSeq", StringComparison.Ordinal));
        if (mainSeq is null) return Anim.Empty;

        var geometry = ShapeGeometry(slideXml, sldW, sldH);
        var paraSeq = new Dictionary<string, int>(StringComparer.Ordinal); // spid → 已出现的段序

        foreach (var ctn in mainSeq.Descendants(P + "cTn").ToList())
        {
            var nodeType = ctn.Attribute("nodeType")?.Value;
            if (nodeType != "clickEffect" && nodeType != "afterEffect" && nodeType != "withEffect") continue;
            var ids = new List<string>();
            foreach (var tg in ctn.Descendants(P + "spTgt"))
            {
                var v = tg.Attribute("spid")?.Value;
                if (!string.IsNullOrEmpty(v) && !ids.Contains(v)) ids.Add(v);
            }
            if (ids.Count == 0) continue;

            if (nodeType != "withEffect" || steps.Count == 0) steps.Add(new List<string>(ids));
            else foreach (var id in ids) if (!steps[^1].Contains(id)) steps[^1].Add(id);

            var revealStep = steps.Count; // 1 基
            var (preset, dir) = EffectName(ctn);
            var effect = ctn.Attribute("presetClass")?.Value ?? "entr";
            if (effect is not ("entr" or "exit" or "emph")) effect = "entr";
            var duration = MaxInt(ctn, P + "cTn", "dur");
            var delay = FirstInt(ctn, P + "cond", "delay");
            foreach (var id in ids)
                if (int.TryParse(id, out var spid) && geometry.TryGetValue(id, out var g))
                {
                    var paraIndex = 0;
                    if (para.Contains(id))
                    {
                        paraSeq[id] = paraSeq.GetValueOrDefault(id) + 1;
                        paraIndex = paraSeq[id];
                    }
                    shapes.Add(new AnimatedShape(spid, effect == "entr" ? revealStep : 0, revealStep, paraIndex,
                        g.X, g.Y, g.W, g.H, g.Kind, effect, preset, dir, duration, delay));
                }
        }
        var paraEligible = ParaShapesEligible(slideXml, para);
        return new Anim(steps, para, shapes, steps.Count, paraEligible);
    }

    /// <summary>逐段形状是否适合「行带切精灵」：文本框无填充、无边线（否则整块底纹会被移除，无法只切文字）。</summary>
    private static bool ParaShapesEligible(XDocument slideXml, HashSet<string> paraSpids)
    {
        if (paraSpids.Count == 0) return true;
        var spTree = slideXml.Descendants(P + "spTree").FirstOrDefault();
        if (spTree is null) return false;
        foreach (var el in spTree.Elements())
        {
            var id = ShapeId(el);
            if (id is null || !paraSpids.Contains(id)) continue;
            var spPr = el.Element(P + "spPr");
            if (spPr is null) return false; // 不确定 → 保守
            if (spPr.Elements().Any(e => e.Name == A + "solidFill" || e.Name == A + "gradFill" || e.Name == A + "pattFill" || e.Name == A + "blipFill")) return false;
            var ln = spPr.Element(A + "ln");
            if (ln is not null && ln.Elements().Any(e => e.Name == A + "solidFill" || e.Name == A + "gradFill" || e.Name == A + "pattFill")) return false;
        }
        return true;
    }

    /// <summary>效果节点的可读名 + 方向（b/l/t/r）：位移 / 淡入淡出 / 擦除 / 旋转 / 缩放 / 变色…
    /// 方向编码与预设相关：位移用位掩码（1=上 2=右 4=下 8=左），擦除看 filter 的 wipe(up|down|left|right)。</summary>
    private static (string Preset, string? Dir) EffectName(XElement ctn)
    {
        var cls = ctn.Attribute("presetClass")?.Value ?? "entr";
        var sub = int.TryParse(ctn.Attribute("presetSubtype")?.Value, out var s) ? s : 0;
        var motion = ctn.Descendants(P + "attrName").Any(n => n.Value is "ppt_x" or "ppt_y");
        if (motion)
        {
            // 位移：presetSubtype 位掩码
            var dir = sub switch { 1 => "t", 2 => "r", 4 => "b", 8 => "l", _ => (string?)null };
            return (cls == "exit" ? "flyOut" : "flyIn", dir);
        }
        var filter = ctn.Descendants(P + "animEffect").Select(a => a.Attribute("filter")?.Value).FirstOrDefault(f => !string.IsNullOrEmpty(f));
        if (filter is not null)
        {
            if (filter.StartsWith("fade", StringComparison.OrdinalIgnoreCase)) return ("fade", null);
            if (filter.StartsWith("dissolve", StringComparison.OrdinalIgnoreCase)) return ("dissolve", null);
            if (filter.StartsWith("wipe", StringComparison.OrdinalIgnoreCase))
            {
                var inner = filter.Length > 5 ? filter[5..].TrimEnd(')').Trim().ToLowerInvariant() : "";
                var d = inner switch { "up" => "t", "down" => "b", "left" => "l", "right" => "r", _ => (string?)null };
                return ("wipe", d);
            }
        }
        if (ctn.Descendants(P + "animRot").Any()) return ("spin", null);
        if (ctn.Descendants(P + "animScale").Any()) return ("pulse", null);
        if (ctn.Descendants(P + "animClr").Any()) return ("fillColor", null);
        return ("appear", null);
    }

    private static int FirstInt(XElement root, XName name, string attr)
    {
        foreach (var e in root.Descendants(name))
            if (int.TryParse(e.Attribute(attr)?.Value, out var v)) return v;
        return 0;
    }

    /// <summary>取最大整数（时长：一个效果里有 set（dur=1）与 animEffect（真时长），取最大才是真时长）。</summary>
    private static int MaxInt(XElement root, XName name, string attr)
    {
        var max = 0;
        foreach (var e in root.Descendants(name))
            if (int.TryParse(e.Attribute(attr)?.Value, out var v) && v > max) max = v;
        return max;
    }

    /// <summary>幻灯片尺寸（EMU）。缺失时退回 16:9 标准尺寸。</summary>
    private static (double W, double H) ReadSlideSize(Dictionary<string, byte[]> entries)
    {
        var presPath = FindEntry(entries, "ppt/presentation.xml");
        var sz = presPath is null ? null : LoadXml(entries, presPath)?.Root?.Element(P + "sldSz");
        var w = double.TryParse(sz?.Attribute("cx")?.Value, out var cx) ? cx : 12192000.0;
        var h = double.TryParse(sz?.Attribute("cy")?.Value, out var cy) ? cy : 6858000.0;
        return (w, h);
    }

    /// <summary>spid → 形状几何（相对幻灯片的比例）+ 类型（shape / picture / table / group）。</summary>
    private static Dictionary<string, (double X, double Y, double W, double H, string Kind)> ShapeGeometry(XDocument slideXml, double sldW, double sldH)
    {
        var map = new Dictionary<string, (double, double, double, double, string)>(StringComparer.Ordinal);
        var spTree = slideXml.Descendants(P + "spTree").FirstOrDefault();
        if (spTree is null) return map;
        foreach (var el in spTree.Elements())
        {
            var local = el.Name.LocalName;
            var kind = local switch { "sp" => "shape", "pic" => "picture", "graphicFrame" => "table", "grpSp" => "group", _ => null };
            if (kind is null) continue;
            var id = ShapeId(el);
            if (id is null) continue;
            var xfrm = el.Descendants(A + "xfrm").FirstOrDefault();
            var off = xfrm?.Element(A + "off");
            var ext = xfrm?.Element(A + "ext");
            if (off is null || ext is null) continue;
            if (!double.TryParse(off.Attribute("x")?.Value, out var x)) continue;
            if (!double.TryParse(off.Attribute("y")?.Value, out var y)) continue;
            if (!double.TryParse(ext.Attribute("cx")?.Value, out var w)) continue;
            if (!double.TryParse(ext.Attribute("cy")?.Value, out var h)) continue;
            map[id] = (x / sldW, y / sldH, w / sldW, h / sldH, kind);
        }
        return map;
    }

    // ================= 写出「播放稿」 =================

    private static string WriteDeck(string sourcePath, string outDir, Dictionary<string, byte[]> entries, List<Variant> variants)
    {
        Directory.CreateDirectory(outDir);

        var presPath = FindEntry(entries, "ppt/presentation.xml") ?? throw new InvalidOperationException("缺少 presentation.xml");
        var presRelsPath = FindEntry(entries, "ppt/_rels/presentation.xml.rels") ?? throw new InvalidOperationException("缺少 presentation rels");
        var ctPath = FindEntry(entries, "[Content_Types].xml") ?? throw new InvalidOperationException("缺少 [Content_Types].xml");

        var pres = LoadXml(entries, presPath)!;
        var presRels = LoadXml(entries, presRelsPath)!;
        var ct = LoadXml(entries, ctPath)!;
        var sldIdLst = pres.Root?.Element(P + "sldIdLst") ?? throw new InvalidOperationException("缺少 sldIdLst");

        // 锚点先快照，避免边插边枚举导致位置错位
        var anchors = sldIdLst.Elements(P + "sldId").ToList();
        var usedSldIds = anchors.Select(e => int.TryParse(e.Attribute("id")?.Value, out var v) ? v : 255).DefaultIfEmpty(255).Max();
        var relSeq = 1;
        var partSeq = 1;

        for (var i = 0; i < variants.Count; i++)
        {
            var v = variants[i];
            var stepCount = Math.Max(1, v.Steps.Count);
            if (v.Sprite)
            {
                // 精灵模式：该页只留 1 页 = 移除全部被动画形状的底图
                entries[v.OrigPath] = Serialize(BuildVariantXml(entries, v.OrigPath, v.Steps, v.Para, 0));
                continue;
            }
            if (stepCount <= 1) continue;

            var anchor = anchors.ElementAtOrDefault(i);
            for (var s = 1; s < stepCount; s++)
            {
                var variantXml = BuildVariantXml(entries, v.OrigPath, v.Steps, v.Para, s + 1); // 第 s+1 步（1 基）
                var partName = $"ppt/slides/slidePlay{partSeq}.xml";
                var relsName = $"ppt/slides/_rels/slidePlay{partSeq}.xml.rels";
                entries[partName] = Serialize(variantXml);
                if (BuildVariantRels(entries, v.OrigPath) is { } relsBytes) entries[relsName] = relsBytes;

                var rid = $"rIdPlay{relSeq++}";
                presRels.Root?.Add(new XElement(Rel + "Relationship",
                    new XAttribute("Id", rid), new XAttribute("Type", SlideRelType), new XAttribute("Target", $"slides/slidePlay{partSeq}.xml")));
                ct.Root?.Add(new XElement(Ct + "Override",
                    new XAttribute("PartName", "/" + partName), new XAttribute("ContentType", SlideContentType)));

                var sldId = new XElement(P + "sldId", new XAttribute("id", ++usedSldIds), new XAttribute(R + "id", rid));
                if (anchor is not null) anchor.AddAfterSelf(sldId); else sldIdLst.Add(sldId);
                partSeq++;
            }
            entries[v.OrigPath] = Serialize(BuildVariantXml(entries, v.OrigPath, v.Steps, v.Para, 1));
        }

        entries[presPath] = Serialize(pres);
        entries[presRelsPath] = Serialize(presRels);
        entries[ctPath] = Serialize(ct);

        var outPath = Path.Combine(outDir, "play-" + Guid.NewGuid().ToString("N")[..8] + ".pptx");
        WriteZip(entries, outPath);
        return outPath;
    }

    /// <summary>把内存里的部件集合写成一个 zip/pptx。</summary>
    private static void WriteZip(Dictionary<string, byte[]> entries, string outPath)
    {
        using var fs = File.Create(outPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var es = entry.Open();
            es.Write(bytes, 0, bytes.Length);
        }
    }

    /// <summary>生成某张幻灯片「第 stepNo 步」（1 基；0 = 一步都不显现 = 底图）的 XML：未显现的形状移除；逐段形状按已显现段数留白。</summary>
    private static XDocument BuildVariantXml(Dictionary<string, byte[]> entries, string slidePath,
        List<List<string>> steps, HashSet<string> paraSpids, int stepNo)
    {
        var doc = LoadXml(entries, slidePath)!;
        var spTree = doc.Descendants(P + "spTree").FirstOrDefault();
        if (spTree is null) return doc;

        var allTargets = new HashSet<string>(steps.SelectMany(s => s), StringComparer.Ordinal);
        if (allTargets.Count == 0) return doc;

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
