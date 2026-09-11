#r "nuget: DocumentFormat.OpenXml, 3.2.0"

// ============================================================================
// md_to_docx —— Markdown 文案排版导出 Word（.docx）
//
// 【何时使用】把已定稿的推广文案 / 方案 / 稿件（Markdown 结构）排版导出为 Word 文档交付时调用。
//   典型场景：文案推广链路收尾 —— 出稿后用户要「给我一份 Word」。
// 【能力】识别 Markdown：# 标题 / ## 二级标题 / ### 三级 / - 无序列表 / 1. 有序列表 / > 引用 /
//   ``` 代码块 / --- 分隔线；行内 **加粗** *斜体* `代码` 的标记符会被剔除（保留文字）。
// 【限制】纯排版转换，不支持图片、表格、目录（如需这些请用内置 docx_report / docx_gongwen）。
//
// 入口：public static string Run(string input) -> JSON
//   input = {
//     "markdown": "# 标题\n## 小节\n- 要点",   // 必填：Markdown 正文
//     "title": "文档标题(可选，覆盖首个 # 一级标题；也可在无 # 时作为标题)",
//     "author": "单位/作者(可选)",
//     "date": "日期(可选)",
//     "outputPath": "/app/docs/x.docx(可选，默认走平台默认目录)"
//   }
//   返回 = { ok, path, blocks, produce_file: { path, name, bytes }, message }
//
// 【注意】平台预置 using 不含 System.IO，用到 Path/Directory/File 须自行 using System.IO;
// ============================================================================

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

public class Skill
{
    // 版式：正文宋体、标题黑体，符合中文书面文档习惯
    private const string FontTitle = "黑体";
    private const string FontHeading = "黑体";
    private const string FontBody = "宋体";
    private const int SizeTitle = 36;   // 半磅：18pt
    private const int SizeH1 = 32;
    private const int SizeH2 = 28;
    private const int SizeH3 = 26;
    private const int SizeBody = 24;    // 12pt 小四

    public static string Run(string input)
    {
        try
        {
            JsonElement root;
            string markdown;
            string titleOverride;
            string author;
            string dateText;
            string requested;

            // 参数解析：优先按 JSON 对象解。
            // 容错动机：模型有时不按 JSON 传，而是把上下文里的不可信内容包装标记、
            // XML 标签或裸文本当参数（实测得到过 "<untrusted_content>" 字面量）。
            // 技能层做防御性解析，比直接报错让整条链路失败要好。
            if (TryParseJson(input, out root))
            {
                markdown = Str(root, "markdown");
                titleOverride = Str(root, "title");
                author = Str(root, "author");
                dateText = Str(root, "date");
                requested = Str(root, "outputPath");
            }
            else
            {
                markdown = ExtractMarkdownFallback(input);
                titleOverride = ""; author = ""; dateText = ""; requested = "";
            }

            if (string.IsNullOrWhiteSpace(markdown))
                return Err("缺少 markdown 字段：请把要导出的 Markdown 正文放在 markdown 字段里，"
                    + "形如 {\"markdown\":\"# 标题\\n\\n正文\"}。");

            var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            // 标题：显式 title 优先；否则取首个 "# " 行
            var title = titleOverride;
            if (string.IsNullOrWhiteSpace(title))
            {
                foreach (var l in lines)
                {
                    var t = l.Trim();
                    if (t.StartsWith("# ") && !t.StartsWith("## "))
                    {
                        title = t.Substring(2).Trim();
                        break;
                    }
                }
            }

            var path = ResolveOutputPath(requested, string.IsNullOrWhiteSpace(title) ? "未命名文档" : title);
            int blocks;

            using (var wd = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                var main = wd.AddMainDocumentPart();
                var body = new Body();
                main.Document = new Document(body);

                InstallStyles(main);

                if (!string.IsNullOrWhiteSpace(title))
                    body.AppendChild(TitlePara(title!));
                if (!string.IsNullOrWhiteSpace(author))
                    body.AppendChild(AuthorPara(author!));
                if (!string.IsNullOrWhiteSpace(dateText))
                    body.AppendChild(AuthorPara(dateText!));

                blocks = AppendMarkdown(body, lines, title);

                body.AppendChild(SectionProps());
                main.Document.Save();
            }

            var fi = new FileInfo(path);
            var json = new StringBuilder();
            json.Append("{\"ok\":true,\"path\":").Append(JsonStr(path))
                .Append(",\"blocks\":").Append(blocks)
                .Append(",\"produce_file\":{\"path\":").Append(JsonStr(path))
                .Append(",\"name\":").Append(JsonStr(fi.Name))
                .Append(",\"bytes\":").Append(fi.Length)
                .Append("},\"message\":").Append(JsonStr("已生成 Word 文档：" + path))
                .Append('}');
            return json.ToString();
        }
        catch (Exception ex)
        {
            return Err("Markdown 转 Word 失败：" + ex.GetType().Name + "：" + ex.Message);
        }
    }

