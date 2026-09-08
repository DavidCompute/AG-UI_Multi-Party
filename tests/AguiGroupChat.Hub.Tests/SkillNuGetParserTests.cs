using AguiGroupChat.SkillHosting;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>dotnet 技能正文里 #r "nuget:" 引用指令的解析（离线纯文本层；联网还原见 NuGetSkillReferenceResolver 的宿主联调）。</summary>
public sealed class SkillNuGetParserTests
{
    [Fact]
    public void Parse_ExtractsIdAndVersion_IgnoresNonDirectiveLines()
    {
        var src = """
            #r "nuget: PdfSharp, 6.1.1"
            #r "nuget: Newtonsoft.Json,13.0.3"
            using PdfSharp.Pdf;

            public static string Run(string input) {
                return input;
            }
            """;
        var refs = SkillNuGetParser.ParseReferences(src);
        Assert.Collection(refs,
            r => { Assert.Equal("PdfSharp", r.PackageId); Assert.Equal("6.1.1", r.Version); Assert.Equal(1, r.Line); },
            r => { Assert.Equal("Newtonsoft.Json", r.PackageId); Assert.Equal("13.0.3", r.Version!.Trim()); Assert.Equal(2, r.Line); });
    }

    [Fact]
    public void Parse_SupportsNoVersion_CaseInsensitive_AndCommentLinesAreIgnored()
    {
        var src = """
            // 示例：#r "nuget: 某某, 1.0.0"
            #R "NUGET:  Sample.Package "
            #r "nuget: Versioned, [1.0,2.0)"
            public static string Run(string input) { return input; }
            """;
        var refs = SkillNuGetParser.ParseReferences(src);
        Assert.Collection(refs,
            r => { Assert.Equal("Sample.Package", r.PackageId); Assert.Null(r.Version); },
            r => { Assert.Equal("Versioned", r.PackageId); Assert.Equal("[1.0,2.0)", r.Version); });
    }

    [Fact]
    public void StripDirectives_RemovesOnlyDirectiveLines_KeepsCodeAndUsings()
    {
        var src = "#r \"nuget: X, 1.0.0\"\nusing X;\n\npublic static string Run(string input)\n{\n    return input;\n}\n";
        var cleaned = SkillNuGetParser.StripDirectives(src);
        Assert.DoesNotContain("#r", cleaned);
        Assert.Contains("using X;", cleaned);
        Assert.Contains("public static string Run", cleaned);
        // 不误删正常 using / 代码
        var plain = "using System;\npublic static string Run(string input) { return \"hi\"; }\n";
        Assert.Equal(plain.Replace("\r\n", "\n"), SkillNuGetParser.StripDirectives(plain));
    }

    [Fact]
    public void Parse_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Empty(SkillNuGetParser.ParseReferences(null));
        Assert.Empty(SkillNuGetParser.ParseReferences("   "));
        Assert.Empty(SkillNuGetParser.ParseReferences("using System;\npublic static string Run(string i){return i;}\n"));
    }
}
