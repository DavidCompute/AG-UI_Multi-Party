using System.Text;
using System.Text.RegularExpressions;

namespace AguiGroupChat.SkillHosting;

/// <summary>
/// dotnet（C#）技能正文里 NuGet 引用指令的解析：支持 dotnet-script 风格
/// <c>#r "nuget: 包名, 版本"</c>（可省略版本 = 取最新稳定版）。指令行只作“声明引用”，
/// 编译前从源码剔除（Roslyn 不认 #r），由 <see cref="NuGetSkillReferenceResolver"/> 还原为真实程序集引用。
/// </summary>
public static class SkillNuGetParser
{
    /// <summary>一条解析出的 NuGet 引用。Version 可空（未指定 → 取最新稳定版）。</summary>
    public sealed record NuGetDirective(string PackageId, string? Version, int Line);

    private static readonly Regex DirectiveRegex = new(
        @"^\s*#r\s+""nuget\s*:\s*(?<id>[^,""\r\n]+?)(\s*,\s*(?<ver>[^""\r\n]+?))?""\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>解析正文里的全部 <c>#r "nuget: …"</c> 指令（按行解析，忽略大小写与多余空白）。</summary>
    public static IReadOnlyList<NuGetDirective> ParseReferences(string? source)
    {
        var result = new List<NuGetDirective>();
        if (string.IsNullOrWhiteSpace(source)) return result;
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var m = DirectiveRegex.Match(line);
            if (!m.Success) continue;
            var id = m.Groups["id"].Value.Trim();
            if (id.Length == 0) continue;
            var ver = m.Groups["ver"].Success ? m.Groups["ver"].Value.Trim() : null;
            result.Add(new NuGetDirective(id, string.IsNullOrWhiteSpace(ver) ? null : ver, i + 1));
        }
        return result;
    }

    /// <summary>剔除正文里的 <c>#r "nuget: …"</c> 指令行（其余内容逐行原样保留），得到可直接交给 Roslyn 的源码。</summary>
    public static string StripDirectives(string? source)
    {
        if (string.IsNullOrEmpty(source)) return source ?? "";
        var sb = new StringBuilder(source.Length);
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (DirectiveRegex.IsMatch(line)) continue; // 声明行不参与编译
            if (i > 0) sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString();
    }
}