    // ===== Markdown 解析与排版 =====

    /// <summary>逐行解析 Markdown 并追加段落；返回生成的块数。跳过与标题重复的首个 H1。</summary>
    private static int AppendMarkdown(Body body, string[] lines, string? title)
    {
        int blocks = 0;
        bool inCode = false;
        var codeBuf = new StringBuilder();
        bool firstH1Skipped = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.TrimEnd();

            // 代码块围栏 ```：整体收集，作为等宽段落输出
            if (line.TrimStart().StartsWith("```"))
            {
                if (!inCode) { inCode = true; codeBuf.Clear(); }
                else
                {
                    inCode = false;
                    if (codeBuf.Length > 0) { body.AppendChild(CodePara(codeBuf.ToString().TrimEnd())); blocks++; }
                }
                continue;
            }
            if (inCode) { codeBuf.AppendLine(raw); continue; }

            var t = line.Trim();
            if (t.Length == 0) continue;

            // 分隔线
            if (t == "---" || t == "***" || t == "___") { body.AppendChild(HrulePara()); blocks++; continue; }

            // 标题
            if (t.StartsWith("### ")) { body.AppendChild(HeadingPara(Inline(t.Substring(4)), 3)); blocks++; continue; }
            if (t.StartsWith("## ")) { body.AppendChild(HeadingPara(Inline(t.Substring(3)), 2)); blocks++; continue; }
            if (t.StartsWith("# "))
            {
                // 与文档标题重复的首个 H1 不重复输出（标题已单独渲染）
                if (!firstH1Skipped && !string.IsNullOrWhiteSpace(title)
                    && string.Equals(Inline(t.Substring(2)).Trim(), title!.Trim(), StringComparison.Ordinal))
                {
                    firstH1Skipped = true;
                    continue;
                }
                firstH1Skipped = true;
                body.AppendChild(HeadingPara(Inline(t.Substring(2)), 1)); blocks++; continue;
            }

            // 引用
            if (t.StartsWith("> ")) { body.AppendChild(QuotePara(Inline(t.Substring(2)))); blocks++; continue; }

            // 无序列表（- / * / +）
            if (t.Length > 1 && (t[0] == '-' || t[0] == '*' || t[0] == '+') && char.IsWhiteSpace(t[1]))
            {
                body.AppendChild(ListPara(Inline(t.Substring(2)), ordered: false)); blocks++; continue;
            }

            // 有序列表 1. / 1)
            var ol = MatchOrdered(t);
            if (ol != null) { body.AppendChild(ListPara(Inline(ol), ordered: true)); blocks++; continue; }

            // 普通段落
            body.AppendChild(BodyPara(Inline(t)));
            blocks++;
        }

        if (inCode && codeBuf.Length > 0) { body.AppendChild(CodePara(codeBuf.ToString().TrimEnd())); blocks++; }
        return blocks;
    }

    /// <summary>识别 "1. xxx" / "1) xxx"，返回内容；非有序列表返回 null。</summary>
    private static string? MatchOrdered(string t)
    {
        int j = 0;
        while (j < t.Length && char.IsDigit(t[j])) j++;
        if (j == 0 || j >= t.Length) return null;
        if (t[j] != '.' && t[j] != ')') return null;
        if (j + 1 >= t.Length || !char.IsWhiteSpace(t[j + 1])) return null;
        return t.Substring(j + 1).TrimStart();
    }

    /// <summary>剔除行内标记符（** / * / ` / _），保留文字。
    /// 用纯文本承载，避免逐段拆 run 的复杂度与出错面；中文文档里这点视觉损失可接受。</summary>
    private static string Inline(string s)
    {
        var b = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '`') continue;
            if (c == '*' || c == '_')
            {
                // 成对出现的标记符整体剔除；孤立字符（如文件名里的 _）保留
                if (i + 1 < s.Length && s[i + 1] == c) { i++; continue; }
                continue;
            }
            b.Append(c);
        }
        return b.ToString().Trim();
    }

    // ===== 段落构造 =====

    private static Paragraph TitlePara(string text) => new(
        ParaProps(JustificationValues.Center, before: 240, after: 240, spacingLine: 360, indent: false),
        new Run(RunProps(FontTitle, SizeTitle, bold: true), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph AuthorPara(string text) => new(
        ParaProps(JustificationValues.Center, before: 0, after: 120, spacingLine: 300, indent: false),
        new Run(RunProps(FontBody, SizeBody), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph HeadingPara(string text, int level)
    {
        var size = level <= 1 ? SizeH1 : level == 2 ? SizeH2 : SizeH3;
        return new Paragraph(
            ParaProps(JustificationValues.Left, before: level <= 1 ? 320 : 240, after: 120, spacingLine: 320, indent: false),
            new Run(RunProps(FontHeading, size, bold: true), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static Paragraph BodyPara(string text) => new(
        ParaProps(JustificationValues.Both, before: 0, after: 120, spacingLine: 360, indent: true),
        new Run(RunProps(FontBody, SizeBody), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph ListPara(string text, bool ordered) => new(
        ParaProps(JustificationValues.Left, before: 0, after: 80, spacingLine: 340, indent: true),
        new Run(RunProps(FontBody, SizeBody), new Text((ordered ? "" : "• ") + text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph QuotePara(string text) => new(
        ParaProps(JustificationValues.Left, before: 80, after: 120, spacingLine: 340, indent: true),
        new Run(RunProps(FontBody, SizeBody, italic: true), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph CodePara(string text) => new(
        ParaProps(JustificationValues.Left, before: 80, after: 120, spacingLine: 240, indent: false),
        new Run(RunProps("Consolas", 20), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph HrulePara() => new(
        ParaProps(JustificationValues.Center, before: 120, after: 120, spacingLine: 240, indent: false),
        new Run(new Break()));

    private static ParagraphProperties ParaProps(JustificationValues align, int before, int after, int spacingLine, bool indent)
    {
        var p = new ParagraphProperties(
            new Justification { Val = align },
            new SpacingBetweenLines { Before = before.ToString(), After = after.ToString(), Line = spacingLine.ToString(), LineRule = LineSpacingRuleValues.Auto });
        if (indent)
            p.AppendChild(new Indentation { FirstLineChars = 200 }); // 首行缩进 2 字符（中文习惯）
        return p;
    }

    private static RunProperties RunProps(string font, int size, bool bold = false, bool italic = false)
    {
        var r = new RunProperties(
            new RunFonts { Ascii = font, HighAnsi = font, EastAsia = font },
            new FontSize { Val = size.ToString() },
            new FontSizeComplexScript { Val = size.ToString() });
        if (bold) r.AppendChild(new Bold());
        if (italic) r.AppendChild(new Italic());
        return r;
    }

    private static SectionProperties SectionProps() => new(
        new PageSize { Width = 11906, Height = 16838 },              // A4 纵向
        new PageMargin { Top = 1440, Bottom = 1440, Left = 1440, Right = 1440, Header = 851, Footer = 992, Gutter = 0 });

    /// <summary>注册基础样式（Normal 默认宋体小四），保证 Word 打开不套用异常默认字体。</summary>
    private static void InstallStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();

        var normal = new Style { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true };
        normal.AppendChild(new StyleName { Val = "Normal" });
        normal.AppendChild(new StyleRunProperties(
            new RunFonts { Ascii = FontBody, HighAnsi = FontBody, EastAsia = FontBody },
            new FontSize { Val = SizeBody.ToString() },
            new FontSizeComplexScript { Val = SizeBody.ToString() }));
        styles.AppendChild(normal);

        part.Styles = styles;
        part.Styles.Save();
    }

    // ===== 输入 / 路径 / 输出辅助 =====

    private static string Str(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    /// <summary>尝试当 JSON 对象解析；非法 / 非对象返回 false。</summary>
    private static bool TryParseJson(string? input, out JsonElement root)
    {
        root = default;
        var s = (input ?? "").Trim();
        if (s.Length == 0 || (s[0] != '{' && s[0] != '[')) return false;
        try
        {
            using var doc = JsonDocument.Parse(s);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            root = doc.RootElement.Clone();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 兼底解析：参数不是合法 JSON 时，尽量从文本里救出 Markdown 正文。
    ///
    /// <para>
    /// 为什么需要：两重现实原因。
    /// 其一，模型有时不按 JSON 传参，而把上下文里的包装标记或 XML 标签当参数
    /// （实测碰到过模型直接传字面量 <c>&lt;untrusted_content&gt;</c>）。
    /// 其二，<b>编排计划路径</b>会把整段用户消息上下文（含「以下是群最近对话」与不可信边界包装）
    /// 原样投给技能，而纯排版转换只应处理用户真正要导出的那段内容。
    /// 对纯排版技能而言，把正文救出来远比报错或把聊天记录写进交付文档有价值。
    /// </para>
    ///
    /// 处理：剥掉包装标记与平台前言；若整体不是 Markdown（没有 # / ## / - / 1. 等），
    /// 把每行当普通段落交给排版（也能出一份可读的文档，而不是报错）。
    /// </summary>
    private static string ExtractMarkdownFallback(string? input)
    {
        var s = input ?? "";
        // 剥离平台注入的外部内容边界标记（原样出现时会让整段变成非法 JSON）
        s = s.Replace("<untrusted_content>", "", StringComparison.Ordinal)
             .Replace("</untrusted_content>", "", StringComparison.Ordinal);

        // 剥掉残余的简单 XML/HTML 标签（只去标签本身，保留标签内文字）；
        // 顺手还原被转义的 &lt; &gt; &amp;，避免写进文档变成乱码
        s = System.Text.RegularExpressions.Regex.Replace(s, @"</?[A-Za-z_][A-Za-z0-9_.:-]*(\s[^<>]*)?/?>", "");
        s = s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&").Replace("&quot;", "\"");

        var lines = s.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var kept = new List<string>();
        foreach (var l in lines)
        {
            var t = l.Trim();
            // 丢弃平台的边界说明行 / 对话历史前言（不是用户要导出的正文）
            if (t.StartsWith("（以上为外部来源内容", StringComparison.Ordinal)) continue;
            if (t.StartsWith("以下是群", StringComparison.Ordinal)) continue;
            if (t.StartsWith("以下是话题", StringComparison.Ordinal)) continue;
            kept.Add(l);
        }

        // 去掉首尾空行与 Markdown 代码围栏（用户常用 ``` 包住要转换的内容）
        while (kept.Count > 0 && kept[0].Trim().Length == 0) kept.RemoveAt(0);
        while (kept.Count > 0 && kept[kept.Count - 1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);
        if (kept.Count > 0 && kept[0].TrimStart().StartsWith("```", StringComparison.Ordinal)) kept.RemoveAt(0);
        if (kept.Count > 0 && kept[kept.Count - 1].Trim().StartsWith("```", StringComparison.Ordinal)) kept.RemoveAt(kept.Count - 1);

        return string.Join("\n", kept).Trim();
    }

    /// <summary>输出路径：显式 outputPath 优先；否则落默认目录（AGUI_DOCX_OUT > 用户主目录/agui-docx > 临时目录/agui-docx）。</summary>
    private static string ResolveOutputPath(string? requested, string title)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var full = Path.GetFullPath(requested!.Trim());
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            if (!full.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) full += ".docx";
            return full;
        }

        var outDir = DefaultOutputDir();
        Directory.CreateDirectory(outDir);
        return UniquePath(outDir, SafeFileNameFromTitle(title));
    }

    private static string DefaultOutputDir()
    {
        var configured = Environment.GetEnvironmentVariable("AGUI_DOCX_OUT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var d = Path.GetFullPath(configured.Trim());
                Directory.CreateDirectory(d);
                return d;
            }
            catch { /* 配置路径不可用 → 回退默认 */ }
        }
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                var d = Path.Combine(home, "agui-docx");
                Directory.CreateDirectory(d);
                return d;
            }
        }
        catch { /* 无主目录 → 回退临时目录 */ }
        var tmp = Path.Combine(Path.GetTempPath(), "agui-docx");
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    /// <summary>标题转安全文件名（保留中文）：仅剔除文件系统非法字符与控制字符，限长 80。</summary>
    private static string SafeFileNameFromTitle(string title)
    {
        var raw = (title ?? "").Trim();
        var b = new StringBuilder();
        foreach (var c in raw)
        {
            if (char.IsControl(c)) continue;
            if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
            { b.Append('_'); continue; }
            b.Append(c);
        }
        var name = b.ToString();
        var sb = new StringBuilder();
        bool lastSpace = false;
        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c)) { if (!lastSpace) sb.Append(' '); lastSpace = true; continue; }
            sb.Append(c); lastSpace = false;
        }
        var final = sb.ToString().Trim();
        if (final.Length == 0) final = "未命名文档";
        if (!final.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) final += ".docx";
        if (final.Length > 80) final = final.Substring(0, 76) + ".docx";
        return final;
    }

    /// <summary>同名不覆盖：追加 _2 / _3 …</summary>
    private static string UniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            path = Path.Combine(dir, stem + "_" + i + ext);
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(dir, stem + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ext);
    }

    private static string JsonStr(string s)
    {
        var b = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                default:
                    if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
                    else b.Append(c);
                    break;
            }
        }
        return b.Append('"').ToString();
    }

    private static string Err(string msg) => "{\"ok\":false,\"error\":" + JsonStr(msg) + "}";
}
